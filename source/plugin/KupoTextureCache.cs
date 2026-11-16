using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FF5MenuSprites
{
    /// <summary>
    /// Speeds up KupoUI's texture swaps (MenuLayout "kupoTextureCache": true, live).
    /// Why [verified, decompiled KupoUI.PR 2026-11-06]: KupoUI.PR.Textures.TextureResolver.TryReplaceTextureInPlace
    /// (Texture2D originalTexture, string textureName, string assetAddressHint) reads the PNG from disk, LoadImage()s it
    /// into the game's texture and Apply()s it, once per texture *instance*. The game reloads its UI textures with every
    /// menu/prefab load, so the same files are read, decoded and uploaded again each time (measured: ~0.4 s per return
    /// to the title, ~0.95 s on the first bestiary open).
    /// What this does: the first time a file is swapped (KupoUI does the work), a GPU copy of the result is kept. Later
    /// instances of the same texture (same name + address) are resized to match and filled with Graphics.CopyTexture
    /// (GPU to GPU: no disk read, no decode, no mipmap build, no upload), and marked done in KupoUI's own
    /// InPlaceProcessedTextureIds. Anything that fails falls back to KupoUI's normal swap for that texture (logged).
    /// Limits [inferred]: the filled texture has no CPU-side copy of the new pixels (reading pixels from it in a script
    /// would return old data); the cache holds one extra GPU copy per replaced file.
    /// </summary>
    static class KupoTextureCache
    {
        internal static bool Enabled;

        static readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>();
        static readonly HashSet<string> failed = new HashSet<string>();
        static HashSet<int> kupoDone;          // KupoUI's InPlaceProcessedTextureIds (same object)
        static HarmonyLib.Harmony harmony;
        static bool hooked, supported, supportChecked;
        static int hits, stored, fails, warned;
        static long bytes;
        static int nextReport = 500;
        static int nBattleSkip;
        static bool battleSkipLogged;

        internal static void Install(HarmonyLib.Harmony h)
        {
            harmony = h;
            if (!TryHook()) AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }

        static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs e)
        {
            try
            {
                if (e.LoadedAssembly.GetName().Name != "KupoUI.PR") return;
                if (TryHook()) AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            }
            catch (Exception ex) { Plugin.L.LogWarning("[KupoCache] hook failed: " + ex.Message); }
        }

        static bool TryHook()
        {
            if (hooked) return true;
            Type t = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "KupoUI.PR") { t = a.GetType("KupoUI.PR.Textures.TextureResolver"); break; }
            if (t == null) return false;
            hooked = true;
            try
            {
                var m = t.GetMethod("TryReplaceTextureInPlace", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(Texture2D), typeof(string), typeof(string) }, null);
                var f = t.GetField("InPlaceProcessedTextureIds", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                kupoDone = f?.GetValue(null) as HashSet<int>;
                if (m == null || kupoDone == null)
                {
                    Plugin.L.LogWarning("[KupoCache] KupoUI's texture swap not found (other KupoUI version?); cache off");
                    return true;
                }
                harmony.Patch(m,
                    prefix: new HarmonyLib.HarmonyMethod(typeof(KupoTextureCache).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)) { priority = HarmonyLib.Priority.High },
                    postfix: new HarmonyLib.HarmonyMethod(typeof(KupoTextureCache).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.L.LogInfo("[KupoCache] hooked KupoUI TryReplaceTextureInPlace");
                // GPU shadows (2026-11-14): remember which file each KupoUI texture came from, so a depth map beside it
                // can be found (battle sprites all share names like Default_00). KupoUI makes battle sprites with
                // LoadTexture(name, address, out metadata) -> new texture "<name>_Custom" [verified, decompiled
                // KupoUI.PR TextureResolver.cs:454, :682]; in-place swaps go through TryReplaceTextureInPlace.
                try
                {
                    const BindingFlags SA = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                    harmony.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(KupoTextureCache).GetMethod(nameof(InPlacePath), SA)));
                    var lt = t.GetMethod("LoadTexture", SA);
                    if (lt != null) harmony.Patch(lt, postfix: new HarmonyLib.HarmonyMethod(typeof(KupoTextureCache).GetMethod(nameof(LoadPath), SA)));
                    Plugin.L.LogWarning($"[KupoCache] texture-to-file record on (LoadTexture {(lt != null ? "hooked" : "not found")}) (info, not an error)");
                }
                catch (Exception e) { Plugin.L.LogWarning("[KupoCache] texture-to-file record off (depth maps not found): " + e.Message); }
            }
            catch (Exception e) { Plugin.L.LogWarning("[KupoCache] could not hook KupoUI: " + e.Message); }
            return true;
        }

        /// <summary>Texture instance id -> the file KupoUI loaded it from (resolved the way KupoUI does: address first, then name).</summary>
        internal static readonly Dictionary<int, string> TexFile = new Dictionary<int, string>();
        static int pathFails;
        static void LoadPath(string __0, string __1, Texture2D __result) { if (__result != null) NotePath(__result, __0, __1, false); }
        static void InPlacePath(Texture2D __0, string __1, string __2) { if (__0 != null) NotePath(__0, __1, __2, true); }
        static void NotePath(Texture2D t, string name, string address, bool normalize)
        {
            try
            {
                if (string.IsNullOrEmpty(name) || !FastSetup()) return;
                string path = null;
                object[] a = { address, null };
                if (!string.IsNullOrEmpty(address) && (bool)mByAddress.Invoke(null, a)) path = (string)a[1];
                else
                {
                    string n = normalize ? (string)mNormalize.Invoke(null, new object[] { name }) : name;
                    object[] b = { n, null };
                    if ((bool)mByName.Invoke(null, b)) path = (string)b[1];
                }
                if (path != null) TexFile[t.GetInstanceID()] = path;
            }
            catch (Exception e) { if (pathFails++ < 3) Plugin.L.LogWarning("[KupoCache] texture-to-file record failed: " + e.Message); }
        }

        static bool Supported()
        {
            if (supportChecked) return supported;
            supportChecked = true;
            try { supported = (int)SystemInfo.copyTextureSupport != 0; } catch { supported = false; }
            Plugin.L.LogWarning($"[KupoCache] GPU texture copy {(supported ? "supported" : "NOT supported: cache stays off")} (info, not an error)");
            return supported;
        }

        // __0 = originalTexture, __1 = textureName, __2 = assetAddressHint (KupoUI's parameter order).
        // Diagnostics (test 2026-11-06: no reuse seen in the timings): why each call went the way it did.
        static int nCalls, nOff, nDone, nFailedKey, nNew, nDead, nNotSwapped, keysLogged, deadLogged;
        // Non-readable game textures: KupoUI reads + decodes the file, then fails on them (inferred from the counts:
        // ~495 decodes per load with almost no successful swap). Counted, and skipped when SkipUnreadable is on.
        internal static bool SkipUnreadable;
        static int nUnreadIn, nUnreadSwapped, nUnreadSkipped;
        static bool lastUnreadable;
        static float nextStats;

        static void Stats()
        {
            if (!Perf.Enabled && !Rules.Log) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextStats) return;
            nextStats = now + 10f;
            Plugin.L.LogWarning($"[KupoCache] calls {nCalls}: off {nOff}, battle sprites left to KupoUI {nBattleSkip}, already done {nDone}, failed key {nFailedKey}, first time {nNew}, cached copy gone {nDead}, KupoUI did not swap {nNotSwapped}, reused {hits}; kept {stored} (~{bytes / (1024 * 1024)} MB), fell back {fails} | non-readable: seen {nUnreadIn}, KupoUI swapped {nUnreadSwapped}, skipped {nUnreadSkipped} (skip {(SkipUnreadable ? "on" : "off")}), fast path {nFast} (failed {nFastFail}, files in memory {fileBytes.Count}, {fileBytesTotal / (1024 * 1024)} MB, {(FastUnreadable ? "on" : "off")}) (info, not an error)");
        }

        static bool Prefix(Texture2D __0, string __1, string __2, ref bool __result, out string __state)
        {
            __state = null;
            try
            {
                nCalls++;
                Stats();
                if (!Enabled || __0 == null || string.IsNullOrEmpty(__1) || !Supported()) { nOff++; return true; }
                // Battle character poses go to KupoUI untouched: with the cache, a party member with a full replaced
                // pose set fell back to the game's own sprite after taking damage; with the cache off it did not
                // [verified once, user 2026-11-14; the cause inside the cache is not known].
                if (__2 != null && __2.IndexOf("/Chara/Battle/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    nBattleSkip++;
                    if (!battleSkipLogged) { battleSkipLogged = true; Plugin.L.LogWarning("[KupoCache] battle character sprites are left to KupoUI's own swap (info, not an error)"); }
                    return true;
                }
                int id = __0.GetInstanceID();
                if (kupoDone.Contains(id)) { nDone++; return true; }  // KupoUI returns false at once
                lastUnreadable = !__0.isReadable;
                if (lastUnreadable) nUnreadIn++;
                string key = __1 + "|" + (__2 ?? "");
                if (failed.Contains(key)) { nFailedKey++; return true; }
                bool had = cache.TryGetValue(key, out var c);
                if (!had || c == null)
                {
                    if (had) { nDead++; cache.Remove(key); if (deadLogged++ < 5) Plugin.L.LogWarning($"[KupoCache] cached copy of '{key}' was destroyed (info)"); }
                    else nNew++;
                    // No cached copy: a non-readable texture is skipped when the switch is on (cached ones are still filled).
                    if (lastUnreadable && SkipUnreadable) { nUnreadSkipped++; __result = false; return false; }
                    // Non-readable: do what KupoUI does, minus the disk read and the Apply that always throws.
                    if (lastUnreadable && FastUnreadable && TryFast(__0, __1, __2, id)) { __result = false; return false; }
                    __state = key;
                    return true;                                         // KupoUI swaps, Postfix keeps a copy
                }
                try
                {
                    if (__0.width != c.width || __0.height != c.height || __0.format != c.format || __0.mipmapCount != c.mipmapCount)
                    {
                        if (!__0.Resize(c.width, c.height, c.format, c.mipmapCount > 1)) throw new Exception("Resize returned false");
                        if (__0.mipmapCount != c.mipmapCount) throw new Exception($"mip count {__0.mipmapCount} vs {c.mipmapCount}");
                        // Resize only changes the CPU side; the GPU texture is rebuilt (blank, white) at the next upload,
                        // which wiped the copy (test 2026-11-06: title background and window frames white). Upload first.
                        __0.Apply(false, false);
                    }
                    Graphics.CopyTexture(c, __0);
                    __0.filterMode = c.filterMode;
                    __0.wrapModeU = c.wrapModeU;
                    __0.wrapModeV = c.wrapModeV;
                    __0.name = c.name;
                    kupoDone.Add(id);
                    __result = true;
                    hits++;
                    if (hits >= nextReport) { nextReport += 500; Report(); }
                    return false;
                }
                catch (Exception e)
                {
                    Fail(key, "fill", e.Message);
                    cache.Remove(key);
                    try { UnityEngine.Object.Destroy(c); } catch { }
                    return true;                                         // KupoUI does it the normal way
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("[KupoCache] prefix error, KupoUI handles it: " + e.Message); return true; }
        }

        static void Postfix(Texture2D __0, bool __result, string __state)
        {
            if (__state == null) return;
            if (!__result) { nNotSwapped++; return; }
            if (lastUnreadable) nUnreadSwapped++;
            if (__0 == null || cache.ContainsKey(__state)) return;
            try
            {
                if (keysLogged++ < 5) Plugin.L.LogWarning($"[KupoCache] keeping '{__state}' {__0.width}x{__0.height} {__0.format} mips {__0.mipmapCount} (info)");
                bool mips = __0.mipmapCount > 1;
                var c = new Texture2D(__0.width, __0.height, __0.format, mips);
                if (c.mipmapCount != __0.mipmapCount) { UnityEngine.Object.Destroy(c); Fail(__state, "store", $"mip count {__0.mipmapCount} vs {c.mipmapCount}"); return; }
                c.Apply(false, true);                                    // drop our CPU copy before the GPU copy lands
                Graphics.CopyTexture(__0, c);
                c.filterMode = __0.filterMode;
                c.wrapModeU = __0.wrapModeU;
                c.wrapModeV = __0.wrapModeV;
                c.name = __0.name;
                c.hideFlags = HideFlags.DontUnloadUnusedAsset;
                cache[__state] = c;
                stored++;
                bytes += (long)__0.width * __0.height * 4 * (mips ? 4 : 3) / 3;
            }
            catch (Exception e) { Fail(__state, "store", e.Message); }
        }

        // ---- Fast path for non-readable game textures (test 2026-11-06) ----
        // KupoUI's in-place swap on these [decompiled]: path lookup, LoadTextureMetadata, File.ReadAllBytes, LoadImage
        // (takes effect on screen, user screenshots), filterMode + ApplyWrapMode, then Apply(true, false) throws "not
        // readable" → caught, id added to SkippedTextureIds, returns false. Here: same lookup and calls through KupoUI's
        // own helpers, file bytes kept in memory, no Apply, same result (false + SkippedTextureIds).
        internal static bool FastUnreadable;
        static int nFast, nFastFail;
        static bool fastReady, fastBroken;
        static MethodInfo mNormalize, mByAddress, mByName, mMeta, mFilter, mWrap;
        static FieldInfo fInitialized;
        static HashSet<int> kupoSkipped;
        static readonly Dictionary<string, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>> fileBytes =
            new Dictionary<string, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>>(StringComparer.OrdinalIgnoreCase);
        static long fileBytesTotal;
        const long FileBytesCap = 512L * 1024 * 1024;
        static readonly HashSet<string> fastNo = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static bool FastSetup()
        {
            if (fastReady) return true;
            if (fastBroken) return false;
            try
            {
                Type t = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "KupoUI.PR") { t = a.GetType("KupoUI.PR.Textures.TextureResolver"); break; }
                const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                mNormalize = t?.GetMethod("NormalizeName", S);
                mByAddress = t?.GetMethod("TryGetFilePathByAddress", S);
                mByName = t?.GetMethod("TryGetFilePathByNormalizedName", S);
                mMeta = t?.GetMethod("LoadTextureMetadata", S);
                mFilter = t?.GetMethod("ResolveFilterMode", S);
                mWrap = t?.GetMethod("ApplyWrapMode", S, null, new[] { typeof(Texture2D), typeof(string), mMeta?.ReturnType }, null);
                fInitialized = t?.GetField("_initialized", S);
                kupoSkipped = t?.GetField("SkippedTextureIds", S)?.GetValue(null) as HashSet<int>;
                if (mNormalize == null || mByAddress == null || mByName == null || mMeta == null || mFilter == null || mWrap == null || fInitialized == null || kupoSkipped == null)
                {
                    fastBroken = true;
                    Plugin.L.LogWarning("[KupoCache] fast path off: KupoUI helpers not found (other KupoUI version?)");
                    return false;
                }
                fastReady = true;
                return true;
            }
            catch (Exception e) { fastBroken = true; Plugin.L.LogWarning("[KupoCache] fast path off: " + e.Message); return false; }
        }

        static bool TryFast(Texture2D tex, string name, string address, int id)
        {
            if (!FastSetup()) return false;
            string path = null;
            try
            {
                if (!(bool)fInitialized.GetValue(null)) return false;
                if (kupoSkipped.Contains(id)) return false;
                string norm = (string)mNormalize.Invoke(null, new object[] { name });
                object[] a = { address, null };
                if (!string.IsNullOrEmpty(address) && (bool)mByAddress.Invoke(null, a)) path = (string)a[1];
                else
                {
                    object[] b = { norm, null };
                    if (!(bool)mByName.Invoke(null, b)) return false;     // no file: KupoUI's own miss handling (cheap)
                    path = (string)b[1];
                }
                if (string.IsNullOrEmpty(path) || fastNo.Contains(path)) return false;
                if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) { fastNo.Add(path); return false; }
                object meta = mMeta.Invoke(null, new object[] { path });
                if (!fileBytes.TryGetValue(path, out var data) || data == null)
                {
                    var raw = System.IO.File.ReadAllBytes(path);
                    data = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>(raw);
                    if (fileBytesTotal + raw.Length <= FileBytesCap) { fileBytes[path] = data; fileBytesTotal += raw.Length; }
                }
                if (!ImageConversion.LoadImage(tex, data)) { kupoSkipped.Add(id); nFast++; return true; }   // KupoUI: return false
                tex.filterMode = (FilterMode)mFilter.Invoke(null, new object[] { path, meta });
                mWrap.Invoke(null, new object[] { tex, path, meta });
                kupoSkipped.Add(id);
                nFast++;
                return true;
            }
            catch (Exception e)
            {
                nFastFail++;
                if (path != null) fastNo.Add(path);
                if (warned++ < 10) Plugin.L.LogWarning($"[KupoCache] fast path failed for '{name}', KupoUI handles it: {(e.InnerException ?? e).Message}");
                return false;
            }
        }

        static void Fail(string key, string step, string msg)
        {
            failed.Add(key);
            fails++;
            if (warned++ < 10) Plugin.L.LogWarning($"[KupoCache] '{key}' {step} failed, KupoUI swaps it the normal way: {msg}");
        }

        static void Report() =>
            Plugin.L.LogWarning($"[KupoCache] reused {hits} swaps; {stored} files kept (~{bytes / (1024 * 1024)} MB GPU); {fails} fell back (info, not an error)");
    }
}
