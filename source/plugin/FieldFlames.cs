using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UnityEngine;

namespace FF5MenuSprites
{
    /// <summary>
    /// Flame frames for field fire effects (torches, fireplaces). How the game draws a map flame [verified, F8 dump
    /// screen_20261117_120447, Karnak town; docs/verified_facts.md "Field fire and lava"]: the effect runs once,
    /// off-screen, under EffectRoot/Effect/FieldEffectParticleEntity(Clone)/EffectPrefab(Clone)/EF_xxx/ (parts e.g.
    /// Fire, Sparkle, Mask or fire, glow, sparks; particle renderers on layer 14) and its own EffectPrefab(Clone)/Camera
    /// films them into a small render texture (40x40); every torch shows that texture on a quad
    /// Map_NNNNN/FieldEffectBoardEntity(Clone)/VisualParent. Torch = EF_FF4_M_002, fireplace = EF_FF5_M_003 [verified, dumps].
    ///
    /// Here (flame = frames, sparks = particles; keep the game's sparks and glow): for an effect that
    /// has a folder effects/&lt;EF name&gt;/ with flame_*.png (upright frames, mobile art at 2x = 2 px per game unit),
    /// the effect's "fire" part is switched off and a sprite showing the frames is put at the same off-screen spot,
    /// child of the effect (so it hides when the game hides the effect) and drawn before the other parts (sparks on
    /// top). The camera then films frames + sparks together and every torch shows that. MenuLayout
    /// "fieldEffectResolution" (default 2) makes the effect's render texture that many times larger, so the frames and
    /// sparks are not squeezed into 40x40 px. Optional effects/&lt;EF name&gt;/flame.json: { "frameMs": 133,
    /// "offset": [x, y] (game units, y up), "scale": 1 }. A folder with no frames and flame.json { "hideFire": true } only
    /// hides the game's fire part (fireplace since 2026-11-21: the fire is a Tiled map object). Any error: the game's
    /// flame stays, logged once.
    /// </summary>
    static class FieldFlames
    {
        internal static bool Enabled = true;
        internal static float Resolution = 2f;
        const string Tag = "[Flames] ";
        static string Folder => Path.Combine(Path.GetDirectoryName(Plugin.ConfigPath) ?? "", "effects");

