using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;

namespace FF5MenuSprites
{
    /// <summary>
    /// Faster save/load list (MenuLayout "fastSaveList", default true, live).
    /// Why [verified, profileGame log 2026-11-16]: after 'Load Game' one frame takes ~1.2 s, with 4-14 PNG decodes of
    /// 55-63 ms each, called by the game's own code; same wait without this plugin. Source [inferred, dump]: each save slot
    /// holds a screenshot (Last.Camera.FrontRenderTarget.CreateSaveScreenShot) that the slot list decodes in
    /// Last.UI.KeyInput.SaveContentController.SetScreenShot(String).
    /// What this does: the game's SetScreenShot call is held back; the screenshot (base64 PNG or a file path) is decoded on a
    /// worker thread (PNG + mip chain); then the game's own call is replayed and the LoadImage inside it gets the ready pixels
    /// (Resize + raw upload, no decode). Anything unexpected (other PNG kinds, no LoadImage inside the call) -> the game's
    /// own way, logged with [SaveList].
    /// </summary>
    static class SaveShots
    {
        internal static bool Enabled = true;

        sealed class Pix { public int W, H, Levels, Level0Len; public byte[] Data; public string Kind; public double DecodeMs; }
        sealed class Job { public IntPtr K; public Last.UI.KeyInput.SaveContentController C; public string S; public int Token; public Pix P; public bool Done; }

        static readonly Dictionary<IntPtr, Job> pending = new Dictionary<IntPtr, Job>();
        static readonly ConcurrentQueue<Job> finished = new ConcurrentQueue<Job>();
        static int token, logs;
        static bool replaying, off;
        static Pix current;           // set only while a replay runs on the main thread
        static bool used;             // the replay's LoadImage took the pixels
        // per-burst numbers for the log
        static int nShots, nFallback; static double sumDecode, sumUpload, worstUpload; static string sizes = "";
        // split of each replay (diagnostic): time before our upload, the upload itself, after it; LoadImage calls inside
        static long tTake0, tTake1; static int loadCalls;
        static double sumPre, sumTake, sumPost; static int sumLoads;
        static readonly Stopwatch burst = new Stopwatch();
        // Screenshots decoded before (2026-11-25, user: slot pictures flicker with fastSaveList in FF5): the game calls
        // SetScreenShot again as the list refreshes; holding each call back showed the old picture for 100-200+ ms
        // [inferred from the code + repeated 1-shot bursts in the logs]. A picture decoded before is now set at once,
        // inside the game's own call, with the ready pixels (no hold-back). Kept: newest pictures up to ReadyCap bytes.
        static readonly Dictionary<string, Pix> ready = new Dictionary<string, Pix>();
        static readonly List<string> readyOrder = new List<string>();
        static long readyBytes;
        const long ReadyCap = 160L * 1024 * 1024;
        static bool syncRun;
        static int nSync;

        static void Remember(string s, Pix p)
        {
            if (p == null || ready.ContainsKey(s)) return;
            ready[s] = p; readyOrder.Add(s); readyBytes += p.Data.Length;
            while (readyBytes > ReadyCap && readyOrder.Count > 1)
            {
                var old = readyOrder[0]; readyOrder.RemoveAt(0);
                if (ready.TryGetValue(old, out var op)) { readyBytes -= op.Data.Length; ready.Remove(old); }
            }
        }

