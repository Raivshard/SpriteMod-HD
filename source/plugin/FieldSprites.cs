using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnityEngine;

namespace FF5MenuSprites
{
    /// <summary>
    /// MenuLayout "fieldSprites": true swaps field (map) character frames for PNGs in
    /// GameAssets/Serial/Res/Chara/Map/MO_FF5_xNNN_CNN/&lt;frame&gt;.png beside MenuLayout.json.
    /// Why [verified, KupoUI TextureLogger 2026-11-15]: field characters are SpriteRenderers whose sprites (e.g.
    /// "Default_00(Clone)") are cut from one atlas texture ("sactx-128x128-Uncompressed-MO_FF5_P001_C00-86274720");
    /// KupoUI's SpriteRenderer.sprite hook looks such frames up by sprite name only, and names like "Default_00" also
    /// exist in every battle set, so the field frame never resolves.
    /// How: a postfix on UnityEngine.SpriteRenderer.set_sprite (interop verified) sees the game's frame, finds the
    /// folder from the atlas texture name and the file from the frame name, builds that sprite once (pivot bottom
    /// centre like the game's frames [verified, MagiciteExport spritedata Pivot = [0.5,0]], pixels per unit from the
    /// size file &lt;frame&gt;.json "pixelsPerUnit", default 1, and "shiftY" in image pixels, default 0, point filter) and
    /// sets it instead. No file = the game's
    /// frame stays. Values apply live; the hook is installed at start.
    /// Standing still: the game's walk cycles have 2 frames and "_00" is both a walk frame and the frame shown at rest
    /// [verified, anim_info.json "Default" = Default_01, Default_00; F8 dump 2026-11-15: resting characters on _00]. If
    /// &lt;frame&gt;.idle.png exists, the plugin shows &lt;frame&gt;.png while the frames keep changing (walking: the game
    /// switches _01/_00 every 0.10 s [verified, log]) and the idle file once the frame has stayed IdleHold seconds
    /// (standing; also NPCs and scripted moves). Position cannot tell: the player stays put while the map scrolls.
    /// Second stride: if &lt;frame&gt;.2.png exists, each time a renderer comes round to that frame the other file is used,
    /// so a 2-frame game cycle (_01, _00) plays a 4-step walk: stride A, passing, stride B, passing.
    /// </summary>
    static class FieldSprites
    {
        internal static bool Enabled;
        /// <summary>MenuLayout "fieldSpritesDebug": log every frame the game sets on a field character (max 400 lines).</summary>
        internal static bool Debug;
        static int debugLines;
        /// <summary>
        /// MenuLayout "fieldStrideDelay": { "WalkL": 0.033 } = seconds the passing frame stays into each stride of that
        /// walk (frames named &lt;prefix&gt;_00, the strides). The game holds every walk frame 0.10 s [verified, log]; the
        /// user found the side passing pose too short (2026-11-15). Live.
        /// </summary>
        internal static Dictionary<string, float> StrideDelay = new Dictionary<string, float>();
        /// <summary>Seconds a frame with an idle file must stay before the standing frame shows (walk steps: 0.10 s).</summary>
        const float IdleHold = 0.16f;
        static readonly Regex Atlas = new Regex(@"^sactx-\d+x\d+-[A-Za-z]+-(MO_FF5_[A-Z]\d{3}_C\d{2})-", RegexOptions.Compiled);
        static readonly Dictionary<string, Sprite> made = new Dictionary<string, Sprite>();   // "folder/frame" -> sprite, null = no file
        static readonly HashSet<string> folderLogged = new HashSet<string>();
        static bool inside, broken, idleLogged;
        static int swaps;

        class St { public SpriteRenderer R; public string Folder, Frame, Key; public float Since, Delay; public bool HasIdle, Idle, UseAlt, AltNext, Pending; public Sprite Shown; public int Player = -1; }

        /// <summary>Stride delay for a frame name: "WalkL_00" -> StrideDelay["WalkL"], only for "_00" frames.</summary>
        static float DelayFor(string frame)
        {
            if (StrideDelay.Count == 0 || !frame.EndsWith("_00", StringComparison.Ordinal)) return 0f;
            return StrideDelay.TryGetValue(frame.Substring(0, frame.Length - 3), out var d) ? Mathf.Clamp(d, 0f, 0.09f) : 0f;
        }

        /// <summary>Debug only: is this renderer under RootObject/FieldPlayer(Clone)? Cached per state.</summary>
        static bool IsPlayer(SpriteRenderer r, St st)
        {
            if (st.Player < 0)
            {
                st.Player = 0;
                for (var t = r.transform; t != null; t = t.parent)
                    if (t.name.StartsWith("FieldPlayer", StringComparison.Ordinal)) { st.Player = 1; break; }
            }
            return st.Player == 1;
        }
        static readonly Dictionary<IntPtr, St> states = new Dictionary<IntPtr, St>();