        class Set { public Sprite[] Frames; public float FrameSec = 0.133f; public Vector2 Offset; public float Scale = 1f; public int QuadMode; }
        // flame.json "quadMode": Tiled blend-mode name for the quads that show the effect on the map (fireplace
        // glow as overlay); drawn with SpriteMod/ObjectBlend, alpha = brightest channel of the effect's render texture
        static readonly string[] Modes = { "normal", "add", "multiply", "screen", "overlay", "darken", "lighten", "color-dodge",
            "color-burn", "hard-light", "soft-light", "difference", "exclusion" };
        static Shader blendShader; static bool blendTried;
        static readonly List<(MeshRenderer R, Material Old, Material New)> swapped = new List<(MeshRenderer, Material, Material)>();
        static int QuadMode(string js)
        {
            try
            {
                if (!File.Exists(js)) return 0;
                using var doc = JsonDocument.Parse(File.ReadAllText(js), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                return doc.RootElement.TryGetProperty("quadMode", out var q) && q.ValueKind == JsonValueKind.String ? Math.Max(0, Array.IndexOf(Modes, q.GetString())) : 0;
            }
            catch { return 0; }
        }
        class Inst { public Transform Root; public GameObject FireGo; public Renderer Fire; public GameObject Go; public SpriteRenderer R; public Set S; public int Shown = -1; }
        class Rt { public Camera Cam; public RenderTexture Old, New; public string Name; }

        static readonly Dictionary<string, Set> sets = new Dictionary<string, Set>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<IntPtr, Inst> insts = new Dictionary<IntPtr, Inst>();
        static readonly Dictionary<IntPtr, Rt> rts = new Dictionary<IntPtr, Rt>();
        static readonly HashSet<string> logged = new HashSet<string>();
        static float next;
        static long stamp = -1;
        static bool broken;
        static float lastRes = -1f;

        static void Log(string s) { if (logged.Count < 200 && logged.Add(s)) Plugin.L.LogInfo(Tag + s); }   // details: BepInEx Info level
        static void Warn(string s) { if (logged.Count < 200 && logged.Add(s)) Plugin.L.LogWarning(Tag + s); }

        internal static void Tick()
        {
            if (broken) return;
            try
            {
                if (!Enabled) { if (insts.Count > 0 || rts.Count > 0) Undo("switched off in MenuLayout"); return; }
                Animate();
                if (Time.unscaledTime < next) return;
                next = Time.unscaledTime + 0.5f;
                if (Restamp() || lastRes != Resolution) { Undo(lastRes < 0f ? null : "files or resolution changed"); lastRes = Resolution; }
                if (sets.Count == 0 && !AnyFolder()) return;
                Discover();
                SwapQuads();
            }
            catch (Exception e)
            {
                broken = true;
                Plugin.L.LogWarning(Tag + "stopped, the game's flames stay: " + e);
                try { Undo(null); } catch { }
            }
        }

        static bool AnyFolder() => Directory.Exists(Folder) && Directory.GetDirectories(Folder).Length > 0;

        /// <summary>True when the effects folder changed (frames are re-read).</summary>
        static bool Restamp()
        {
            long st = 0;
            if (Directory.Exists(Folder))
                foreach (var f in Directory.GetFiles(Folder, "*.*", SearchOption.AllDirectories)) st = st * 31 + File.GetLastWriteTimeUtc(f).Ticks + new FileInfo(f).Length;
            if (st == stamp) return false;
            bool first = stamp == -1;
            stamp = st;
            foreach (var s in sets.Values)
                if (s?.Frames != null) foreach (var sp in s.Frames) if (sp != null && !sp.WasCollected) { var t = sp.texture; UnityEngine.Object.Destroy(sp); if (t != null && !t.WasCollected) UnityEngine.Object.Destroy(t); }
            sets.Clear();
            logged.Clear();
            return !first;
        }

        /// <summary>Frames for an effect, or null (no folder / no readable frames).</summary>
        static Set Load(string ef)
        {
            if (sets.TryGetValue(ef, out var have)) return have;
            Set set = null;
            string dir = Path.Combine(Folder, ef);
            if (Directory.Exists(dir))
            {
                var files = Directory.GetFiles(dir, "flame_*.png");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                var frames = new List<Sprite>();
                foreach (var f in files)
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(f))) { Plugin.L.LogWarning(Tag + "not a readable PNG: " + f); UnityEngine.Object.Destroy(tex); continue; }
                    tex.filterMode = FilterMode.Point;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    tex.name = "SpriteModFlame_" + Path.GetFileNameWithoutExtension(f);
                    // mobile art is 2x the game's pixels: 2 px per game unit
                    // pivot bottom centre: the frame stands on the game flame's base point
                    var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0f), 2f, 0, SpriteMeshType.FullRect);
                    sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    frames.Add(sp);
                }
                if (frames.Count > 0)
                {
                    set = new Set { Frames = frames.ToArray(), QuadMode = QuadMode(Path.Combine(dir, "flame.json")) };
                    string js = Path.Combine(dir, "flame.json");
                    if (File.Exists(js))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(File.ReadAllText(js), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                            var r = doc.RootElement;
                            if (r.TryGetProperty("frameMs", out var fm) && fm.ValueKind == JsonValueKind.Number && fm.GetSingle() > 0f) set.FrameSec = fm.GetSingle() / 1000f;
                            if (r.TryGetProperty("scale", out var sc) && sc.ValueKind == JsonValueKind.Number && sc.GetSingle() > 0f) set.Scale = sc.GetSingle();
                            if (r.TryGetProperty("offset", out var of) && of.ValueKind == JsonValueKind.Array && of.GetArrayLength() == 2)
                                set.Offset = new Vector2(of[0].GetSingle(), of[1].GetSingle());
                        }
                        catch (Exception e) { Plugin.L.LogWarning(Tag + "flame.json not read (defaults used): " + js + ": " + e.Message); }
                    }
                    Log($"{ef}: {frames.Count} frame(s) {frames[0].texture.width}x{frames[0].texture.height}, {set.FrameSec * 1000f:0} ms each, offset {set.Offset}, scale {set.Scale}");
                }
                else if (HideOnly(Path.Combine(dir, "flame.json")))
                {
                    // the fireplace fire is a Tiled map object now; the game's fire part is only hidden
                    set = new Set { Frames = new Sprite[0], QuadMode = QuadMode(Path.Combine(dir, "flame.json")) };
                    Log($"{ef}: no frames, flame.json \"hideFire\": the game's fire part is hidden (sparks and glow stay)");
                }
                else Log($"{ef}: folder has no flame_*.png");
            }
            sets[ef] = set;
            return set;
        }

        static bool HideOnly(string js)
        {
            try
            {
                if (!File.Exists(js)) return false;
                using var doc = JsonDocument.Parse(File.ReadAllText(js), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                return doc.RootElement.TryGetProperty("hideFire", out var h) && h.ValueKind == JsonValueKind.True;
            }
            catch (Exception e) { Plugin.L.LogWarning(Tag + "flame.json not read: " + js + ": " + e.Message); return false; }
        }

        static void Discover()
        {
            var er = GameObject.Find("EffectRoot/Effect");
            if (er == null) return;
            var root = er.transform;
            for (int i = 0; i < root.childCount; i++)
            {
                var ent = root.GetChild(i);
                if (!ent.name.StartsWith("FieldEffectParticleEntity", StringComparison.Ordinal)) continue;
                var pf = ent.Find("EffectPrefab(Clone)");
                if (pf == null) continue;
                Camera cam = null;
                var camT = pf.Find("Camera");
                if (camT != null) cam = camT.GetComponent<Camera>();
                for (int j = 0; j < pf.childCount; j++)
                {
                    var e = pf.GetChild(j);
                    if (!e.name.StartsWith("EF_", StringComparison.Ordinal)) continue;
                    var set = Load(e.name);
                    if (set == null) continue;
                    if (!insts.ContainsKey(e.Pointer)) Attach(e, set);
                    if (cam != null) Sharpen(cam, e.name);
                }
            }
        }

        static void Attach(Transform e, Set set)
        {
            Transform fireT = null;
            for (int k = 0; k < e.childCount; k++)
                if (string.Equals(e.GetChild(k).name, "fire", StringComparison.OrdinalIgnoreCase)) { fireT = e.GetChild(k); break; }
            var fire = fireT != null ? fireT.GetComponent<Renderer>() : null;
            if (fire == null) { Log($"{e.name}: no part named 'fire' with a renderer; left alone"); insts[e.Pointer] = new Inst { Root = e }; return; }

            // draw before the other parts: lowest queue and sorting order of the siblings, then one below
            int minQ = int.MaxValue, minOrder = int.MaxValue;
            var parts = new List<string>();
            foreach (var r in e.GetComponentsInChildren<Renderer>(true))
            {
                parts.Add(r.gameObject.name);
                if (r.Pointer == fire.Pointer) continue;
                var m = r.sharedMaterial;
                if (m != null) minQ = Math.Min(minQ, m.renderQueue);
                minOrder = Math.Min(minOrder, r.sortingOrder);
            }
            if (set.Frames.Length == 0)   // hide only ("hideFire")
            {
                fire.enabled = false;
                insts[e.Pointer] = new Inst { Root = e, FireGo = fireT.gameObject, Fire = fire, S = set };
                Log($"{e.name}: '{fireT.name}' hidden (no frames); parts: {string.Join(", ", parts)}");
                return;
            }
            var b = fire.bounds;
            var go = new GameObject("SpriteModFlame");
            go.layer = fireT.gameObject.layer;
            go.transform.SetParent(e, false);
            var ls = e.lossyScale;
            go.transform.localScale = new Vector3(set.Scale / (ls.x != 0f ? ls.x : 1f), set.Scale / (ls.y != 0f ? ls.y : 1f), 1f);
            // Test 1 (2026-11-17) centred the frames on the game flame's bounds: too high (torch: bounds centre 15 units
            // above the camera centre, frame cut off at the top of the 40-unit view; fireplace visibly high). The game
            // flame rises from its emitter, so the frame now stands on the emitter (the fire part's position), a little
            // further from the camera than the sparks.
            var ep = fireT.position;
            go.transform.position = new Vector3(ep.x + set.Offset.x, ep.y + set.Offset.y, ep.z + 1f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = set.Frames[0];
            if (minOrder != int.MaxValue) sr.sortingOrder = minOrder - 1;
            var mat = sr.material;   // own instance
            if (mat != null && minQ != int.MaxValue) mat.renderQueue = minQ;
            fire.enabled = false;
            insts[e.Pointer] = new Inst { Root = e, FireGo = fireT.gameObject, Fire = fire, Go = go, R = sr, S = set, Shown = 0 };
            var camT = e.parent != null ? e.parent.Find("Camera") : null;
            var camC = camT != null ? camT.GetComponent<Camera>() : null;
            Log($"{e.name}: '{fireT.name}' off, frames stand at {go.transform.position} (emitter {ep}; game flame bounds {b.center} size {b.size}; " +
                $"camera {(camT != null ? camT.position.ToString() : "none")} size {(camC != null ? camC.orthographicSize : -1f)}), layer {go.layer}, " +
                $"shader '{(mat != null && mat.shader != null ? mat.shader.name : "none")}' queue {(mat != null ? mat.renderQueue : -1)} order {sr.sortingOrder}; parts: {string.Join(", ", parts)}");
        }

        /// <summary>Larger render texture for the effect's camera (fieldEffectResolution).</summary>
        static void Sharpen(Camera cam, string ef)
        {
            if (Resolution <= 1.01f) return;
            if (rts.TryGetValue(cam.Pointer, out var rt))
            {
                var cur = cam.targetTexture;
                if (cur != null && rt.New != null && !rt.New.WasCollected && cur.Pointer != rt.New.Pointer)
                {
                    Log($"{ef}: the game set the effect camera's texture back; larger one set again");
                    rt.Old = cur;
                    cam.targetTexture = rt.New;
                }
                return;
            }
            var old = cam.targetTexture;
            if (old == null) { Log($"{ef}: effect camera has no render texture; resolution left alone"); rts[cam.Pointer] = new Rt { Cam = cam, Name = ef }; return; }
            int k = Mathf.Clamp(Mathf.RoundToInt(Resolution), 2, 8);
            var nw = new RenderTexture(old.width * k, old.height * k, old.depth, old.format) { name = "SpriteModFx_" + ef, filterMode = old.filterMode, wrapMode = old.wrapMode };
            nw.Create();
            cam.targetTexture = nw;
            rts[cam.Pointer] = new Rt { Cam = cam, Old = old, New = nw, Name = ef };
            Log($"{ef}: effect render texture {old.width}x{old.height} -> {nw.width}x{nw.height} (x{k}), filter {old.filterMode}");
        }

        /// <summary>Quads that show an old effect texture get the larger one (new quads appear as maps load).</summary>
        static void SwapQuads()
        {
            if (rts.Count == 0) return;
            // effects of a map that was unloaded: free our texture
            List<IntPtr> dead = null;
            foreach (var kv in rts)
                if (kv.Value.Cam == null || kv.Value.Cam.WasCollected)
                {
                    (dead ??= new List<IntPtr>()).Add(kv.Key);
                    var nw = kv.Value.New;
                    if (nw != null && !nw.WasCollected) { nw.Release(); UnityEngine.Object.Destroy(nw); }
                }
            if (dead != null) foreach (var k in dead) rts.Remove(k);
            var view = GameObject.Find("RootObject/RootFiled/MapRenderer");
            var root = view != null && view.transform.parent != null ? view.transform.parent.parent : null;
            if (root == null) return;
            var map = new Dictionary<IntPtr, Rt>();
            foreach (var rt in rts.Values) if (rt.Old != null && !rt.Old.WasCollected && rt.New != null && !rt.New.WasCollected) map[rt.Old.Pointer] = rt;
            if (map.Count == 0) return;
            for (int i = 0; i < root.childCount; i++)
            {
                var m = root.GetChild(i);
                if (!m.name.StartsWith("Map_", StringComparison.Ordinal)) continue;
                for (int j = 0; j < m.childCount; j++)
                {
                    var e = m.GetChild(j);
                    if (!e.name.StartsWith("FieldEffectBoardEntity", StringComparison.Ordinal)) continue;
                    foreach (var r in e.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        var mat = r.sharedMaterial;
                        var t = mat != null ? mat.mainTexture : null;
                        if (t == null || !map.TryGetValue(t.Pointer, out var rt)) continue;
                        mat.mainTexture = rt.New;
                        Log($"{rt.Name}: quad '{m.name}/{e.name}/{r.gameObject.name}' now shows the larger texture");
                        if (sets.TryGetValue(rt.Name, out var st) && st != null && st.QuadMode > 0)
                        {
                            if (!blendTried) { blendTried = true; blendShader = GpuShadow.LoadShader("assets/shaders/objectblend.shader"); }
                            if (blendShader == null) { Warn($"{rt.Name}: quadMode needs SpriteMod/ObjectBlend (shader bundle); left normal"); continue; }
                            var bm = new Material(blendShader) { name = "SpriteModEffectBlend", renderQueue = mat.renderQueue + 1, mainTexture = rt.New };
                            // queue +1 (2459): always after the Tiled objects (2458). Same queue swapped order with the
                            // embers object as the game moves the quad's depth 247-251 while the player walks: flicker
                            // [verified, F8 2026-11-23]. Depth test still hides it behind characters in front.
                            bm.SetFloat("_Mode", st.QuadMode); bm.SetFloat("_AlphaFromRgb", 1f);
                            swapped.Add((r, mat, bm));
                            r.sharedMaterial = bm;
                            Log($"{rt.Name}: quad '{m.name}/{e.name}/{r.gameObject.name}' blend mode {Modes[st.QuadMode]}");
                        }
                    }
                }
            }
        }

        static void Animate()
        {
            if (insts.Count == 0) return;
            List<IntPtr> gone = null;
            foreach (var kv in insts)
            {
                var o = kv.Value;
                if (o.Go == null)   // effect without a fire part, or hide only: keep the game's fire off
                {
                    if (o.Fire != null && !o.Fire.WasCollected && o.Fire.enabled) o.Fire.enabled = false;
                    continue;
                }
                if (o.Root == null || o.Root.WasCollected || o.Go.WasCollected || o.R == null || o.R.WasCollected) { (gone ??= new List<IntPtr>()).Add(kv.Key); continue; }
                // follow the game's flame part: hidden when the game hides it
                bool show = o.FireGo != null && !o.FireGo.WasCollected && o.FireGo.activeSelf;
                if (o.Go.activeSelf != show) o.Go.SetActive(show);
                if (o.Fire != null && !o.Fire.WasCollected && o.Fire.enabled) o.Fire.enabled = false;   // the game switched it on again
                int n = o.S.Frames.Length;
                int idx = n > 1 ? (int)(Time.unscaledTime / o.S.FrameSec) % n : 0;
                if (idx != o.Shown) { o.Shown = idx; o.R.sprite = o.S.Frames[idx]; }
            }
            if (gone != null) foreach (var k in gone) insts.Remove(k);
        }

        /// <summary>Everything back to the game's own state.</summary>
        static void Undo(string why)
        {
            foreach (var s in swapped)
            {
                if (s.R != null && !s.R.WasCollected && s.Old != null && !s.Old.WasCollected) s.R.sharedMaterial = s.Old;
                if (s.New != null && !s.New.WasCollected) UnityEngine.Object.Destroy(s.New);
            }
            swapped.Clear();
            foreach (var o in insts.Values)
            {
                if (o.Fire != null && !o.Fire.WasCollected) o.Fire.enabled = true;
                if (o.Go != null && !o.Go.WasCollected) UnityEngine.Object.Destroy(o.Go);
            }
            insts.Clear();
            foreach (var rt in rts.Values)
            {
                if (rt.Cam != null && !rt.Cam.WasCollected && rt.Old != null && !rt.Old.WasCollected) rt.Cam.targetTexture = rt.Old;
                if (rt.New != null && !rt.New.WasCollected)
                {
                    RestoreQuads(rt);
                    rt.New.Release();
                    UnityEngine.Object.Destroy(rt.New);
                }
            }
            rts.Clear();
            if (why != null) Plugin.L.LogInfo(Tag + "game flames restored: " + why);
        }

        static void RestoreQuads(Rt rt)
        {
            if (rt.Old == null || rt.Old.WasCollected) return;
            var view = GameObject.Find("RootObject/RootFiled/MapRenderer");
            var root = view != null && view.transform.parent != null ? view.transform.parent.parent : null;
            if (root == null) return;
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mat = r.sharedMaterial;
                if (mat != null && mat.mainTexture != null && mat.mainTexture.Pointer == rt.New.Pointer) mat.mainTexture = rt.Old;
            }
        }
    }
}