        internal static void Install(HarmonyLib.Harmony h)
        {
            try
            {
                const System.Reflection.BindingFlags SA = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
                var set = HarmonyLib.AccessTools.Method(typeof(Last.UI.KeyInput.SaveContentController), "SetScreenShot", new[] { typeof(string) });
                h.Patch(set, prefix: new HarmonyLib.HarmonyMethod(typeof(SaveShots).GetMethod(nameof(SetPrefix), SA)),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(SaveShots).GetMethod(nameof(SetPostfix), SA)));
                int n = 0;
                foreach (var m in typeof(ImageConversion).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public))
                {
                    if (m.Name != "LoadImage") continue;
                    var ps = m.GetParameters();
                    string pre = ps.Length == 3 ? nameof(LoadPrefix3) : ps.Length == 2 ? nameof(LoadPrefix2) : null;
                    if (pre == null) continue;
                    h.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(SaveShots).GetMethod(pre, SA)) { priority = HarmonyLib.Priority.First });
                    n++;
                }
                // FF1: Sprite.Create of the shot takes ~920 ms (2560x1440) / ~350 ms (320x180) with the default tight
                // mesh [verified, profileGame log 2026-11-25]; a screenshot is an opaque rectangle, so FullRect looks the same.
                int nc = 0;
                foreach (var m in typeof(Sprite).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public))
                {
                    if (m.Name != "Create") continue;
                    bool has = false;
                    foreach (var pi in m.GetParameters()) if (pi.Name == "meshType" && pi.ParameterType == typeof(SpriteMeshType)) has = true;
                    if (!has) continue;
                    h.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(SaveShots).GetMethod(nameof(CreatePrefix), SA)));
                    nc++;
                }
                Plugin.L.LogInfo($"[SaveList] hooked SetScreenShot + {n} LoadImage overloads + {nc} Sprite.Create overloads");
            }
            catch (Exception e) { off = true; Plugin.L.LogWarning("[SaveList] could not hook (the game's own way stays): " + e.Message); }
        }

        // ---- game call held back -------------------------------------------------------------------------------------
        static bool SetPrefix(Last.UI.KeyInput.SaveContentController __instance, string __0)
        {
            if (replaying || off || !Enabled) return true;
            try
            {
                if (__instance == null || string.IsNullOrEmpty(__0) || __0.Length < 64) return true;
                if (ready.TryGetValue(__0, out var rp))
                {
                    // Decoded before: let the game's call run now with the ready pixels (no hold-back, no flicker).
                    pending.Remove(__instance.Pointer);   // an older held-back call for this slot must not land after it
                    current = rp; used = false; replaying = true; syncRun = true; nSync++;
                    return true;
                }
                if (!burst.IsRunning) { burst.Restart(); nShots = nFallback = 0; sumDecode = sumUpload = worstUpload = 0; sizes = ""; sumPre = sumTake = sumPost = 0; sumLoads = 0; }
                var job = new Job { K = __instance.Pointer, C = __instance, S = __0, Token = ++token };
                pending[job.K] = job;            // newest call per slot wins
                Task.Run(() =>
                {
                    try { job.P = DecodeAny(job.S); } catch (Exception) { job.P = null; }
                    job.Done = true;
                    finished.Enqueue(job);
                });
                return false;
            }
            catch (Exception e) { Plugin.L.LogWarning("[SaveList] " + e.Message); return true; }
        }

        static int syncLogs;
        static void SetPostfix()
        {
            if (!syncRun) return;
            if (syncLogs < 5) { syncLogs++; Plugin.L.LogWarning($"[SaveList] picture decoded before: set at once, no hold-back (pixels used: {used}) (info)"); }
            syncRun = false; replaying = false; current = null;
        }

        /// <summary>Main thread, every frame: replay the game's call for finished decodes (about 12 ms of uploads per frame).</summary>
        internal static void Tick()
        {
            if (finished.IsEmpty && pending.Count == 0) return;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalMilliseconds < 12 && finished.TryDequeue(out var job))
            {
                if (!pending.TryGetValue(job.K, out var cur) || cur.Token != job.Token) continue;   // a newer call replaced it
                pending.Remove(job.K);
                if (job.C == null || job.C.WasCollected) continue;                                         // list closed
                current = job.P; used = false; tTake0 = tTake1 = 0; loadCalls = 0;
                var t0 = Stopwatch.GetTimestamp();
                replaying = true;
                try { job.C.SetScreenShot(job.S); }
                catch (Exception e) { Plugin.L.LogWarning("[SaveList] replay: " + e.Message); }
                finally { replaying = false; current = null; }
                long t1 = Stopwatch.GetTimestamp();
                double ms = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                if (job.P != null && used)
                {
                    double f = 1000.0 / Stopwatch.Frequency;
                    sumPre += (tTake0 - t0) * f; sumTake += (tTake1 - tTake0) * f; sumPost += (t1 - tTake1) * f; sumLoads += loadCalls;
                    nShots++; sumDecode += job.P.DecodeMs; sumUpload += ms; worstUpload = Math.Max(worstUpload, ms);
                    Remember(job.S, job.P);
                    string sz = $"{job.P.W}x{job.P.H} {job.P.Kind}";
                    if (!sizes.Contains(sz)) sizes += (sizes.Length > 0 ? ", " : "") + sz;
                }
                else
                {
                    nFallback++;
                    if (job.P != null && !used && logs < 10)
                    {
                        logs++;
                        Plugin.L.LogWarning("[SaveList] the game's SetScreenShot did not decode right away (no LoadImage inside); fastSaveList off for this session (info)");
                        off = true;
                    }
                }
            }
            if (pending.Count == 0 && finished.IsEmpty && burst.IsRunning)
            {
                burst.Stop();
                if (logs < 10 && (nShots + nFallback) > 0)
                {
                    logs++;
                    Plugin.L.LogWarning($"[SaveList] {nShots} screenshots ready {burst.Elapsed.TotalMilliseconds:0} ms after the list opened ({sizes}); " +
                                        $"decode on a worker {(nShots > 0 ? sumDecode / nShots : 0):0} ms each, main-thread replay {(nShots > 0 ? sumUpload / nShots : 0):0.0} ms each " +
                                        $"(worst {worstUpload:0.0}); {nFallback} done the game's way; replay split per shot: before upload " +
                                        $"{(nShots > 0 ? sumPre / nShots : 0):0.0} ms, upload {(nShots > 0 ? sumTake / nShots : 0):0.0} ms, after {(nShots > 0 ? sumPost / nShots : 0):0.0} ms, " +
                                        $"LoadImage calls {(nShots > 0 ? (double)sumLoads / nShots : 0):0.#}; sprites switched to a full-rectangle mesh {fullRectCount}; " +
                                        $"pictures set at once from memory since the last line {nSync} ({ready.Count} kept, {readyBytes / (1024 * 1024)} MB) (info)");
                    fullRectCount = 0; nSync = 0;
                }
            }
        }

        // ---- Sprite.Create inside the replay: full-rectangle mesh (no tight outline tracing) -----------------------------
        static int fullRectCount;
        static void CreatePrefix(ref SpriteMeshType meshType)
        {
            if (!replaying || meshType == SpriteMeshType.FullRect) return;
            meshType = SpriteMeshType.FullRect;
            fullRectCount++;
        }

        // ---- LoadImage inside the replay: hand over the ready pixels ----------------------------------------------------
        static bool LoadPrefix2(Texture2D __0, ref bool __result) => Take(__0, false, ref __result);
        static bool LoadPrefix3(Texture2D __0, bool __2, ref bool __result) => Take(__0, __2, ref __result);

        static bool Take(Texture2D tex, bool nonReadable, ref bool result)
        {
            if (replaying) loadCalls++;
            var p = current;
            if (p == null || tex == null) return true;
            current = null;                                   // one image per replay
            tTake0 = Stopwatch.GetTimestamp();
            try { return TakeInner(tex, nonReadable, ref result, p); }
            finally { tTake1 = Stopwatch.GetTimestamp(); }
        }

        static bool TakeInner(Texture2D tex, bool nonReadable, ref bool result, Pix p)
        {
            try
            {
                bool mips = tex.mipmapCount > 1;
                if (!tex.Resize(p.W, p.H, TextureFormat.RGBA32, mips)) return true;
                int len;
                if (!mips) len = p.Level0Len;
                else if (tex.mipmapCount == p.Levels) len = p.Data.Length;
                else
                {
                    if (logs < 10) { logs++; Plugin.L.LogWarning($"[SaveList] mip count {tex.mipmapCount} != {p.Levels}; the game's way (info)"); }
                    return true;
                }
                var hnd = GCHandle.Alloc(p.Data, GCHandleType.Pinned);
                try { tex.LoadRawTextureData(hnd.AddrOfPinnedObject(), len); }
                finally { hnd.Free(); }
                tex.Apply(false, nonReadable);
                used = true; result = true;
                return false;
            }
            catch (Exception e)
            {
                if (logs < 10) { logs++; Plugin.L.LogWarning("[SaveList] upload failed, the game's way: " + e.Message); }
                return true;                                  // the original LoadImage decodes as usual
            }
        }

        // ---- worker thread: no Unity / Il2Cpp calls below ----------------------------------------------------------------
        static Pix DecodeAny(string s)
        {
            var t0 = Stopwatch.GetTimestamp();
            byte[] file = null; string kind = null;
            string t = s.Trim();
            if (t.Length < 1024 && t.IndexOfAny(Path.GetInvalidPathChars()) < 0 && File.Exists(t)) { file = File.ReadAllBytes(t); kind = "file"; }
            else
            {
                int comma = t.StartsWith("data:", StringComparison.Ordinal) ? t.IndexOf(',') : -1;
                try { file = Convert.FromBase64String(comma >= 0 ? t.Substring(comma + 1) : t); kind = "base64"; } catch (FormatException) { return null; }
            }
            var p = DecodePng(file);
            if (p == null) return null;
            p.Kind = kind;
            p.DecodeMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            return p;
        }

        static int BE(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];

        /// <summary>8-bit RGB / RGBA, not interlaced; anything else -> null (the game's own decoder). Output RGBA32,
        /// bottom row first (Unity order), with a 2x2 box-filtered mip chain after level 0.</summary>
        static Pix DecodePng(byte[] f)
        {
            if (f == null || f.Length < 45 || f[0] != 0x89 || f[1] != 0x50 || f[2] != 0x4E || f[3] != 0x47) return null;
            int pos = 8, w = 0, h = 0, bd = 0, ct = -1, il = 0;
            var idat = new MemoryStream();
            while (pos + 8 <= f.Length)
            {
                int len = BE(f, pos), d = pos + 8;
                if (len < 0 || d + len > f.Length) return null;
                char c0 = (char)f[pos + 4], c1 = (char)f[pos + 5], c2 = (char)f[pos + 6], c3 = (char)f[pos + 7];
                if (c0 == 'I' && c1 == 'H' && c2 == 'D' && c3 == 'R') { w = BE(f, d); h = BE(f, d + 4); bd = f[d + 8]; ct = f[d + 9]; il = f[d + 12]; }
                else if (c0 == 'I' && c1 == 'D' && c2 == 'A' && c3 == 'T') idat.Write(f, d, len);
                else if (c0 == 'I' && c1 == 'E' && c2 == 'N' && c3 == 'D') break;
                pos = d + len + 4;
            }
            if (bd != 8 || il != 0 || (ct != 2 && ct != 6) || w <= 0 || h <= 0 || w > 8192 || h > 8192) return null;
            int bpp = ct == 6 ? 4 : 3, stride = w * bpp;
            var raw = new byte[(stride + 1) * h];
            idat.Position = 0;
            using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            {
                int r = 0;
                while (r < raw.Length) { int n = z.Read(raw, r, raw.Length - r); if (n <= 0) break; r += n; }
                if (r < raw.Length) return null;
            }
            // mip chain sizes (Unity: levels down to 1x1)
            int levels = 1, total = w * h * 4;
            for (int lw = w, lh = h; lw > 1 || lh > 1; levels++) { lw = Math.Max(1, lw >> 1); lh = Math.Max(1, lh >> 1); total += lw * lh * 4; }
            var o = new byte[total];
            var prev = new byte[stride]; var cur = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                int src = y * (stride + 1), ft = raw[src];
                Buffer.BlockCopy(raw, src + 1, cur, 0, stride);
                switch (ft)
                {
                    case 0: break;
                    case 1: for (int i = bpp; i < stride; i++) cur[i] = (byte)(cur[i] + cur[i - bpp]); break;
                    case 2: for (int i = 0; i < stride; i++) cur[i] = (byte)(cur[i] + prev[i]); break;
                    case 3:
                        for (int i = 0; i < stride; i++) cur[i] = (byte)(cur[i] + (((i >= bpp ? cur[i - bpp] : 0) + prev[i]) >> 1));
                        break;
                    case 4:
                        for (int i = 0; i < stride; i++)
                        {
                            int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                            int pp = a + b - c, pa = Math.Abs(pp - a), pb = Math.Abs(pp - b), pc = Math.Abs(pp - c);
                            cur[i] = (byte)(cur[i] + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c));
                        }
                        break;
                    default: return null;
                }
                int dst = (h - 1 - y) * w * 4;
                if (bpp == 4) Buffer.BlockCopy(cur, 0, o, dst, stride);
                else for (int x = 0, s = 0; x < w; x++, s += 3) { o[dst++] = cur[s]; o[dst++] = cur[s + 1]; o[dst++] = cur[s + 2]; o[dst++] = 255; }
                var tmp = prev; prev = cur; cur = tmp;
            }
            // mips
            int sOff = 0, sw = w, sh = h, dOff = w * h * 4;
            for (int l = 1; l < levels; l++)
            {
                int dw = Math.Max(1, sw >> 1), dh = Math.Max(1, sh >> 1);
                for (int y = 0; y < dh; y++)
                {
                    int y0 = Math.Min(2 * y, sh - 1), y1 = Math.Min(2 * y + 1, sh - 1);
                    for (int x = 0; x < dw; x++)
                    {
                        int x0 = Math.Min(2 * x, sw - 1), x1 = Math.Min(2 * x + 1, sw - 1);
                        int a = sOff + (y0 * sw + x0) * 4, b = sOff + (y0 * sw + x1) * 4, c = sOff + (y1 * sw + x0) * 4, d = sOff + (y1 * sw + x1) * 4;
                        int q = dOff + (y * dw + x) * 4;
                        for (int k = 0; k < 4; k++) o[q + k] = (byte)((o[a + k] + o[b + k] + o[c + k] + o[d + k] + 2) >> 2);
                    }
                }
                sOff = dOff; dOff += dw * dh * 4; sw = dw; sh = dh;
            }
            return new Pix { W = w, H = h, Levels = levels, Level0Len = w * h * 4, Data = o };
        }
    }
}