        static string Root => Path.Combine(Path.GetDirectoryName(Plugin.ConfigPath) ?? "", "GameAssets", "Serial", "Res", "Chara", "Map");

        internal static void Install(HarmonyLib.Harmony harmony)
        {
            try
            {
                var setter = HarmonyLib.AccessTools.PropertySetter(typeof(SpriteRenderer), "sprite");
                harmony.Patch(setter, postfix: new HarmonyLib.HarmonyMethod(typeof(FieldSprites).GetMethod(nameof(Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.L.LogInfo("[Field] sprite hook applied");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("[Field] sprite hook failed, field characters keep the game's sprites: " + e.Message);
            }
        }

        static void Post(SpriteRenderer __instance, Sprite __0)
        {
            if (!Enabled || broken || inside || __0 == null || __instance == null) return;
            try
            {
                var tex = __0.texture;
                if (tex == null) return;
                var m = Atlas.Match(tex.name);
                if (!m.Success) return;
                string folder = m.Groups[1].Value;
                string frame = __0.name;
                if (frame.EndsWith("(Clone)", StringComparison.Ordinal)) frame = frame.Substring(0, frame.Length - 7);
                string key = folder + "/" + frame;
                var walk = Get(folder, frame);
                var rp = __instance.Pointer;
                if (walk == null) { states.Remove(rp); return; }
                if (states.Count > 1024) states.Clear();
                if (!states.TryGetValue(rp, out var st)) { st = new St(); states[rp] = st; }
                if (Debug && debugLines < 400 && st.Key != key && IsPlayer(__instance, st))
                {
                    debugLines++;
                    Plugin.L.LogWarning($"[FieldDbg] f {Time.frameCount} '{__instance.name}' set {key} (was {st.Key ?? "-"}, {Time.unscaledTime - st.Since:0.000} s) idle={st.Idle}");
                }
                if (st.Key != key)                                   // the game changed the frame
                {
                    st.Key = key; st.Folder = folder; st.Frame = frame; st.Since = Time.unscaledTime;
                    st.HasIdle = Get(folder, frame + ".idle") != null;
                    st.Idle = false;
                    // second stride: each time this renderer comes round to a frame that has <frame>.2.png, the
                    // other stride is used (stride A, passing, stride B, passing)
                    if (Get(folder, frame + ".2") != null) { st.AltNext = !st.AltNext; st.UseAlt = st.AltNext; }
                    else st.UseAlt = false;
                    // stride delay: keep showing the passing frame a little into the stride (only when coming from a
                    // frame we drew, i.e. mid-walk)
                    st.Delay = DelayFor(frame);
                    st.Pending = st.Delay > 0f && st.Shown != null && !st.Shown.WasCollected;
                }
                st.R = __instance;
                if (st.UseAlt) walk = Get(folder, frame + ".2") ?? walk;
                var show = st.Pending ? st.Shown : st.Idle ? Get(folder, frame + ".idle") ?? walk : walk;
                st.Shown = show;
                inside = true;
                try { __instance.sprite = show; } finally { inside = false; }
                SyncSiblings(st);                                    // head follows at once, not a frame later
                swaps++;
            }
            catch (Exception e)
            {
                broken = true;
                Plugin.L.LogWarning("[Field] sprite swap failed, switched off until the game restarts (the game's sprites stay): " + e.Message);
            }
        }

        /// <summary>Every frame (LayoutWatcher.Update): idle file while a character stands still, walk file while it moves.</summary>
        internal static void Tick()
        {
            if (!Enabled || broken || states.Count == 0) return;
            float now = Time.unscaledTime;
            List<IntPtr> dead = null;
            foreach (var kv in states)
            {
                var st = kv.Value;
                if (st.R == null || st.R.WasCollected) { (dead ??= new List<IntPtr>()).Add(kv.Key); continue; }
                SyncSiblings(st);
                if (st.Pending && now - st.Since >= st.Delay)       // stride delay over: show the stride
                {
                    st.Pending = false;
                    var stride = st.UseAlt ? Get(st.Folder, st.Frame + ".2") ?? Get(st.Folder, st.Frame) : Get(st.Folder, st.Frame);
                    if (stride != null)
                    {
                        st.Shown = stride;
                        inside = true;
                        try { st.R.sprite = stride; } finally { inside = false; }
                        SyncSiblings(st);
                    }
                }
                // Standing = the walk frame has not changed for IdleHold seconds. Position cannot be used: the player
                // stays at world (0,-6) while the map scrolls, and even relative to the map root no walking movement
                // shows (only a 1-unit bob on the side frames) [verified, [FieldDbg] log 2026-11-15 02:58]. While
                // walking the game switches _01/_00 every 0.10 s in every direction [verified, same log].
                if (!st.HasIdle || st.Idle || now - st.Since < IdleHold) continue;
                const bool want = true;
                var sp = Get(st.Folder, st.Frame + ".idle");
                if (sp == null) { st.HasIdle = false; continue; }
                st.Idle = want;
                st.Pending = false;
                st.Shown = sp;
                inside = true;
                try { st.R.sprite = sp; } finally { inside = false; }
                SyncSiblings(st);
                if (!idleLogged) { idleLogged = true; Plugin.L.LogWarning($"[Field] standing frame used for '{st.Key}' (frame unchanged {now - st.Since:0.00} s) (info, not an error)"); }
                if (Debug && debugLines < 400 && IsPlayer(st.R, st)) { debugLines++; Plugin.L.LogWarning($"[FieldDbg] f {Time.frameCount} '{st.R.name}' STAND {st.Key} after {now - st.Since:0.000} s"); }
            }
            if (dead != null) foreach (var d in dead) states.Remove(d);
        }

        /// <summary>
        /// Keeps the head renderer showing what the body shows. VisualInstanceHead (a sibling of VisualInstanceBody)
        /// never gets an atlas frame through the sprite setter [verified, [FieldDbg] log: no Head lines] and the game
        /// does not copy the body's sprite to it at every step: after the standing frame was put on it once it stayed
        /// there while walking (user, 2026-11-15) [inferred]. So: any sibling SpriteRenderer that shows one of this
        /// character's frames (ours, or the game's atlas for the same folder) is set to the body's current sprite.
        /// </summary>
        static void SyncSiblings(St st)
        {
            if (st.R.name != "VisualInstanceBody") return;
            var body = st.R.sprite;
            var par = st.R.transform.parent;
            if (body == null || par == null) return;
            for (int i = 0; i < par.childCount; i++)
            {
                var sr = par.GetChild(i).GetComponent<SpriteRenderer>();
                if (sr == null || sr.Pointer == st.R.Pointer) continue;
                var s = sr.sprite;
                if (s == null || s.Pointer == body.Pointer) continue;
                var t = s.texture;
                if (t == null || t.name.IndexOf(st.Folder, StringComparison.Ordinal) < 0) continue;   // not this character's frame
                inside = true;
                try { sr.sprite = body; } finally { inside = false; }
            }
        }

        /// <summary>Cached sprite for folder/frame; null = no file (also cached).</summary>
        static Sprite Get(string folder, string frame)
        {
            string key = folder + "/" + frame;
            if (!made.TryGetValue(key, out var sp) || (sp != null && sp.WasCollected))
            {
                sp = Make(folder, frame);
                made[key] = sp;
            }
            return sp;
        }

        static Sprite Make(string folder, string frame)
        {
            string png = Path.Combine(Root, folder, frame + ".png");
            if (!File.Exists(png)) return null;
            float ppu = 1f, shiftY = 0f;    // shiftY: image pixels the picture moves up (negative = down)
            string js = Path.Combine(Root, folder, frame + ".json");
            if (File.Exists(js))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(js));
                    if (doc.RootElement.TryGetProperty("pixelsPerUnit", out var p) && p.ValueKind == JsonValueKind.Number && p.GetSingle() > 0f)
                        ppu = p.GetSingle();
                    if (doc.RootElement.TryGetProperty("shiftY", out var sy) && sy.ValueKind == JsonValueKind.Number)
                        shiftY = sy.GetSingle();
                }
                catch (Exception e) { Plugin.L.LogWarning($"[Field] size file not readable, 1 px per unit used: {js}: {e.Message}"); }
            }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(png)))
            {
                Plugin.L.LogWarning("[Field] not a readable PNG, the game's frame stays: " + png);
                return null;
            }
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = "Field_" + folder + "_" + frame;
            tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
            // shiftY moves the picture by moving the anchor (the feet are the image's bottom row, so the picture itself
            // cannot move down inside the image): -1 = one image pixel down
            var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, -shiftY / tex.height), ppu, 0, SpriteMeshType.FullRect);
            sp.name = frame + "_Custom";
            sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
            if (folderLogged.Add(folder))
                Plugin.L.LogWarning($"[Field] {folder}: frames from {Path.Combine(Root, folder)} ({tex.width}x{tex.height}, {ppu} px per unit) (info, not an error)");
            return sp;
        }
    }
}
