using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Last.UI;
using UnityEngine;
using UnityEngine.UI;

namespace FF5MenuSprites
{
    /// <summary>
    /// Sizes and places the character sprite on each menu screen. Rules come from
    /// Modules/System/FF5/MenuLayout.json (re-read when the file changes): for every menu character slot
    /// (Last.UI.MenuCharacterController) whose object path contains a rule's "match", the character image is
    /// scaled to "height" and moved by "moveDown"/"moveRight" (all in screen pixels at 1440p, converted to the
    /// current resolution), keeping the bottom centre as the anchor. Original layout is used if no rule matches.
    /// </summary>
    [BepInPlugin("local.ff5.menusprites", "FF5 Menu Sprites", Version)]
    public class Plugin : BasePlugin
    {
        /// <summary>Project version v0.M.SS.mm (milestone 0-9; small and minor two digits each), tracked in HANDOFF.md. BepInEx keeps only the first
        /// three parts (BepInPlugin.TryParseLongVersion), so the full string is logged on load.</summary>
        internal const string Version = "0.1.26.29";
        internal static ManualLogSource L;
        internal static string ConfigPath;
        static Canvas.WillRenderCanvases afterLayout; // kept referenced so it isn't collected
        static float nextSearch;

        /// <summary>
        /// The rules file: Modules/System/FF5/MenuLayout.json if present, else the first (by path) MenuLayout.json
        /// in any FF5 folder under Modules (e.g. Modules/00-Mods/SpriteMod HD/FF5). Several matches are logged.
        /// If none exists, the System/FF5 path is returned (and reported missing).
        /// </summary>
        internal static string FindConfig()
        {
            string modules = Path.Combine(Paths.GameRootPath, "Modules");
            string fixedPath = Path.Combine(modules, "System", "FF5", "MenuLayout.json");
            if (File.Exists(fixedPath)) return fixedPath;
            try
            {
                var found = new List<string>();
                foreach (var f in Directory.GetFiles(modules, "MenuLayout.json", SearchOption.AllDirectories))
                    if (string.Equals(Path.GetFileName(Path.GetDirectoryName(f)), "FF5", StringComparison.OrdinalIgnoreCase)) found.Add(f);
                found.Sort(StringComparer.OrdinalIgnoreCase);
                if (found.Count > 1) L?.LogWarning("[MenuLayout] several rules files found, using the first: " + string.Join(" | ", found));
                if (found.Count > 0) return found[0];
            }
            catch (Exception e) { L?.LogWarning("[MenuLayout] searching Modules for MenuLayout.json failed: " + e.Message); }
            return fixedPath;
        }

        /// <summary>If the rules file disappeared (moved), looks for it again, at most every 3 s.</summary>
        internal static void Relocate()
        {
            if (File.Exists(ConfigPath) || Time.unscaledTime < nextSearch) return;
            nextSearch = Time.unscaledTime + 3f;
            string p = FindConfig();
            if (p != ConfigPath && File.Exists(p))
            {
                ConfigPath = p;
                L.LogInfo("[MenuLayout] rules file now " + p);
            }
        }

        public override void Load()
        {
            L = Log;
            ConfigPath = FindConfig();
            try
            {
                AddComponent<LayoutWatcher>();
                Log.LogInfo("[MenuLayout] FF5 Menu Sprites v" + Version + " loaded; rules file " + ConfigPath);
            }
            catch (Exception e)
            {
                Log.LogError("[MenuLayout] could not start: " + e);
            }
            var harmony = new HarmonyLib.Harmony("local.ff5.menusprites");
            NameFix.Install(harmony);
            Cleanup.Install(harmony);
            ScrollSpacing.Install(harmony);
            FieldSprites.Install(harmony);
            try { KupoTextureCache.Install(harmony); }
            catch (Exception e) { Log.LogWarning("[KupoCache] could not start: " + e.Message); }
            try { GameProfiler.ReadSwitch(ConfigPath); GameProfiler.Install(harmony); }
            catch (Exception e) { Log.LogWarning("[GameProf] could not start: " + e.Message); }
            try { MiniPatches.Install(harmony); Log.LogInfo("[Mini] patches applied"); }
            catch (Exception e) { Log.LogWarning("[Mini] patches failed (Mini shows the game's own Mini sprites): " + e.Message); }
            try { BattleCursorPatches.Install(harmony); Log.LogInfo("[Battle] cursor patches applied"); }
            catch (Exception e) { Log.LogWarning("[Battle] cursor patches failed (the arrow keeps the game's place): " + e.Message); }
            try
            {
                harmony.PatchAll(typeof(CameraPointPatches).Assembly);
                Log.LogInfo("[Render] camera point patches applied");
            }
            catch (Exception e)
            {
                Log.LogError("[Render] camera patches failed (enlarged render textures will misplace damage numbers): " + e);
            }
            try
            {
                var target = HarmonyLib.AccessTools.Method(typeof(Graphic), "OnEnable");
                harmony.Patch(target, postfix: new HarmonyLib.HarmonyMethod(typeof(GraphicOnEnablePatch).GetMethod("Postfix",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
                Discovery.Hooked = true;
                Log.LogInfo("[MenuLayout] UI switch-on hook applied (no scene sweeps)");
            }
            catch (Exception e)
            {
                Log.LogWarning("[MenuLayout] UI switch-on hook failed; falling back to a scene search once a second: " + e.Message);
            }
        }

        /// <summary>
        /// Subscribes Elements.Apply to Canvas.willRenderCanvases behind the UI layout pass. Handlers run in subscription
        /// order and CanvasUpdateRegistry subscribes when it is first created, so it is created first (instance getter)
        /// and ours is (re)added after it. Called from the watcher once the UI exists.
        /// </summary>
        internal static void HookAfterLayout()
        {
            try
            {
                var reg = CanvasUpdateRegistry.instance;
                if (afterLayout == null)
                    afterLayout = DelegateSupport.ConvertDelegate<Canvas.WillRenderCanvases>(new Action(() =>
                    {
                        try { Elements.Apply(); }
                        catch (Exception e) { L.LogWarning("[Elements] after layout: " + e.Message); }
                    }));
                else Canvas.remove_willRenderCanvases(afterLayout);
                Canvas.add_willRenderCanvases(afterLayout);
                L.LogInfo("[Elements] after-layout pass hooked (registry " + (reg != null ? "present" : "missing") + ")");
            }
            catch (Exception e)
            {
                L.LogWarning("[Elements] could not hook after-layout pass; moves inside layouts may be undone: " + e.Message);
            }
        }
    }

    class Rule
    {
        public string Name, Match;
        public float Height, MoveDown, MoveRight;
        public float Top = -1; // if set: top of the image at this y (1440p px from the screen top), overrides MoveDown
        public List<(string Path, string Sprite)> Hide = new List<(string, string)>();
    }

    /// <summary>
    /// "elements" entry: moves, resizes or recolours a UI text/image or a sprite. Picked by "path" (object path ends
    /// with it, e.g. "character_info/names/name") or by "sprite" (sprite name starts with it). Pixels at 1440p.
    /// </summary>
    class ElementRule
    {
        public string Name, Path, Sprite, Within; // Within: the object path must also contain this
        public bool BattleOnly;
        public float MoveDown, MoveRight, FontSize; // FontSize: 1440p px, 0 = unchanged
        public float FontScale;                     // multiplies the text's own size (turns best fit off); 0 = unchanged
        public float FontSizeMax;                   // shrink-to-fit upper limit, 1440p px; 0 = unchanged
        public float FontSizeMin;                   // shrink-to-fit lower limit, 1440p px; 0 = unchanged
        public float Spacing = -1;                  // layout group spacing in 1440p px; -1 = unchanged
        public float[] ListPad;                     // "listPadding": layout group padding, 1440p px: left, right, top, bottom; null = unchanged
        public TextAnchor? ListAlign;               // "listAlign": layout group child alignment; null = unchanged
        public bool IsGroup => Spacing >= 0 || ListPad != null || ListAlign.HasValue;
        public string Font;                         // font name: a font already loaded in the game, else an installed Windows font
        public float Width;                         // 1440p px, 0 = unchanged (centre kept unless CenterOver is set)
        public float Widen;                         // 1440p px added on the right, left edge kept; 0 = unchanged
        public List<string> Below = new List<string>();      // top edge = lowest bottom of these objects + Gap
        public float Gap;
        public List<string> CenterOver = new List<string>(); // centre x = centre of these objects together
        public bool Placed => Below.Count > 0 || CenterOver.Count > 0;
        public Color? Color;
        public TextStyle Style;                     // fontStyle / lineSpacing / alignment / shadow / outline; null = none
        public int Nth;                             // 1-based: only the Nth child of its parent with this name; 0 = all
        public bool OnTop;                          // draw above the rest of its canvas (own Canvas, overrideSorting)
        public Color? BackdropColor;                // box behind the text (drawn just under it, above everything else)
        public Vector2 BackdropPad = new Vector2(6, 2); // 1440p px around the text's letters
        public int BackdropMerge;                   // >0: one box per ancestor this many levels up, around all its matches
        public float BackdropRadius;                // rounded corners, 1440p px; 0 = square
        public float Scale;                         // multiplies the object's own scale (any object, e.g. an image); 0 = unchanged
        public float Height;                        // 1440p px, any object (rows in a list too: via its LayoutElement); 0 = unchanged
        public bool AddSpritesDown;                 // in battle, battle.spritesDown is added to MoveDown
        public float Radius;                        // rounded corners for a plain (sprite-less) image box, 1440p px; 0 = unchanged
        public StateStyle Selected;                 // extra settings while the object's list row has the cursor; null = none
        public StateStyle Greyed;                   // extra settings while the game shows the object greyed out (unusable); null = none
        public Vector2 Pad;                         // "padding": 1440p px added on each side of the box (x left/right, y top/bottom)
        public bool HasPad => Pad.x != 0 || Pad.y != 0;
        public float Alpha = -1;                    // "alpha" 0-1: CanvasGroup on the object (fades it and its children); -1 = unchanged
        public string Image;                        // "image": PNG path relative to MenuLayout.json's folder; replaces a UI Image's sprite
        public bool Hide;                           // "hide": alpha 0 (looks only: keeps its space; the game still lets it be selected, mouse too)
        public string Text;                         // "text": only texts showing these words (* = any letters, capitals ignored); null = any
        public System.Text.RegularExpressions.Regex TextRx;
        public string FontType;                     // "fontType": only texts whose Last.UI.FontSelector.type is this (Font01..Font10, Default)
        /// <summary>"text" pattern -> regex: whole text, trimmed, case-insensitive, '*' = any run of characters.</summary>
        public static System.Text.RegularExpressions.Regex TextPattern(string p) =>
            new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(p.Trim()).Replace("\\*", ".*") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        /// <summary>Rules a drawing-less container can use (moves, scale, height).</summary>
        public bool ForContainer => MoveDown != 0 || MoveRight != 0 || AddSpritesDown || Scale > 0 || Height > 0 || Width > 0 || Widen != 0 || Alpha >= 0 || HasPad ||
                                    (Selected != null && Selected.Scale > 0) || (Greyed != null && Greyed.Scale > 0);
        public string Leaf => Path == null ? null : Elements.NoClone(Path.Substring(Path.LastIndexOf('/') + 1));
    }

    /// <summary>
    /// "selected" / "greyed": applied on top of the rule while the row has the cursor (Elements.IsSelected) or while the
    /// game shows the text greyed out (Elements.IsGreyed). Selected wins when both are true.
    /// </summary>
    class StateStyle
    {
        public Color? Color;
        public float Scale;     // multiplies the object's scale (on top of the rule's own scale); 0 = unchanged
        public float FontScale; // replaces the rule's fontScale in this state; 0 = the rule's own
        public string Font;     // replaces the rule's font in this state; null = the rule's own
    }

    /// <summary>Shadow or outline settings: Off = switch the effect off; else add it if missing and set what is given.</summary>
    class EffectSpec
    {
        public bool Off;
        public Color? Color;
        public Vector2? Distance; // 1440p px: x right, y down
        public float Blur;        // 1440p px: soft edge faked by 8 fainter copies in a ring of this radius (shadow only)
    }

    /// <summary>
    /// Text style settings shared by "elements" entries and "textStyles" (per font type). Each field null/NaN = unchanged.
    /// UnityEngine.UI.Text fontStyle / lineSpacing / alignment; UnityEngine.UI.Shadow / Outline effectColor and
    /// effectDistance [verified, interop dump].
    /// </summary>
    class TextStyle
    {
        public FontStyle? Style;
        public string Face;          // font-face modifiers, e.g. "SemiBold", "Small", "Small Light": face "<family> <Face>"
        public float LineSpacing = float.NaN;
        public TextAnchor? Align;
        public EffectSpec Shadow, Outline;
        public List<string> FontTypes = new List<string>(); // "textStyles" only
        public bool Any => Style.HasValue || Face != null || !float.IsNaN(LineSpacing) || Align.HasValue || Shadow != null || Outline != null;

        static readonly string[] StyleNames = { "Normal", "Bold", "Italic", "BoldAndItalic" }; // "Regular" = Normal
        internal static readonly string[] AnchorNames = { "UpperLeft", "UpperCenter", "UpperRight", "MiddleLeft", "MiddleCenter", "MiddleRight", "LowerLeft", "LowerCenter", "LowerRight" };

        internal static int Index(string[] names, string v, string what)
        {
            string k = (v ?? "").Replace(" ", "");
            if (names == StyleNames && string.Equals(k, "Regular", StringComparison.OrdinalIgnoreCase)) return 0;
            for (int i = 0; i < names.Length; i++) if (string.Equals(names[i], k, StringComparison.OrdinalIgnoreCase)) return i;
            throw new FormatException($"{what} must be one of {string.Join(", ", names)}: '{v}'");
        }

        static EffectSpec Effect(JsonElement e, Func<string, Color?> color)
        {
            if (e.ValueKind == JsonValueKind.False) return new EffectSpec { Off = true };
            if (e.ValueKind == JsonValueKind.True) return new EffectSpec();
            var s = new EffectSpec();
            if (e.TryGetProperty("color", out var c)) s.Color = color(c.GetString());
            if (e.TryGetProperty("distance", out var d))
            {
                // 3, "3", [3] = 3 right and 3 down; [3, 2] = 3 right, 2 down.
                float Num(JsonElement x) =>
                    x.ValueKind == JsonValueKind.Number ? x.GetSingle() :
                    x.ValueKind == JsonValueKind.String && float.TryParse(x.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v :
                    throw new FormatException("distance must be a number, [n] or [right, down]");
                if (d.ValueKind == JsonValueKind.Array && d.GetArrayLength() == 2) s.Distance = new Vector2(Num(d[0]), Num(d[1]));
                else if (d.ValueKind == JsonValueKind.Array && d.GetArrayLength() == 1) s.Distance = new Vector2(Num(d[0]), Num(d[0]));
                else { float n = Num(d); s.Distance = new Vector2(n, n); }
            }
            if (e.TryGetProperty("blur", out var b)) s.Blur = Mathf.Max(0f, b.GetSingle());
            return s;
        }

        /// <summary>Reads the style keys of a JSON object; null if it has none.</summary>
        internal static TextStyle Parse(JsonElement e, Func<string, Color?> color)
        {
            var s = new TextStyle();
            if (e.TryGetProperty("fontStyle", out var fs))
            {
                // Unity's own four styles (+ "Regular"); anything else names a face of the same family, e.g.
                // "SemiBold" -> "Josefin Sans SemiBold", "Small Semibold" -> "Segoe UI Variable Small Semibold".
                // A trailing "Italic" on a face is applied as Unity's italic.
                string v = (fs.GetString() ?? "").Trim();
                var words = new List<string>(v.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                bool bold = false, italic = false, other = false;
                foreach (var w in words)
                {
                    if (w.Equals("Bold", StringComparison.OrdinalIgnoreCase)) bold = true;
                    else if (w.Equals("Italic", StringComparison.OrdinalIgnoreCase)) italic = true;
                    else if (!w.Equals("Normal", StringComparison.OrdinalIgnoreCase) && !w.Equals("Regular", StringComparison.OrdinalIgnoreCase)
                             && !w.Equals("BoldAndItalic", StringComparison.OrdinalIgnoreCase)) other = true;
                    if (w.Equals("BoldAndItalic", StringComparison.OrdinalIgnoreCase)) { bold = true; italic = true; }
                }
                if (words.Count == 0) throw new FormatException("fontStyle is empty");
                if (!other) s.Style = (FontStyle)((bold ? 1 : 0) + (italic ? 2 : 0));
                else
                {
                    if (italic) { words.RemoveAll(w => w.Equals("Italic", StringComparison.OrdinalIgnoreCase)); s.Style = FontStyle.Italic; }
                    s.Face = string.Join(" ", words);
                }
            }
            if (e.TryGetProperty("lineSpacing", out var ls)) s.LineSpacing = ls.GetSingle();
            if (e.TryGetProperty("alignment", out var al)) s.Align = (TextAnchor)Index(AnchorNames, al.GetString(), "alignment");
            if (e.TryGetProperty("shadow", out var sh)) s.Shadow = Effect(sh, color);
            if (e.TryGetProperty("outline", out var ol)) s.Outline = Effect(ol, color);
            if (e.TryGetProperty("fontType", out var ft))
            {
                if (ft.ValueKind == JsonValueKind.String) s.FontTypes.Add(ft.GetString());
                else if (ft.ValueKind == JsonValueKind.Array) foreach (var x in ft.EnumerateArray()) s.FontTypes.Add(x.GetString());
            }
            return s.Any ? s : null;
        }
    }

    /// <summary>
    /// "1440p px" -> canvas units. The game's canvases scale to a 1920x1080 reference (CanvasScaler
    /// ScaleWithScreenSize [verified, F8 dump]), so 1 px at 1440p = referenceHeight / 1440 units on any screen shape.
    /// At 16:9 this equals the old Screen.height-based value; at 16:10 (Expand canvases letterbox the 16:9 menu box,
    /// Last.UI.AspectFitter [class verified, behaviour inferred]) the old value was ~11% too large. Canvases without
    /// such a scaler keep the screen-height formula.
    /// </summary>
    static class Px
    {
        static readonly Dictionary<IntPtr, float> refH = new Dictionary<IntPtr, float>();
        static bool logged;
        internal static float Upp(Canvas root)
        {
            if (root == null) return Screen.height / 1440f;
            float old = (Screen.height / 1440f) / Mathf.Max(0.0001f, root.scaleFactor);
            if (!refH.TryGetValue(root.Pointer, out float h))
            {
                h = 0;
                try
                {
                    var cs = root.GetComponent<CanvasScaler>();
                    if (cs != null && cs.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize) h = cs.referenceResolution.y;
                }
                catch { }
                if (refH.Count > 200) refH.Clear();
                refH[root.Pointer] = h;
            }
            if (h <= 0) return old;
            float upp = h / 1440f;
            if (!logged && Mathf.Abs(upp - old) > 0.01f * upp)
            {
                logged = true;
                Plugin.L.LogInfo($"[Aspect] screen {Screen.width}x{Screen.height} is not 16:9: px values use the game's reference height ({h:0}) instead of the screen height ({upp:0.###} vs {old:0.###} units per px on '{root.name}')");
            }
            return upp;
        }
        internal static float PerUnit(Canvas root) => 1f / Mathf.Max(0.0001f, Upp(root));
    }

    /// <summary>
    /// "textureLimit": Unity QualitySettings.masterTextureLimit (0 = full size, 1 = half, ...). The game runs at 1
    /// [verified, F8 dump 2026-11-07], which draws textures at half resolution. -1/missing = the game's own value
    /// (restored when the key is removed).
    /// </summary>
    static class TextureLimit
    {
        internal static int Want = -1;
        static int original = -1;
        internal static void Apply()
        {
            try
            {
                int cur = QualitySettings.masterTextureLimit;
                if (Want >= 0)
                {
                    if (original < 0) original = cur;
                    if (cur != Want) { QualitySettings.masterTextureLimit = Want; Plugin.L.LogWarning($"[Textures] masterTextureLimit {cur} -> {Want} (info, not an error)"); }
                }
                else if (original >= 0)
                {
                    if (cur != original) QualitySettings.masterTextureLimit = original;
                    Plugin.L.LogWarning($"[Textures] masterTextureLimit back to the game's {original} (info, not an error)");
                    original = -1;
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("[Textures] " + e.Message); }
        }
    }

    static class Rules
    {
        static long stamp = -1;
        public static List<Rule> List = new List<Rule>();
        public static List<ElementRule> Elements = new List<ElementRule>();

        /// <summary>"listPadding": n (all sides), [left/right, top/bottom] or [left, right, top, bottom] -> {l, r, t, b}.</summary>
        static float[] ListPadding(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Number) { float v = e.GetSingle(); return new[] { v, v, v, v }; }
            if (e.ValueKind == JsonValueKind.Array)
            {
                int n = e.GetArrayLength();
                if (n == 1) { float v = e[0].GetSingle(); return new[] { v, v, v, v }; }
                if (n == 2) { float x = e[0].GetSingle(), y = e[1].GetSingle(); return new[] { x, x, y, y }; }
                if (n == 4) return new[] { e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle(), e[3].GetSingle() };
            }
            throw new FormatException("listPadding must be a number, [left/right, top/bottom] or [left, right, top, bottom]");
        }

        /// <summary>A JSON string or array of strings as a list.</summary>
        static List<string> Strings(JsonElement e)
        {
            var l = new List<string>();
            if (e.ValueKind == JsonValueKind.String) l.Add(e.GetString());
            else if (e.ValueKind == JsonValueKind.Array) foreach (var s in e.EnumerateArray()) l.Add(s.GetString());
            return l;
        }

        static Color? ParseColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            hex = hex.TrimStart('#');
            if (hex.Length != 6 && hex.Length != 8) throw new FormatException("colour must be #RRGGBB or #RRGGBBAA: " + hex);
            float B(int i) => Convert.ToInt32(hex.Substring(i, 2), 16) / 255f;
            return new Color(B(0), B(2), B(4), hex.Length == 8 ? B(6) : 1f);
        }
        public static bool Log;

        /// <summary>Re-reads the rules file if it changed. Returns true when the rules changed.</summary>
        public static bool Refresh()
        {
            Plugin.Relocate();
            long mainStamp = File.Exists(Plugin.ConfigPath) ? File.GetLastWriteTimeUtc(Plugin.ConfigPath).Ticks : 0;
            var addOnFiles = AddOnFiles();
            long s = mainStamp;
            foreach (var f in addOnFiles)
            {
                long t = 0;
                try { t = File.GetLastWriteTimeUtc(f).Ticks; } catch { }
                s = unchecked(s * 31 + t + StringComparer.OrdinalIgnoreCase.GetHashCode(f));
            }
            if (s == stamp) return false;
            stamp = s;
            var list = new List<Rule>();
            bool wasLog = Log;
            Log = false;
            if (mainStamp == 0)
            {
                Plugin.L.LogWarning("[MenuLayout] rules file missing; menus keep their original layout");
            }
            else
            {
                var addOnDocs = new List<JsonDocument>();
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(Plugin.ConfigPath));
                    var root = doc.RootElement;
                    // Sources in order: the main file, then the add-on files (MenuLayout.<name>.json) by file name.
                    var sources = new List<(string label, JsonElement root)> { ("MenuLayout.json", root) };
                    KeyCheck(() => Keys.CheckMainTop(root));
                    foreach (var f in addOnFiles)
                    {
                        string label = Path.GetFileName(f);
                        try
                        {
                            var ad = JsonDocument.Parse(File.ReadAllText(f));
                            addOnDocs.Add(ad);
                            if (ad.RootElement.ValueKind != JsonValueKind.Object) { Plugin.L.LogWarning($"[AddOns] {label}: must be {{ ... }}; skipped"); continue; }
                            KeyCheck(() => Keys.CheckAddOnTop(ad.RootElement, label));
                            sources.Add((label, ad.RootElement));
                        }
                        catch (Exception ae) { Plugin.L.LogWarning($"[AddOns] {label} unreadable, skipped: {ae.Message}"); }
                    }
                    if (root.TryGetProperty("log", out var lg)) Log = lg.ValueKind == JsonValueKind.True;
                    Perf.Enabled = root.TryGetProperty("perf", out var pf) && pf.ValueKind == JsonValueKind.True;
                    try
                    {
                        var nm = new Dictionary<string, string>();
                        if (root.TryGetProperty("characterNames", out var cn) && cn.ValueKind == JsonValueKind.Object)
                            foreach (var kv in cn.EnumerateObject())
                                if (!kv.Name.StartsWith("_") && kv.Value.ValueKind == JsonValueKind.String) nm[kv.Name] = kv.Value.GetString();
                        NameFix.Map = nm;
                        TextureLimit.Want = root.TryGetProperty("textureLimit", out var tl) && tl.ValueKind == JsonValueKind.Number ? Mathf.Clamp(tl.GetInt32(), 0, 3) : -1;
                        TextureLimit.Apply();
                        KupoTextureCache.Enabled = root.TryGetProperty("kupoTextureCache", out var ktc) && ktc.ValueKind == JsonValueKind.True;
                        FieldSprites.Enabled = root.TryGetProperty("fieldSprites", out var fsp) && fsp.ValueKind == JsonValueKind.True;
                        FieldSprites.Debug = root.TryGetProperty("fieldSpritesDebug", out var fsd) && fsd.ValueKind == JsonValueKind.True;
                        // per walk: a number = middle (passing) frame only, or [first, middle, last] (stride A, passing, stride B)
                        var sdl = new Dictionary<string, float[]>();
                        if (root.TryGetProperty("fieldStrideDelay", out var fsdl) && fsdl.ValueKind == JsonValueKind.Object)
                            foreach (var kv in fsdl.EnumerateObject())
                            {
                                if (kv.Name.StartsWith("_")) continue;
                                if (kv.Value.ValueKind == JsonValueKind.Number) sdl[kv.Name] = new[] { 0f, kv.Value.GetSingle(), 0f };
                                else if (kv.Value.ValueKind == JsonValueKind.Array)
                                {
                                    var d = new float[3]; int i = 0;
                                    foreach (var e in kv.Value.EnumerateArray()) { if (i < 3 && e.ValueKind == JsonValueKind.Number) d[i] = e.GetSingle(); i++; }
                                    if (i != 3) Plugin.L.LogWarning($"[Field] fieldStrideDelay \"{kv.Name}\": give 3 numbers [first, middle, last]; read {Math.Min(i, 3)}, missing ones = 0");
                                    sdl[kv.Name] = d;
                                }
                            }
                        FieldSprites.StrideDelay = sdl;
                        KupoTextureCache.SkipUnreadable = root.TryGetProperty("kupoSkipUnreadable", out var ksu) && ksu.ValueKind == JsonValueKind.True;
                        KupoTextureCache.FastUnreadable = root.TryGetProperty("kupoFastUnreadable", out var kfu) && kfu.ValueKind == JsonValueKind.True;
                        Cleanup.SkipGcCollect = false; Cleanup.UnloadEveryMinutes = 0f;
                        if (root.TryGetProperty("cleanup", out var cu) && cu.ValueKind == JsonValueKind.Object)
                        {
                            Cleanup.SkipGcCollect = cu.TryGetProperty("skipGcCollect", out var sg) && sg.ValueKind == JsonValueKind.True;
                            if (cu.TryGetProperty("unloadEveryMinutes", out var um) && um.ValueKind == JsonValueKind.Number) Cleanup.UnloadEveryMinutes = Math.Max(0f, um.GetSingle());
                        }
                    }
                    catch (Exception e) { Plugin.L.LogWarning("[Names] characterNames skipped: " + e.Message); }
                    ScreenDump.Key = root.TryGetProperty("dumpKey", out var dk) ? dk.GetString() : "F8";
                    var screenSrc = new List<int>();
                    for (int si = 0; si < sources.Count; si++)
                    {
                    if (!sources[si].root.TryGetProperty("screens", out var scr) || scr.ValueKind != JsonValueKind.Array) continue;
                    foreach (var r in scr.EnumerateArray())
                    {
                      try
                      {
                        KeyCheck(() => Keys.CheckScreen(r, $"{sources[si].label} screen '{(r.TryGetProperty("name", out var kn) && kn.ValueKind == JsonValueKind.String ? kn.GetString() : "?")}'"));
                        var rule = new Rule
                        {
                            Name = r.TryGetProperty("name", out var n) ? n.GetString() : "?",
                            Match = r.GetProperty("match").GetString(),
                            Height = r.TryGetProperty("height", out var h) ? h.GetSingle() : 0,
                            MoveDown = r.TryGetProperty("moveDown", out var d) ? d.GetSingle() : 0,
                            MoveRight = r.TryGetProperty("moveRight", out var x) ? x.GetSingle() : 0,
                            Top = r.TryGetProperty("top", out var tp) ? tp.GetSingle() : -1,
                        };
                        if (r.TryGetProperty("hide", out var hide))
                            foreach (var hd in hide.EnumerateArray())
                                rule.Hide.Add((hd.GetProperty("path").GetString(), hd.TryGetProperty("sprite", out var hs) ? hs.GetString() : null));
                        if (!string.IsNullOrEmpty(rule.Match)) Place(list, screenSrc, rule, rule.Name, si, sources, "screens");
                      }
                      catch (Exception ex)
                      {
                        Plugin.L.LogWarning($"[MenuLayout] {sources[si].label}: screen '{(r.TryGetProperty("name", out var bn) ? bn.GetString() : "?")}' skipped: {ex.Message}");
                      }
                    }
                    }
                    Battle.ResolutionScale = 0;
                    if (root.TryGetProperty("battle", out var bt) && bt.TryGetProperty("resolutionScale", out var rs))
                        Battle.ResolutionScale = rs.GetSingle();
                    BackgroundFit.Enabled = root.TryGetProperty("battle", out var bt2) && bt2.TryGetProperty("fitBackground", out var fb) && fb.ValueKind == JsonValueKind.True;
                    BattleShift.Down = 0;
                    BattleShift.Cameras = new List<string>(BattleShift.DefaultCameras);
                    if (root.TryGetProperty("battle", out var bt3))
                    {
                        if (bt3.TryGetProperty("spritesDown", out var sd) && sd.ValueKind == JsonValueKind.Number) BattleShift.Down = sd.GetSingle();
                        if (bt3.TryGetProperty("spriteCameras", out var sc) && sc.ValueKind == JsonValueKind.Array) BattleShift.Cameras = Strings(sc);
                    }
                    BattleCharScale.Ppu = root.TryGetProperty("battle", out var bt4) && bt4.TryGetProperty("hdPixelsPerUnit", out var hp) && hp.ValueKind == JsonValueKind.Number ? hp.GetSingle() : 6.667f;
                    BattleCharScale.CustomScale = bt4.ValueKind == JsonValueKind.Object && bt4.TryGetProperty("customScale", out var cs) && cs.ValueKind == JsonValueKind.Number ? Mathf.Clamp(cs.GetSingle(), 0.25f, 8f) : 1f;
                    BattleCharScale.MiniScale = bt4.ValueKind == JsonValueKind.Object && bt4.TryGetProperty("miniScale", out var mns) && mns.ValueKind == JsonValueKind.Number ? Mathf.Clamp(mns.GetSingle(), 0f, 1f) : 0.5f;
                    BattleCharScale.PartyRight = BattleCharScale.PartyDown = BattleCharScale.BackRowPx = BattleCharScale.DepthScale = 0f;
                    BattleCharScale.PartySlots = new List<Vector2>();
                    BattleCharScale.GsOn = BattleCharScale.GsHideGame = BattleCharScale.GsDebug = false;
                    BattleCharScale.GsAzimuth = 45f; BattleCharScale.GsElevation = 50f; BattleCharScale.GsOpacity = 0.45f; BattleCharScale.GsTilt = 0.5f;
                    BattleCharScale.GsThick = 0.25f; BattleCharScale.GsDepthRange = 0.6f; BattleCharScale.GsBlur = 4f; BattleCharScale.GsFalloff = 0.6f;
                    if (root.TryGetProperty("battle", out var bt6))
                    {
                        if (bt6.TryGetProperty("partyRight", out var pr) && pr.ValueKind == JsonValueKind.Number) BattleCharScale.PartyRight = pr.GetSingle();
                        if (bt6.TryGetProperty("partyDown", out var pd) && pd.ValueKind == JsonValueKind.Number) BattleCharScale.PartyDown = pd.GetSingle();
                        if (bt6.TryGetProperty("backRowShift", out var brs) && brs.ValueKind == JsonValueKind.Number) BattleCharScale.BackRowPx = brs.GetSingle();
                        if (bt6.TryGetProperty("depthScale", out var dsc) && dsc.ValueKind == JsonValueKind.Number) BattleCharScale.DepthScale = Mathf.Clamp(dsc.GetSingle(), 0f, 90f);
                        if (bt6.TryGetProperty("groundShadow", out var gsh) && gsh.ValueKind == JsonValueKind.Object)
                        {
                            BattleCharScale.GsOn = !(gsh.TryGetProperty("on", out var gon) && gon.ValueKind == JsonValueKind.False);
                            BattleCharScale.GsHideGame = gsh.TryGetProperty("hideGameShadows", out var ghd) && ghd.ValueKind == JsonValueKind.True;
                            BattleCharScale.GsDebug = gsh.TryGetProperty("debug", out var gdb) && gdb.ValueKind == JsonValueKind.True;
                            if (gsh.TryGetProperty("azimuth", out var gaz) && gaz.ValueKind == JsonValueKind.Number) BattleCharScale.GsAzimuth = gaz.GetSingle();
                            if (gsh.TryGetProperty("elevation", out var gel) && gel.ValueKind == JsonValueKind.Number) BattleCharScale.GsElevation = Mathf.Clamp(gel.GetSingle(), 5f, 90f);
                            if (gsh.TryGetProperty("opacity", out var gop) && gop.ValueKind == JsonValueKind.Number) BattleCharScale.GsOpacity = Mathf.Clamp01(gop.GetSingle());
                            if (gsh.TryGetProperty("groundTilt", out var gti) && gti.ValueKind == JsonValueKind.Number) BattleCharScale.GsTilt = Mathf.Clamp(gti.GetSingle(), 0f, 1f);
                            if (gsh.TryGetProperty("thickness", out var gtk) && gtk.ValueKind == JsonValueKind.Number) BattleCharScale.GsThick = Mathf.Clamp(gtk.GetSingle(), 0f, 2f);
                            if (gsh.TryGetProperty("depthRange", out var gdr) && gdr.ValueKind == JsonValueKind.Number) BattleCharScale.GsDepthRange = Mathf.Clamp(gdr.GetSingle(), 0f, 3f);
                            if (gsh.TryGetProperty("blur", out var gbl) && gbl.ValueKind == JsonValueKind.Number) BattleCharScale.GsBlur = Mathf.Clamp(gbl.GetSingle(), 0f, 64f);
                            if (gsh.TryGetProperty("falloff", out var gfo) && gfo.ValueKind == JsonValueKind.Number) BattleCharScale.GsFalloff = Mathf.Clamp01(gfo.GetSingle());
                        }
                        if (bt6.TryGetProperty("partySlots", out var pslots) && pslots.ValueKind == JsonValueKind.Array)
                            foreach (var e in pslots.EnumerateArray())
                            {
                                float x = 0f, y = 0f;
                                if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() == 2 && e[0].ValueKind == JsonValueKind.Number && e[1].ValueKind == JsonValueKind.Number) { x = e[0].GetSingle(); y = e[1].GetSingle(); }
                                else Plugin.L.LogWarning($"[MenuLayout] battle.partySlots: entry {BattleCharScale.PartySlots.Count + 1} must be [right, down]; 0 used");
                                BattleCharScale.PartySlots.Add(new Vector2(x, y));
                            }
                    }
                    BattleCharScale.EnemyOnTop =root.TryGetProperty("battle", out var bt5) && bt5.TryGetProperty("enemyOnTop", out var eotop) && eotop.ValueKind == JsonValueKind.Array ? Strings(eotop) : new List<string>();
                    LoadingIcon.FrameTime = 0.125f;
                    LoadingIcon.Size = 0;
                    LoadingIcon.Priority = null;
                    if (root.TryGetProperty("loadingIcon", out var li))
                    {
                        if (li.TryGetProperty("frameTime", out var lf)) LoadingIcon.FrameTime = lf.GetSingle();
                        if (li.TryGetProperty("size", out var lsz)) LoadingIcon.Size = lsz.GetSingle();
                        if (li.TryGetProperty("priority", out var lp) && !string.IsNullOrEmpty(lp.GetString()))
                        {
                            string pv = lp.GetString().Replace(" ", "");
                            if (Enum.TryParse<UnityEngine.ThreadPriority>(pv, true, out var tp) && Enum.IsDefined(typeof(UnityEngine.ThreadPriority), tp)) LoadingIcon.Priority = tp;
                            else Plugin.L.LogWarning($"[Loading] priority must be Low, BelowNormal, Normal or High (or \"\" = game's): '{lp.GetString()}'; ignored");
                        }
                    }
                    if (!LoadingIcon.Priority.HasValue) LoadingIcon.ReleasePriority();
                    TitleHighlight.Enabled = false;
                    if (root.TryGetProperty("titleHighlight", out var th))
                    {
                        TitleHighlight.Enabled = true;
                        TitleHighlight.HlColor = (th.TryGetProperty("color", out var thc) ? ParseColor(thc.GetString()) : null) ?? new Color(1f, 0.878f, 0.541f, 1f);
                        TitleHighlight.Scale = th.TryGetProperty("scale", out var ths) ? ths.GetSingle() : 1.1f;
                        TitleHighlight.FadeTime = th.TryGetProperty("fadeTime", out var thf) ? thf.GetSingle() : 0.15f;
                    }
                    var heights = new Dictionary<string, int>();
                    if (root.TryGetProperty("renderHeights", out var rh))
                        foreach (var kv in rh.EnumerateObject())
                            if (kv.Value.ValueKind == JsonValueKind.Number) heights[kv.Name] = kv.Value.GetInt32();
                    RenderScale.Heights = heights;
                    var elements = new List<ElementRule>();
                    var namedStyles = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                    var styleSrc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (slabel, sroot) in sources)
                    {
                    if (sroot.TryGetProperty("styles", out var nss) && nss.ValueKind == JsonValueKind.Object)
                        foreach (var kv in nss.EnumerateObject())
                        {
                            if (kv.Name.StartsWith("_")) continue;
                            if (kv.Value.ValueKind != JsonValueKind.Object) { Plugin.L.LogWarning($"[Styles] {slabel}: '{kv.Name}' must be {{ ... }}; skipped"); continue; }
                            KeyCheck(() => Keys.CheckElement(kv.Value, $"{slabel} style '{kv.Name}'"));
                            if (styleSrc.TryGetValue(kv.Name, out var prev) && prev != slabel)
                                Plugin.L.LogInfo($"[AddOns] style '{kv.Name}' from {slabel} replaces the one from {prev}");
                            namedStyles[kv.Name] = kv.Value;
                            styleSrc[kv.Name] = slabel;
                        }
                    }
                    var mergedDocs = new List<JsonDocument>();
                    var elementSrc = new List<int>();
                    for (int si = 0; si < sources.Count; si++)
                    {
                    if (!sources[si].root.TryGetProperty("elements", out var els) || els.ValueKind != JsonValueKind.Array) continue;
                        foreach (var e0 in els.EnumerateArray())
                        {
                          // A bad value skips only this entry (it used to make the whole file unreadable).
                          try
                          {
                            KeyCheck(() => Keys.CheckElement(e0, $"{sources[si].label} element '{(e0.ValueKind == JsonValueKind.Object && e0.TryGetProperty("name", out var kn) && kn.ValueKind == JsonValueKind.String ? kn.GetString() : "?")}'"));
                            var e = SharedStyles.Merge(e0, namedStyles, mergedDocs);
                            var er = new ElementRule
                            {
                                Name = e.TryGetProperty("name", out var en) ? en.GetString() : "?",
                                Path = e.TryGetProperty("path", out var ep) ? ep.GetString() : null,
                                Sprite = e.TryGetProperty("sprite", out var es) ? es.GetString() : null,
                                BattleOnly = e.TryGetProperty("battleOnly", out var eb) && eb.ValueKind == JsonValueKind.True,
                                MoveDown = e.TryGetProperty("moveDown", out var ed) ? ed.GetSingle() : 0,
                                MoveRight = e.TryGetProperty("moveRight", out var ex) ? ex.GetSingle() : 0,
                                FontSize = e.TryGetProperty("fontSize", out var ef) ? ef.GetSingle() : 0,
                                FontScale = e.TryGetProperty("fontScale", out var efs) ? efs.GetSingle() : 0,
                                FontSizeMax = e.TryGetProperty("fontSizeMax", out var efm) ? efm.GetSingle() : 0,
                                FontSizeMin = e.TryGetProperty("fontSizeMin", out var efmn) ? efmn.GetSingle() : 0,
                                Spacing = e.TryGetProperty("spacing", out var esp) ? esp.GetSingle() : -1,
                                Within = e.TryGetProperty("within", out var ew) ? ew.GetString() : null,
                                Font = e.TryGetProperty("font", out var efn) ? efn.GetString() : null,
                                Width = e.TryGetProperty("width", out var ewd) ? ewd.GetSingle() : 0,
                                Widen = e.TryGetProperty("widen", out var ewn) ? ewn.GetSingle() : 0,
                                Gap = e.TryGetProperty("gap", out var eg) ? eg.GetSingle() : 0,
                                Below = e.TryGetProperty("below", out var ebl) ? Strings(ebl) : new List<string>(),
                                CenterOver = e.TryGetProperty("centerOver", out var eco) ? Strings(eco) : new List<string>(),
                                Color = e.TryGetProperty("color", out var ec) ? ParseColor(ec.GetString()) : null,
                                Style = TextStyle.Parse(e, ParseColor),
                                Nth = e.TryGetProperty("nth", out var enth) ? enth.GetInt32() : 0,
                                OnTop = e.TryGetProperty("onTop", out var eot) && eot.ValueKind == JsonValueKind.True,
                                Scale = e.TryGetProperty("scale", out var esc) ? esc.GetSingle() : 0,
                                Height = e.TryGetProperty("height", out var eht) ? eht.GetSingle() : 0,
                                AddSpritesDown = e.TryGetProperty("addSpritesDown", out var easd) && easd.ValueKind == JsonValueKind.True,
                                Radius = e.TryGetProperty("radius", out var erad) ? Math.Max(0f, erad.GetSingle()) : 0,
                                Alpha = e.TryGetProperty("alpha", out var ealp) ? Mathf.Clamp01(ealp.GetSingle()) : -1,
                                Hide = e.TryGetProperty("hide", out var ehd) && ehd.ValueKind == JsonValueKind.True,
                                ListPad = e.TryGetProperty("listPadding", out var elp) ? ListPadding(elp) : null,
                                ListAlign = e.TryGetProperty("listAlign", out var ela) ? (TextAnchor)TextStyle.Index(TextStyle.AnchorNames, ela.GetString(), "listAlign") : (TextAnchor?)null,
                                Image = e.TryGetProperty("image", out var eim) && !string.IsNullOrWhiteSpace(eim.GetString()) ? eim.GetString() : null,
                            };
                            if (er.Hide) er.Alpha = 0f;
                            StateStyle State(string key)
                            {
                                if (!e.TryGetProperty(key, out var st) || st.ValueKind != JsonValueKind.Object) return null;
                                return new StateStyle
                                {
                                    Color = st.TryGetProperty("color", out var c) ? ParseColor(c.GetString()) : null,
                                    Scale = st.TryGetProperty("scale", out var s) ? s.GetSingle() : 0,
                                    FontScale = st.TryGetProperty("fontScale", out var f) ? f.GetSingle() : 0,
                                    Font = st.TryGetProperty("font", out var fn) && !string.IsNullOrEmpty(fn.GetString()) ? fn.GetString() : null,
                                };
                            }
                            er.Selected = State("selected");
                            er.Greyed = State("greyed");
                            if (e.TryGetProperty("padding", out var epd))
                            {
                                if (epd.ValueKind == JsonValueKind.Number) er.Pad = new Vector2(epd.GetSingle(), epd.GetSingle());
                                else if (epd.ValueKind == JsonValueKind.Array && epd.GetArrayLength() == 2) er.Pad = new Vector2(epd[0].GetSingle(), epd[1].GetSingle());
                                else if (epd.ValueKind == JsonValueKind.Array && epd.GetArrayLength() == 1) er.Pad = new Vector2(epd[0].GetSingle(), epd[0].GetSingle());
                                else throw new FormatException("padding must be a number, [n] or [left/right, top/bottom]");
                            }
                            if (e.TryGetProperty("backdrop", out var ebd) && ebd.ValueKind == JsonValueKind.Object)
                            {
                                er.BackdropColor = (ebd.TryGetProperty("color", out var bc) ? ParseColor(bc.GetString()) : null) ?? new Color(0f, 0f, 0f, 0.5f);
                                if (ebd.TryGetProperty("merge", out var bm)) er.BackdropMerge = Math.Max(0, bm.GetInt32());
                                if (ebd.TryGetProperty("radius", out var brd)) er.BackdropRadius = Math.Max(0f, brd.GetSingle());
                                if (ebd.TryGetProperty("padding", out var bp))
                                {
                                    if (bp.ValueKind == JsonValueKind.Number) er.BackdropPad = new Vector2(bp.GetSingle(), bp.GetSingle());
                                    else if (bp.ValueKind == JsonValueKind.Array && bp.GetArrayLength() == 2) er.BackdropPad = new Vector2(bp[0].GetSingle(), bp[1].GetSingle());
                                    else if (bp.ValueKind == JsonValueKind.Array && bp.GetArrayLength() == 1) er.BackdropPad = new Vector2(bp[0].GetSingle(), bp[0].GetSingle());
                                }
                            }
                            if (e.TryGetProperty("text", out var etx) && !string.IsNullOrEmpty(etx.GetString()))
                            {
                                er.Text = etx.GetString();
                                er.TextRx = ElementRule.TextPattern(er.Text);
                            }
                            if (e.TryGetProperty("fontType", out var eft) && !string.IsNullOrEmpty(eft.GetString()))
                            {
                                er.FontType = eft.GetString();
                                if (!System.Text.RegularExpressions.Regex.IsMatch(er.FontType, "^(Font0[1-9]|Font10|Default)$"))
                                    Plugin.L.LogWarning($"[Elements] {sources[si].label}: '{er.Name}': unknown fontType '{er.FontType}' (Font01-Font10, Default)");
                            }
                            // "text" alone would read every text on screen 10x/s: it needs "path" or "within" to narrow it.
                            if (er.Text != null && string.IsNullOrEmpty(er.Path) && string.IsNullOrEmpty(er.Within))
                                Plugin.L.LogWarning($"[Elements] {sources[si].label}: '{er.Name}' skipped: \"text\" needs \"path\" or \"within\" too");
                            else if (string.IsNullOrEmpty(er.Path) && string.IsNullOrEmpty(er.Sprite) && er.Text == null)
                            {
                                // An entry with only "_" keys is a comment/separator: skipped without a warning.
                                bool comment = true;
                                foreach (var pr in e.EnumerateObject()) if (!pr.Name.StartsWith("_")) { comment = false; break; }
                                if (!comment) Plugin.L.LogWarning($"[Elements] {sources[si].label}: '{er.Name}' skipped: needs \"path\", \"sprite\" or \"text\"" +
                                                    (er.FontType != null ? " (for every text of one font type use \"textStyles\")" : ""));
                            }
                            else Place(elements, elementSrc, er, er.Name, si, sources, "elements");
                          }
                          catch (Exception ex)
                          {
                            Plugin.L.LogWarning($"[Elements] {sources[si].label}: entry '{(e0.TryGetProperty("name", out var bn) ? bn.GetString() : "?")}' skipped: {ex.Message}");
                          }
                        }
                    }
                    foreach (var md in mergedDocs) md.Dispose();
                    Elements = elements;
                    var styles = new List<TextStyle>();
                    foreach (var (tlabel, troot) in sources)
                    if (troot.TryGetProperty("textStyles", out var tss) && tss.ValueKind == JsonValueKind.Array)
                        foreach (var e in tss.EnumerateArray())
                        {
                            TextStyle st;
                            KeyCheck(() => Keys.CheckTextStyle(e, $"{tlabel} textStyle '{(e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out var kn) && kn.ValueKind == JsonValueKind.String ? kn.GetString() : "?")}'"));
                            try { st = TextStyle.Parse(e, ParseColor); }
                            catch (Exception ex) { Plugin.L.LogWarning($"[TextStyles] {tlabel}: entry skipped: " + ex.Message); continue; }
                            if (st == null || st.FontTypes.Count == 0) { Plugin.L.LogWarning("[TextStyles] entry without 'fontType' or without any style key; skipped"); continue; }
                            foreach (var t in st.FontTypes)
                                if (!System.Text.RegularExpressions.Regex.IsMatch(t, "^(Font0[1-9]|Font10|Default)$")) Plugin.L.LogWarning($"[TextStyles] unknown fontType '{t}' (Font01-Font10, Default)");
                            styles.Add(st);
                        }
                    FontTypeStyles.Rules = styles;
                    Plugin.L.LogInfo($"[MenuLayout] read {list.Count} rule(s), {elements.Count} element rule(s), {styles.Count} text style(s) from {string.Join(" + ", sources.ConvertAll(x => x.label))}; log={Log};battle resolutionScale override={Battle.ResolutionScale}; render heights: {string.Join(", ", heights)}");
                }
                catch (IOException e) when (busyRetries < 50)
                {
                    // Busy (the editor still holds it while saving): keep the current settings and read again at the next
                    // check, instead of keeping stale settings until the next save (user, 2026-11-14: groundShadow
                    // azimuth changes were ignored; log showed "being used by another process" twice [verified]).
                    busyRetries++;
                    if (busyRetries == 1) Plugin.L.LogWarning("[MenuLayout] rules file busy (being saved?), reading it again shortly: " + e.Message);
                    stamp = 0;
                    Log = wasLog;
                    return false;
                }
                catch (Exception e)
                {
                    Plugin.L.LogWarning("[MenuLayout] rules file unreadable, menus keep their original layout: " + e.Message);
                    list.Clear();
                    Elements = new List<ElementRule>();
                    FontTypeStyles.Rules = new List<TextStyle>();
                }
                finally
                {
                    foreach (var ad in addOnDocs) ad.Dispose();
                }
            }
            List = list;
            busyRetries = 0;
            return true;
        }
        static int busyRetries;

        /// <summary>Runs a key check; a failure in the check itself never stops the rules from loading.</summary>
        static void KeyCheck(Action check)
        {
            try { check(); }
            catch (Exception e) { Plugin.L.LogWarning("[Keys] check failed (rules still load): " + e.Message); }
        }

        /// <summary>
        /// Add-on rule files: MenuLayout.&lt;name&gt;.json in the main file's folder, in file-name order
        /// (MenuLayout.json itself excluded). Rename a file (e.g. to .json.off) to switch it off.
        /// </summary>
        static List<string> AddOnFiles()
        {
            var r = new List<string>();
            try
            {
                string dir = string.IsNullOrEmpty(Plugin.ConfigPath) ? null : Path.GetDirectoryName(Plugin.ConfigPath);
                if (dir == null || !Directory.Exists(dir)) return r;
                foreach (var f in Directory.GetFiles(dir, "MenuLayout.*.json", SearchOption.TopDirectoryOnly))
                {
                    string n = Path.GetFileName(f);
                    if (n.Equals("MenuLayout.json", StringComparison.OrdinalIgnoreCase) || !n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                    r.Add(f);
                }
                r.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception e) { Plugin.L.LogWarning("[AddOns] listing add-on files failed: " + e.Message); }
            return r;
        }

        /// <summary>
        /// Adds a rule; if an EARLIER file already has one with the same name, the new rule takes the first such
        /// rule's place (so order stays the same) and the other same-named rules from earlier files are dropped.
        /// Same names inside one file are left alone.
        /// </summary>
        static void Place<T>(List<T> rules, List<int> src, T rule, string name, int si, List<(string label, JsonElement root)> sources, string what)
        {
            int first = -1;
            if (si > 0 && !string.IsNullOrEmpty(name) && name != "?")
                for (int i = rules.Count - 1; i >= 0; i--)
                {
                    if (src[i] >= si || !string.Equals(NameOf(rules[i]), name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (first >= 0) { rules.RemoveAt(first); src.RemoveAt(first); }
                    first = i;
                }
            if (first < 0) { rules.Add(rule); src.Add(si); return; }
            Plugin.L.LogInfo($"[AddOns] {what} '{name}' from {sources[si].label} replaces the one from {sources[src[first]].label}");
            rules[first] = rule; src[first] = si;
        }

        static string NameOf(object o) => o is Rule r ? r.Name : o is ElementRule e ? e.Name : null;

        public static Rule For(string path)
        {
            foreach (var r in List) if (path.Contains(r.Match)) return r;
            return null;
        }
    }

    /// <summary>
    /// Raises the resolution of the low-res images the game draws battle and field into (verified: camera
    /// CameraBattle -> "Battle" 384x216, CameraBattlebackGround -> 512x256, CameraFieldMain/CameraTileMap -> "Field"
    /// 320x180, all Point filtered, then enlarged to the screen). MenuLayout.json "renderHeights" maps a camera name to
    /// the wanted height in screen pixels (width keeps the aspect); 0 or missing = the game's own size. The render
    /// texture is resized in place, so whatever shows it keeps working; the camera's view (orthographic size) is
    /// unchanged, only more pixels. Original sizes are restored when a camera is set back to 0.
    /// </summary>
    static class RenderScale
    {
        internal static Dictionary<string, int> Heights = new Dictionary<string, int>();
        static readonly Dictionary<IntPtr, Vector2Int> Original = new Dictionary<IntPtr, Vector2Int>();
        /// <summary>Camera -> how much larger its render texture is than the game made it (see CameraPointPatches).</summary>
        internal static readonly Dictionary<IntPtr, float> Factor = new Dictionary<IntPtr, float>();
        /// <summary>Factor (render texture height / game's) for the camera with this name; 1 = unchanged.</summary>
        internal static float FactorFor(string camName)
        {
            foreach (var cam in Camera.allCameras) if (cam.name == camName && Factor.TryGetValue(cam.Pointer, out var k)) return k;
            return 1f;
        }
        internal static bool HasFactor(string camName) => Heights.TryGetValue(camName, out int h) && h > 0;
        static readonly HashSet<string> Failed = new HashSet<string>();

        internal static void Apply()
        {
            if (Heights.Count == 0 && Original.Count == 0) return;
            foreach (var cam in Camera.allCameras)
            {
                var rt = cam.targetTexture;
                if (rt == null) continue;
                bool known = Original.TryGetValue(rt.Pointer, out var orig);
                Heights.TryGetValue(cam.name, out int h);
                if (!known && h <= 0) continue;
                if (!known) orig = new Vector2Int(rt.width, rt.height);
                // Config heights are 1440p pixels; scale to the current screen height.
                int wantH = h > 0 ? Mathf.Max(orig.y, Mathf.RoundToInt(h * Screen.height / 1440f)) : orig.y;
                int wantW = h > 0 ? Mathf.RoundToInt(orig.x * (float)wantH / orig.y) : orig.x;
                if (wantH != orig.y) Factor[cam.Pointer] = (float)wantH / orig.y; else Factor.Remove(cam.Pointer);
                if (rt.width == wantW && rt.height == wantH) continue;
                try
                {
                    rt.Release();
                    rt.width = wantW;
                    rt.height = wantH;
                    rt.Create();
                    if (h > 0) Original[rt.Pointer] = orig; else Original.Remove(rt.Pointer);
                    Plugin.L.LogInfo($"[Render] camera '{cam.name}': '{rt.name}' {orig.x}x{orig.y} -> {wantW}x{wantH}");
                }
                catch (Exception e)
                {
                    if (Failed.Add(cam.name)) Plugin.L.LogWarning($"[Render] could not resize '{cam.name}' target: {e.Message}");
                }
            }
        }
    }

    /// <summary>
    /// The battle background is shown by Last.Battle.BattleBackgroundController's material (shader
    /// Last/LastSpriteMaster), which snaps the picture to a mosaic grid _MosaicWidth x _MosaicHeight (_MosaicBlock)
    /// = 512 x 256 (512): the game's own render size [verified, F8 dump 2026-11-07]. With "renderHeights" raising the
    /// background render texture, that grid kept the picture at low resolution (user: background pixelated). The
    /// three values are multiplied by the same factor as the render texture; whatever the game sets (e.g. a pixelate
    /// transition) is the base and gets the same factor [inferred: the grid follows these values].
    /// </summary>
    /// <summary>
    /// Battle characters are meshes (RootObject/Entity/PlayerParty/BattlePlayer(Clone)/CharaObject/Mesh, MeshRenderer)
    /// built 32x32 units for the game's 32 px sprite [verified, F8 dump 2026-11-07]. KupoUI swaps an HD texture (e.g.
    /// 248x248) onto the same mesh, so it showed 32 units tall (~213 px at 1440p) instead of 248; its pixelsPerUnit
    /// sidecar only affects sprites. While the texture is much larger than the mesh, the mesh object is scaled so one
    /// texture pixel = 1/hdPixelsPerUnit units (6.667 = 1 px at 1440p with the 216-unit battle view), feet kept in place.
    /// Original-art poses (texture about mesh size) go back to the game's scale. "battle.hdPixelsPerUnit": 0 = off.
    /// Enemies (RootObject/Entity/EnemyParty/BattleEnemy(Clone)/CharaObject/Mesh, same mesh type [verified, F8 dump
    /// 2026-11-07]) are scaled per axis, because HD enemy art need not keep the original's width:height.
    /// "battle.enemyOnTop": texture-name prefixes (e.g. "MN_FF5_127") whose enemy mesh draws above the other enemies
    /// (renderer sortingOrder = highest other enemy order + 1; the game's own order is kept as the base).
    /// </summary>
    /// "battle.partyRight" / "partyDown": moves every party member's sprite (CharaObject/Mesh) and its shadow
    /// (CharaObject/Shadow, shadow_bt [verified, F8 dump 2026-11-10]) by N px at 1440p (live). Needs hdPixelsPerUnit on.
    /// "battle.backRowShift": extra px (1440p) added to the game's own back-row distance for party members in the back
    /// row; negative = closer to the front row. Row = the member's x against its slot's front-row x (back row = +16
    /// units [verified, row-probe log 2026-11-12]), so the extra follows the game's own slide. Attempt 2 used
    /// Last.Battle.AnimStateController.isFrontCorps: it does not change with the Row command [verified, same log]. First attempt (2026-11-10) set the static BattleConstants.CorpsBackPlayerPositionX: no effect
    /// (user); its values (front 92, back 108, y spacing 16) do not match the party's world positions (x 98..68,
    /// y spacing 25) [verified, log + F8 dump], so the game does not place the party from them.
    static class BattleCharScale
    {
        internal static float Ppu = 6.667f;
        /// <summary>"battle.customScale": on-screen size factor for replaced party sprites drawn at game-art density
        /// (texture at most 2x the mesh, KupoUI name "..._Custom" [verified, F8 dump 2026-11-07]), e.g. 1.5 for the
        /// PSP sets kept at their real 64 px size (user, 2026-11-15). 1 = game size. HD art (texture wider than 2x the
        /// mesh) keeps the hdPixelsPerUnit sizing.</summary>
        internal static float CustomScale = 1f;
        /// <summary>"battle.miniScale": size of a party member under Mini, as a share of its normal on-screen size. The
        /// game's own Mini set is replaced by the member's normal job set (MiniPatches), so no Mini art is needed
        /// (user, 2026-11-15). Default 0.5 (vanilla Mini is 11-12 px tall vs 23-24 px [measured, Extracted M vs J00]).
        /// 0 = off: the game's own Mini sprites.</summary>
        internal static float MiniScale = 0.5f;
        internal static float PartyRight, PartyDown;
        /// <summary>"battle.partySlots": [[right, down], ...] px at 1440p for slots 1-4 (top first), added to partyRight/partyDown.</summary>
        internal static List<Vector2> PartySlots = new List<Vector2>();
        internal static float BackRowPx;
        /// <summary>
        /// "battle.depthScale": % smaller a sprite is with its feet at the top of the battle view than at the bottom,
        /// linear in between (party, enemies and their shadows; around the feet). 0 = off. Read from the sprite's own
        /// feet on screen, so the step forward (horizontal) keeps the size; a jump shrinks it a little [inferred].
        /// </summary>
        internal static float DepthScale;
        /// <summary>
        /// "battle.groundShadow": every party member and enemy casts a dark copy of its sprite onto the ground line,
        /// sheared by a sun-like light: azimuth = the screen direction the shadow falls (0 right, 90 up/away, 180 left,
        /// 270 down/toward the camera), elevation = the light's height (90 overhead = no length), groundTilt = screen
        /// height of one unit of ground depth (0..1), opacity 0..1. The ground line is the game's own shadow (it stays
        /// on the ground while Float lifts the body: Last.Battle.LevitetoConditionFunction moves characterTransform
        /// [verified, interop dump]; the shadow is a separate EntityMeshRenderer [verified, interop dump]).
        /// </summary>
        internal static bool GsOn, GsHideGame, GsDebug;
        internal static float GsAzimuth = 45f, GsElevation = 50f, GsOpacity = 0.45f, GsTilt = 0.5f;
        /// <summary>thickness: body depth as a share of the image width; footHeight: share of the image height that stays where it is drawn (the feet); blur: px at 1440p; falloff: 0..1 lighter towards the far end.</summary>
        internal static float GsThick = 0.25f, GsDepthRange = 0.6f, GsBlur = 4f, GsFalloff = 0.6f;
        static bool castBroken, footWarned;
        /// <summary>World units: a body whose feet never come closer than this to its game shadow's centre is flying [inferred].</summary>
        const float FlyGap = 4f;
        // Row from position [verified, row-probe log 2026-11-12]: AnimStateController.isFrontCorps does not follow
        // the Row command; the back row stands 16 units further right (78->94, 88->104) and each slot down the
        // screen stands 10 units further right (front row 68/78/88/98).
        const float BackUnits = 16f, SlotStagger = 10f;
        /// <summary>Front-row x of slot 1 this battle (BattlePlayer local x); NaN until the party is first seen.</summary>
        static float frontX0 = float.NaN;
        /// <summary>Current party offset (px at 1440p, right/down) per BattlePlayer, for the active-character arrow.</summary>
        static readonly Dictionary<IntPtr, Vector2> memberOffset = new Dictionary<IntPtr, Vector2>();
        static float unitsPerPx = 216f / 1440f;   // updated from CameraBattle (2 * orthographicSize units over 1440 px)
        internal static List<string> EnemyOnTop = new List<string>();

        class St { public Transform T; public MeshRenderer R; public MeshFilter F; public Vector3 BaseScale, SetScale, BasePos, SetPos; public bool On, Enemy, Shadow, OrderOn; public int BaseOrder, SetOrder, Slot; public float Back; public Transform PL; public int LogRow = -1; public float LogDepth = -1f; public Last.Battle.BattlePlayerEntity Ent; public float EdgeU0 = -1f, EdgeU1, EdgeTime = -10f; public IntPtr EdgeTex; public Vector4 Feet; public bool HasFeet;
            // groundShadow: the game's own shadow for this body; RestGap = smallest feet-to-game-shadow height
            // seen this battle (standing), so a lift above it (Float) detaches the shadow.
            public St GS; public string GsState;
            public float RestGap = float.MaxValue; public bool LiftLogged, HideOn;
            // groundShadow anchors: mesh-local points (sorted by x) and texture px; source "auto" or "file"
            public List<Vector2> Anch, AnchTexPts; public float AnchTime = -10f; public IntPtr AnchTex; public uint AnchUpd; public string AnchSrc; public long AnchStamp; public bool AnchLogged, UpdLogged, FlyHide; public int AnchW; public float LiftW; }
        /// <summary>A party member's weapon sprite (BattlePlayerEntity.BattleWeaponRight/Left [verified, interop dump]), depthScale only.</summary>
        class WSt { public Transform T; public Vector3 BasePos, BaseScale, SetPos, SetScale; public bool On, Logged; }
        static readonly Dictionary<IntPtr, WSt> weapons = new Dictionary<IntPtr, WSt>();

        /// <summary>Raw back-row amount from position: 0 = front row x, 1 = back row x (BattlePlayer local x).</summary>
        static float RawBack(St s) =>
            (s.PL.localPosition.x - (frontX0 + SlotStagger * (s.Slot - 1))) / BackUnits;

        /// <summary>
        /// 0 = front row, 1 = back row, following the game's own slide between them. Beyond the back-row x (entry
        /// run-in, running away) the last value is kept; an attack step forward lowers it towards 0 [inferred].
        /// </summary>
        static float BackAmount(St s)
        {
            if (s.PL == null || s.PL.WasCollected || float.IsNaN(frontX0)) return s.Back;
            float raw = RawBack(s);
            if (raw <= 1.1f) s.Back = Mathf.Clamp01(raw);
            if (!s.Shadow)
            {
                int row = s.Back >= 1f ? 1 : s.Back <= 0f ? 0 : -1;
                if (row >= 0 && row != s.LogRow)
                {
                    s.LogRow = row;
                    Plugin.L.LogWarning($"[Battle] party slot {s.Slot}: {(row == 1 ? "back" : "front")} row (x {s.PL.localPosition.x:0.#}, " +
                        $"front-row x {frontX0 + SlotStagger * (s.Slot - 1):0.#}), backRowShift {BackRowPx:0.#} px (info, not an error)");
                }
            }
            return s.Back;
        }

        /// <summary>
        /// The party offset (px at 1440p) of the party member that <paramref name="t"/> belongs to (BattlePlayer or a
        /// child of it); false if it is not part of a party member.
        /// </summary>
        /// <summary>BattlePlayerEntity.CorpsIndex -> BattlePlayer transform pointer (this battle).</summary>
        static readonly Dictionary<int, IntPtr> corpsMember = new Dictionary<int, IntPtr>();
        /// <summary>Cursor pointer -> corpsIndex it was last put on (-1 = an enemy), from BattleCursorUtility.</summary>
        internal static readonly Dictionary<IntPtr, int> cursorCorps = new Dictionary<IntPtr, int>();

        /// <summary>Party offset (px) of the member a battle cursor was last put on; false for enemies or unknown.</summary>
        internal static bool OffsetOfCursor(IntPtr cursor, out Vector2 px, out int corps)
        {
            px = Vector2.zero;
            if (!cursorCorps.TryGetValue(cursor, out corps) || corps < 0) return false;
            return corpsMember.TryGetValue(corps, out var pl) && memberOffset.TryGetValue(pl, out px);
        }

        /// <summary>
        /// St.Feet (party and enemy bodies): x, y = feet on screen (px at 1440p from the screen centre, y down), z =
        /// shown height / the game's (HD scale x depthScale), w = depthScale factor. The game puts its cursors for its
        /// own sprite: the arrow 32 units above a party member's feet [verified, F8 dump 2026-11-12 18:20].
        /// </summary>
        internal static bool FeetOfCursor(IntPtr cursor, out Vector4 feet)
        {
            feet = default;
            var s = BodyOfCursor(cursor);
            if (s == null || !s.HasFeet) return false;
            feet = s.Feet;
            return true;
        }

        /// <summary>Cursor pointer -> the target enemy's AnimStateController pointer, from BattleCursorUtility.SetTargetEnemy.</summary>
        internal static readonly Dictionary<IntPtr, IntPtr> cursorEnemy = new Dictionary<IntPtr, IntPtr>();
        /// <summary>
        /// Enemy AnimStateController pointer -> its body mesh state. BattleEnemyData.GetAnimStateController() (from
        /// BattleUnitData) and BattleSpriteEntity.AnimStateController [verified, interop dump] are matched as the same
        /// object [inferred; logged].
        /// </summary>
        static readonly Dictionary<IntPtr, St> enemyBody = new Dictionary<IntPtr, St>();

        /// <summary>The body (party member or enemy) a battle cursor was last put on; null if unknown.</summary>
        static St BodyOfCursor(IntPtr cursor)
        {
            if (!cursorCorps.TryGetValue(cursor, out var corps)) return null;
            if (corps >= 0) return corpsMember.TryGetValue(corps, out var pl) && memberBody.TryGetValue(pl, out var m) ? m : null;
            return cursorEnemy.TryGetValue(cursor, out var asc) && enemyBody.TryGetValue(asc, out var e) ? e : null;
        }

        static bool loggedEnemyLink;
        internal static void NoteEnemyCursor(IntPtr cursor, IntPtr asc)
        {
            cursorEnemy[cursor] = asc;
            if (!loggedEnemyLink)
            {
                loggedEnemyLink = true;
                Plugin.L.LogWarning($"[Battle] target hand on an enemy: {(enemyBody.ContainsKey(asc) ? "matched its sprite" : $"no sprite matched ({enemyBody.Count} enemies known), hand keeps the game's place")} (info, not an error)");
            }
        }

        /// <summary>BattlePlayer pointer -> its body mesh state (for the figure's edge).</summary>
        static readonly Dictionary<IntPtr, St> memberBody = new Dictionary<IntPtr, St>();
        static bool edgeWarned;

        /// <summary>
        /// Screen x (px at 1440p from the centre) of the visible figure's left edge, for the member a cursor is on:
        /// the texture's first column with alpha > 64, re-read at most once a second (KupoUI refills the same
        /// texture per pose [inferred]). Party members and enemies; false if unknown or the texture is unreadable.
        /// </summary>
        internal static bool EdgeOfCursor(IntPtr cursor, out float leftPx, out float u0)
        {
            leftPx = 0f; u0 = -1f;
            var s = BodyOfCursor(cursor);
            if (s == null) return false;
            if (s.T == null || s.T.WasCollected || s.R == null || s.R.WasCollected || s.F == null) return false;
            Camera battle = null;
            foreach (var c in Camera.allCameras) if (c.name == "CameraBattle") { battle = c; break; }
            var tex = s.R.sharedMaterial != null ? s.R.sharedMaterial.mainTexture : null;
            var mesh = s.F.sharedMesh;
            if (battle == null || tex == null || mesh == null) return false;
            if (Time.unscaledTime - s.EdgeTime > 1f || s.EdgeTex != tex.Pointer)
            {
                s.EdgeTime = Time.unscaledTime; s.EdgeTex = tex.Pointer;
                try { if (!AlphaColumns(tex, out s.EdgeU0, out s.EdgeU1)) s.EdgeU0 = -1f; }
                catch (Exception e)
                {
                    s.EdgeU0 = -1f;
                    if (!edgeWarned) { edgeWarned = true; Plugin.L.LogWarning($"[Battle] reading '{tex.name}' for the figure's edge failed (hand keeps the scaled place): " + e.Message); }
                }
            }
            if (s.EdgeU0 < 0f) return false;
            u0 = s.EdgeU0;
            var b = mesh.bounds;
            // u -> mesh x: from the mesh UVs if readable, else u 0..1 across the bounds [inferred].
            float xa = b.min.x, xb = b.max.x, ua = 0f, ub = 1f;
            try
            {
                var uv = mesh.uv; var v = mesh.vertices;
                if (uv != null && v != null && uv.Length == v.Length && uv.Length > 0)
                {
                    int iMin = 0, iMax = 0;
                    for (int i = 1; i < uv.Length; i++) { if (uv[i].x < uv[iMin].x) iMin = i; if (uv[i].x > uv[iMax].x) iMax = i; }
                    if (uv[iMax].x - uv[iMin].x > 0.0001f) { xa = v[iMin].x; xb = v[iMax].x; ua = uv[iMin].x; ub = uv[iMax].x; }
                }
            }
            catch { }
            float ym = b.center.y;
            float S(float u)
            {
                float lx = xa + (u - ua) / (ub - ua) * (xb - xa);
                var vp = battle.WorldToViewportPoint(s.T.TransformPoint(new Vector3(lx, ym, b.center.z)));
                return (vp.x - 0.5f) * 1440f * battle.aspect;
            }
            leftPx = Mathf.Min(S(s.EdgeU0), S(s.EdgeU1));
            return true;
        }

        /// <summary>The texture's pixels (row 0 = bottom); unreadable textures are copied through a render texture into <paramref name="tmp"/> (destroy it after).</summary>
        static Il2CppStructArray<Color32> Pixels(Texture tex, out Texture2D tmp)
        {
            tmp = null;
            int w = tex.width, h = tex.height;
            var t2 = tex.TryCast<Texture2D>();
            if (t2 != null && t2.isReadable) return t2.GetPixels32();
            // Not readable on the CPU: copy through a render texture.
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                tmp = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                return tmp.GetPixels32();
            }
            finally { RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt); }
        }

        /// <summary>First and last texture columns (as u, 0..1) holding a pixel with alpha > 64.</summary>
        static bool AlphaColumns(Texture tex, out float u0, out float u1)
        {
            u0 = u1 = -1f;
            int w = tex.width, h = tex.height;
            if (w <= 0 || h <= 0) return false;
            var px = Pixels(tex, out var tmp);
            try
            {
                int left = -1, right = -1;
                for (int x = 0; x < w && left < 0; x++) for (int y = 0; y < h; y++) if (px[y * w + x].a > 64) { left = x; break; }
                for (int x = w - 1; x >= 0 && right < 0; x--) for (int y = 0; y < h; y++) if (px[y * w + x].a > 64) { right = x; break; }
                if (left < 0 || right < 0) return false;
                u0 = (left + 0.5f) / w; u1 = (right + 0.5f) / w;
                return true;
            }
            finally { if (tmp != null) UnityEngine.Object.Destroy(tmp); }
        }

        internal static bool OffsetOf(Transform t, out Vector2 px)
        {
            px = Vector2.zero;
            for (int d = 0; d < 8 && t != null; d++, t = t.parent)
                if (t.name.StartsWith("BattlePlayer")) return memberOffset.TryGetValue(t.Pointer, out px);
            return false;
        }

        static readonly Dictionary<IntPtr, St> list = new Dictionary<IntPtr, St>();
        static float nextFind, nextFindE, lastSeen;
        static bool logged, inBattle;
        static Transform party, enemies;
        static bool loggedEnemy, loggedOrder, loggedShadow;

        internal static void Apply()
        {
            Camera battle = null;
            foreach (var c in Camera.allCameras) if (c.name == "CameraBattle") { battle = c; break; }
            if (battle != null && battle.orthographicSize > 0) unitsPerPx = 2f * battle.orthographicSize / 1440f;
            if (Ppu <= 0)
            {
                if (list.Count > 0) { foreach (var s in list.Values) Restore(s); list.Clear(); }
                GpuShadow.Frame(null, false, false);
                logged = false;
                return;
            }
            // CameraBattle blinks off and on during the battle-start transition [inferred: user saw the size flicker
            // for ~1 s with the same HD art]. Restoring then flickered, so the scale is kept; the characters are
            // destroyed when the battle ends. A battle counts as over after 2 s without the camera.
            if (battle == null)
            {
                if (inBattle && Time.unscaledTime - lastSeen > 2f) { inBattle = false; logged = false; loggedEnemy = false; loggedOrder = false; loggedShadow = false; frontX0 = float.NaN; memberOffset.Clear(); corpsMember.Clear(); cursorCorps.Clear(); weapons.Clear(); memberBody.Clear(); enemyBody.Clear(); cursorEnemy.Clear(); loggedEnemyLink = false; castBroken = false; GpuShadow.Frame(null, false, false); }
            }
            else lastSeen = Time.unscaledTime;
            // Battle just started: search now and then every 0.2 s for 6 s (user: a 3 s search interval left the
            // characters small for 2-3 s at battle start), then every 3 s (scene search ~17 ms).
            if (!inBattle && battle != null) { inBattle = true; nextFind = 0f; }
            // The party's parent (RootObject/Entity/PlayerParty) is found once (GameObject.Find); its children are then
            // checked every frame, so a new character is scaled in its first frame (a timed scene search left it small
            // for up to 0.2 s: user saw flicker at battle start).
            if ((party == null || party.WasCollected) && battle != null && Time.unscaledTime >= nextFind)
            {
                nextFind = Time.unscaledTime + 0.2f;
                var go = GameObject.Find("RootObject/Entity/PlayerParty");
                party = go != null ? go.transform : null;
            }
            if ((enemies == null || enemies.WasCollected) && battle != null && Time.unscaledTime >= nextFindE)
            {
                nextFindE = Time.unscaledTime + 0.2f;
                var go = GameObject.Find("RootObject/Entity/EnemyParty");
                enemies = go != null ? go.transform : null;
            }
            if (enemies != null && !enemies.WasCollected)
                for (int i = 0; i < enemies.childCount; i++)
                {
                    var en = enemies.GetChild(i);
                    if (en == null || !en.name.StartsWith("BattleEnemy")) continue;
                    var co = en.Find("CharaObject");
                    var mt = co != null ? co.Find("Mesh") : null;
                    if (mt == null || list.ContainsKey(mt.Pointer)) continue;
                    var r = mt.GetComponent<MeshRenderer>();
                    St est = null;
                    if (r != null)
                    {
                        est = new St { T = mt, R = r, F = mt.GetComponent<MeshFilter>(), Enemy = true };
                        list[mt.Pointer] = est;
                        try
                        {
                            var ee = en.GetComponentsInChildren<Last.Battle.BattleEnemyEntity>(true);
                            if (ee != null && ee.Length > 0 && ee[0].AnimStateController != null) enemyBody[ee[0].AnimStateController.Pointer] = est;
                        }
                        catch (Exception e) { Plugin.L.LogWarning("[Battle] BattleEnemyEntity not readable (enemy hand keeps the game's place): " + e.Message); }
                    }
                    // The enemy's shadow (ShadowObject/EnemyShadow, MeshRenderer, texture MN_FF5_nnn_Shadow [verified, F8
                    // dump 2026-11-10]) is a separate mesh; HD shadows are sized the same way as the body.
                    var so = en.Find("ShadowObject");
                    var sh = so != null ? so.Find("EnemyShadow") : null;
                    if (sh != null && !list.ContainsKey(sh.Pointer))
                    {
                        var sr = sh.GetComponent<MeshRenderer>();
                        if (sr != null)
                        {
                            var gst = new St { T = sh, R = sr, F = sh.GetComponent<MeshFilter>(), Enemy = true, Shadow = true };
                            list[sh.Pointer] = gst;
                            if (est != null) est.GS = gst;
                        }
                    }
                }
            if (party != null && !party.WasCollected)
                for (int i = 0; i < party.childCount; i++)
                {
                    var pl = party.GetChild(i);
                    if (pl == null || !pl.name.StartsWith("BattlePlayer")) continue;
                    var co = pl.Find("CharaObject");
                    var mt = co != null ? co.Find("Mesh") : null;
                    if (mt == null || list.ContainsKey(mt.Pointer)) continue;
                    var r = mt.GetComponent<MeshRenderer>();
                    // Slot = rank by height when first seen (1 = top of the screen); fixed for the battle, so a jump
                    // or a step forward never swaps slots [inferred: the formation is placed before the battle shows].
                    int slot = 1;
                    for (int j = 0; j < party.childCount; j++)
                    {
                        var o = party.GetChild(j);
                        if (o != null && o.Pointer != pl.Pointer && o.name.StartsWith("BattlePlayer") && o.position.y > pl.position.y) slot++;
                    }
                    // Slot 1's front-row x = the smallest (x - stagger) of the members present when the party first
                    // shows; they stand at their places then [verified, row-probe log: 68/78/104/98 before the run-in].
                    // If every member is in the back row, all count as front row this battle (no shift) [inferred].
                    if (float.IsNaN(frontX0))
                    {
                        float min = float.MaxValue;
                        for (int j = 0; j < party.childCount; j++)
                        {
                            var o = party.GetChild(j);
                            if (o == null || !o.name.StartsWith("BattlePlayer")) continue;
                            int sl = 1;
                            for (int k2 = 0; k2 < party.childCount; k2++)
                            {
                                var o2 = party.GetChild(k2);
                                if (o2 != null && o2.Pointer != o.Pointer && o2.name.StartsWith("BattlePlayer") && o2.position.y > o.position.y) sl++;
                            }
                            min = Mathf.Min(min, o.localPosition.x - SlotStagger * (sl - 1));
                        }
                        if (min < float.MaxValue) frontX0 = min;
                    }
                    var ms = new St { T = mt, R = r, F = mt.GetComponent<MeshFilter>(), Slot = slot, PL = pl };
                    int corps = -1;
                    try
                    {
                        var ents = pl.GetComponentsInChildren<Last.Battle.BattlePlayerEntity>(true);
                        if (ents != null && ents.Length > 0) { corps = ents[0].CorpsIndex; corpsMember[corps] = pl.Pointer; ms.Ent = ents[0]; }
                    }
                    catch (Exception e) { Plugin.L.LogWarning("[Battle] BattlePlayerEntity not readable (arrow keeps the game's place): " + e.Message); }
                    ms.Back = float.IsNaN(frontX0) ? 0f : Mathf.Clamp01(RawBack(ms));
                    if (r != null) { list[mt.Pointer] = ms; memberBody[pl.Pointer] = ms; }
                    // The party shadow (CharaObject/Shadow) only follows the position offsets; it is not resized.
                    var ps = co.Find("Shadow");
                    if (ps != null && !list.ContainsKey(ps.Pointer))
                    {
                        var psr = ps.GetComponent<MeshRenderer>();
                        if (psr != null)
                        {
                            var gst = new St { T = ps, R = psr, F = ps.GetComponent<MeshFilter>(), Shadow = true, Slot = slot, PL = pl, Back = ms.Back };
                            list[ps.Pointer] = gst;
                            ms.GS = gst;
                        }
                    }
                    Plugin.L.LogWarning($"[Battle] party slot {slot}: child {i}, x {pl.localPosition.x:0.#} y {pl.localPosition.y:0.#}, " +
                        $"{(ms.Back >= 0.5f ? "back" : "front")} row (slot 1 front-row x {frontX0:0.#}), corpsIndex {corps} (info, not an error)");
                }
            List<IntPtr> dead = null;
            foreach (var kv in list)
            {
                var s = kv.Value;
                if (s.T == null || s.T.WasCollected || s.R == null || s.R.WasCollected) { (dead ??= new List<IntPtr>()).Add(kv.Key); continue; }
                var mesh = s.F != null ? s.F.sharedMesh : null;
                var tex = s.R.sharedMaterial != null ? s.R.sharedMaterial.mainTexture : null;
                if (mesh == null || tex == null) continue;
                var b = mesh.bounds; // local units
                float meshW = Mathf.Max(0.001f, b.size.x);
                float meshH = Mathf.Max(0.001f, b.size.y);
                bool partyShadow = s.Shadow && !s.Enemy;
                float k = !partyShadow && tex.width > 2f * meshW ? (tex.width / Ppu) / meshW : 1f;
                if (k == 1f && !s.Enemy && !partyShadow && CustomScale != 1f && tex.name != null && tex.name.EndsWith("_Custom"))
                    k = CustomScale;
                if (!s.Enemy && !s.Shadow && MiniScale > 0f && MiniPatches.IsMini(s.Ent))
                {
                    k *= MiniScale;
                    MiniPatches.Log($"party slot {s.Slot} '{tex.name}' under Mini: shown x{k:0.###} (miniScale {MiniScale:0.###})");
                }
                float ky = k;
                if (s.Enemy && k != 1f) ky = (tex.height / Ppu) / meshH;   // enemies: per axis (HD art may be wider)
                // depthScale: feet height on screen (0 bottom, 1 top of the battle view). The feet stay put under the
                // scaling below, so reading them from the current transform gives no feedback loop.
                float f = 1f;
                if (DepthScale > 0f && battle != null)
                {
                    // A lifted body (flyer, Float) is higher, not farther: its size comes from the ground under it
                    // (user, 2026-11-13); LiftW is last frame's lift from groundShadow (0 when that is off).
                    var feetD = s.T.TransformPoint(new Vector3(b.center.x, b.min.y, b.center.z));
                    // Height also brings it closer to the camera (user, 2026-11-13): with the ground seen at a slant
                    // (groundTilt t = sin of the view's tilt), rising h comes h x cos toward the camera, which the
                    // depth look reads like standing h x cos x t further down the screen. So the size is read at
                    // ground point - lift x t x sqrt(1 - t^2) [inferred geometry; t = 0.5 -> 0.43 x lift].
                    if (!s.Shadow && GsOn) feetD.y -= s.LiftW * (1f + GsTilt * Mathf.Sqrt(Mathf.Max(0f, 1f - GsTilt * GsTilt)));
                    float vy = Mathf.Clamp01(battle.WorldToViewportPoint(feetD).y);
                    f = 1f - DepthScale / 100f * vy;
                    if (!s.Shadow && s.LogDepth != DepthScale)
                    {
                        s.LogDepth = DepthScale;
                        Plugin.L.LogWarning($"[Battle] depthScale {DepthScale:0.#}: {(s.Enemy ? "enemy" : $"party slot {s.Slot}")} '{tex.name}' feet at view height {vy:0.##} -> size x{f:0.###} (info, not an error)");
                    }
                }
                var sc = s.T.localScale; var pos = s.T.localPosition;
                if (!s.On || sc != s.SetScale) s.BaseScale = sc;   // the game's own scale (or the game changed it)
                if (!s.On || pos != s.SetPos) s.BasePos = pos;
                s.SetScale = new Vector3(s.BaseScale.x * k * f, s.BaseScale.y * ky * f, s.BaseScale.z);
                // Keep the mesh's bottom edge (feet) and, for enemies, its horizontal centre where they were: scaling
                // around the object's origin moves them by (1-k)*minY and (1-k)*centreX. depthScale then scales
                // around the (already scaled) centre x for the party too.
                float cx = b.center.x * s.BaseScale.x;
                s.SetPos = s.BasePos + new Vector3(s.Enemy ? (1f - k * f) * cx : (1f - f) * k * cx, (1f - ky * f) * b.min.y * s.BaseScale.y, 0f);
                if (!s.Enemy)
                {
                    float dx = PartyRight, dy = PartyDown;
                    if (s.Slot >= 1 && s.Slot <= PartySlots.Count) { dx += PartySlots[s.Slot - 1].x; dy += PartySlots[s.Slot - 1].y; }
                    dx += BackRowPx * BackAmount(s);
                    if (!s.Shadow && s.PL != null && !s.PL.WasCollected) memberOffset[s.PL.Pointer] = new Vector2(dx, dy);
                    if (dx != 0f || dy != 0f)
                    {
                        // px at 1440p -> battle units -> the parent's local units
                        var ps2 = s.T.parent != null ? s.T.parent.lossyScale : Vector3.one;
                        s.SetPos += new Vector3(dx * unitsPerPx / Mathf.Max(0.0001f, Mathf.Abs(ps2.x)), -dy * unitsPerPx / Mathf.Max(0.0001f, Mathf.Abs(ps2.y)), 0f);
                    }
                }
                if (sc != s.SetScale) s.T.localScale = s.SetScale;
                if (pos != s.SetPos) s.T.localPosition = s.SetPos;
                s.On = true;
                if (!s.Shadow && battle != null)
                {
                    // The battle render texture fills the screen [inferred: cursor x matched the sprites, 2026-11-12].
                    var vpf = battle.WorldToViewportPoint(s.T.TransformPoint(new Vector3(b.center.x, b.min.y, b.center.z)));
                    s.Feet = new Vector4((vpf.x - 0.5f) * 1440f * battle.aspect, (0.5f - vpf.y) * 1440f, ky * f, f);
                    s.HasFeet = true;
                }
                if (s.Shadow) HideGameShadow(s);
                else if (GsOn && !castBroken)
                    try { Ground(s, b, mesh, tex); }
                    catch (Exception e) { castBroken = true; Plugin.L.LogWarning("[Battle] groundShadow failed (switched off until the next battle): " + e); }
                if (!s.Enemy && !s.Shadow && s.Ent != null)
                    try
                    {
                        var feet = s.T.TransformPoint(new Vector3(b.center.x, b.min.y, b.center.z));
                        // The weapon stays at the game's place for the member (CharaObject/BattleWeapon, a sibling of
                        // the mesh): it needs the member's offset too [verified, log 2026-11-12: weapon x from the
                        // feet = minus the partySlots/backRowShift offset]. px at 1440p -> world units.
                        memberOffset.TryGetValue(s.PL.Pointer, out var mo);
                        var off = new Vector3(mo.x * unitsPerPx, -mo.y * unitsPerPx, 0f);
                        // The weapon also gets the body's HD size factor k (user, 2026-11-14: weapons looked far too
                        // small next to 1.5x PSP bodies; they had depthScale only), around the same feet.
                        Weapon(s, s.Ent.BattleWeaponRight, f * k, feet, off);
                        Weapon(s, s.Ent.BattleWeaponLeft, f * k, feet, off);
                    }
                    catch (Exception e) { s.Ent = null; Plugin.L.LogWarning("[Battle] depthScale: weapon scaling failed (weapons keep the game's size): " + e.Message); }
                if (s.Shadow && !loggedShadow && k != 1f)
                {
                    loggedShadow = true;
                    Plugin.L.LogWarning($"[Battle] HD enemy shadow '{tex.name}' {tex.width}x{tex.height} on a {b.size.x:0.#}x{b.size.y:0.#}-unit mesh: scale x{k:0.###} y{ky:0.###} (info, not an error)");
                }
                if (s.Enemy && !s.Shadow && !loggedEnemy && k != 1f)
                {
                    loggedEnemy = true;
                    Plugin.L.LogWarning($"[Battle] HD enemy '{tex.name}' {tex.width}x{tex.height} on a {b.size.x:0.#}x{b.size.y:0.#}-unit mesh: scale x{k:0.###} y{ky:0.###} (info, not an error)");
                }
                if (!s.Enemy && !logged && k != 1f)
                {
                    logged = true;
                    Plugin.L.LogWarning($"[Battle] HD character '{tex.name}' {tex.width}x{tex.height} on a {b.size.x:0.#}x{b.size.y:0.#}-unit mesh (bottom {b.min.y:0.#}): scale x{k:0.###} = {(battle == null ? 0f : tex.height / Ppu * 1440f / (2f * battle.orthographicSize)):0} px at 1440p (info, not an error)");
                }
            }
            if (dead != null) foreach (var d in dead) list.Remove(d);
            try { GpuShadow.Frame(battle, GsOn && !castBroken, GsDebug); }
            catch (Exception e) { castBroken = true; Plugin.L.LogWarning("[Battle] groundShadow GPU pass failed (switched off until the next battle): " + e); try { GpuShadow.Frame(null, false, false); } catch { } }
            try { EnemyOrder(); } catch (Exception e) { Plugin.L.LogWarning("[Battle] enemyOnTop failed: " + e.Message); }
        }

        /// <summary>
        /// depthScale and HD size for a weapon: the game's own place and scale (re-read whenever the game sets them)
        /// scaled by the member's factor (depthScale x HD body scale) around the member's feet, so the weapon stays
        /// in the scaled hand [inferred: the art's hand must sit where the scaled vanilla hand would]. Weapons are not under
        /// CharaObject/Mesh [inferred: user saw them keep full size with depthScale on, 2026-11-12].
        /// </summary>
        static void Weapon(St s, Last.Battle.BattleWeapon w, float f, Vector3 feet, Vector3 off)
        {
            if (w == null) return;
            var t = w.transform;
            if (t == null) return;
            if (!weapons.TryGetValue(t.Pointer, out var ws)) { ws = new WSt { T = t }; weapons[t.Pointer] = ws; }
            var lp = t.localPosition; var ls = t.localScale;
            if (!ws.On || lp != ws.SetPos) ws.BasePos = lp;
            if (!ws.On || ls != ws.SetScale) ws.BaseScale = ls;
            if (f == 1f && off == Vector3.zero)
            {
                if (ws.On) { if (lp == ws.SetPos) t.localPosition = ws.BasePos; if (ls == ws.SetScale) t.localScale = ws.BaseScale; ws.On = false; }
                return;
            }
            var par = t.parent;
            var baseWorld = par != null ? par.TransformPoint(ws.BasePos) : ws.BasePos;
            var world = feet + (baseWorld + off - feet) * f;
            world.z = baseWorld.z;
            ws.SetPos = par != null ? par.InverseTransformPoint(world) : world;
            ws.SetScale = new Vector3(ws.BaseScale.x * f, ws.BaseScale.y * f, ws.BaseScale.z);
            t.localScale = ws.SetScale; t.localPosition = ws.SetPos;
            ws.On = true;
            if (!ws.Logged)
            {
                ws.Logged = true;
                var d = baseWorld + off - feet;
                Plugin.L.LogWarning($"[Battle] party slot {s.Slot} weapon '{t.name}': party offset {off.x:0.#}, {off.y:0.#} units added, then {d.x:0.#}, {d.y:0.#} units from the feet -> size x{f:0.###} (info, not an error)");
            }
        }

        /// <summary>Linear map between the body mesh's UV rectangle and its local x/y (from the vertices at the UV extremes).</summary>
        struct UvMap
        {
            public float Ua, Ub, Xa, Xb, Va, Vb, Ya, Yb;
            public bool Ok;
            public float X(float u) => Xa + (u - Ua) / (Ub - Ua) * (Xb - Xa);
            public float U(float x) => Ua + (x - Xa) / (Xb - Xa) * (Ub - Ua);
            public float Y(float v) => Ya + (v - Va) / (Vb - Va) * (Yb - Ya);
            public float V(float y) => Va + (y - Ya) / (Yb - Ya) * (Vb - Va);
            public float UMin => Mathf.Min(Ua, Ub); public float UMax => Mathf.Max(Ua, Ub);
            public float VMin => Mathf.Min(Va, Vb); public float VMax => Mathf.Max(Va, Vb);
            public static UvMap Of(Il2CppStructArray<Vector3> v, Il2CppStructArray<Vector2> uv)
            {
                var m = new UvMap();
                if (v == null || uv == null || v.Length != uv.Length || v.Length < 3) return m;
                int iu0 = 0, iu1 = 0, iv0 = 0, iv1 = 0;
                for (int i = 1; i < uv.Length; i++)
                {
                    if (uv[i].x < uv[iu0].x) iu0 = i; if (uv[i].x > uv[iu1].x) iu1 = i;
                    if (uv[i].y < uv[iv0].y) iv0 = i; if (uv[i].y > uv[iv1].y) iv1 = i;
                }
                m.Ua = uv[iu0].x; m.Ub = uv[iu1].x; m.Xa = v[iu0].x; m.Xb = v[iu1].x;
                m.Va = uv[iv0].y; m.Vb = uv[iv1].y; m.Ya = v[iv0].y; m.Yb = v[iv1].y;
                m.Ok = m.Ub - m.Ua > 0.0001f && m.Vb - m.Va > 0.0001f && Mathf.Abs(m.Xb - m.Xa) > 0.0001f && Mathf.Abs(m.Yb - m.Ya) > 0.0001f;
                return m;
            }
        }

        /// <summary>
        /// Two ground points (texture px, x from the left, y from the top; the pixel's bottom edge touches the ground)
        /// found from the alpha: per column the lowest pixel with alpha > 64, kept only in the bottom 30 % of the
        /// figure (the feet), then the edge of their lower convex hull that spans the middle of that band. A standing
        /// pose gives the bottom of each foot; the line's slope is limited to 1:2 (a raised foot keeps the lower one).
        /// </summary>
        static List<Vector2> AutoAnchors(Il2CppStructArray<Color32> px, int w, int h, int x0, int x1, int y0, int y1)
        {
            var res = new List<Vector2>();
            var bottom = new int[x1 - x0 + 1];
            int lo = int.MaxValue, hi = int.MinValue;
            for (int x = x0; x <= x1; x++)
            {
                int bt = -1;
                for (int y = y0; y <= y1; y++) if (px[y * w + x].a > 64) { bt = y; break; }
                bottom[x - x0] = bt;
                if (bt < 0) continue;
                lo = Math.Min(lo, bt);
                for (int y = y1; y >= bt; y--) if (px[y * w + x].a > 64) { hi = Math.Max(hi, y); break; }
            }
            if (lo == int.MaxValue) return res;
            int band = lo + Math.Max(1, (int)((hi - lo) * 0.3f));
            var pts = new List<Vector2>();
            for (int x = x0; x <= x1; x++) { int bt = bottom[x - x0]; if (bt >= 0 && bt <= band) pts.Add(new Vector2(x, bt)); }
            if (pts.Count == 0) return res;
            // lower hull (smallest y = lowest on screen), left to right
            var hull = new List<Vector2>();
            foreach (var p in pts)
            {
                while (hull.Count >= 2)
                {
                    var a = hull[hull.Count - 2]; var b = hull[hull.Count - 1];
                    if ((b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x) <= 0f) hull.RemoveAt(hull.Count - 1); else break;
                }
                hull.Add(p);
            }
            float xc = (pts[0].x + pts[pts.Count - 1].x) * 0.5f;
            Vector2 pa = hull[0], pb = hull[hull.Count - 1];
            for (int i = 0; i + 1 < hull.Count; i++) if (hull[i].x <= xc && hull[i + 1].x >= xc) { pa = hull[i]; pb = hull[i + 1]; break; }
            float dx = pb.x - pa.x, dy = pb.y - pa.y;
            if (dx > 0f && Mathf.Abs(dy) > 0.5f * dx)
            {
                if (pa.y <= pb.y) pb.y = pa.y + 0.5f * dx; else pa.y = pb.y + 0.5f * dx;
            }
            // texture rows count from the bottom; the file and the log use rows from the top
            res.Add(new Vector2(pa.x, h - 1 - pa.y));
            if (dx > 0f) res.Add(new Vector2(pb.x, h - 1 - pb.y));
            return res;
        }

        /// <summary>An enemy's anchor file (enemies/.../&lt;texture&gt;.anchors.json), re-read when it changes.</summary>
        class AnchorFile { public DateTime Time; public float Checked = -10f; public bool Exists, Written; public int W, H; public List<Vector2> Points = new List<Vector2>(); }
        static readonly Dictionary<string, AnchorFile> anchorFiles = new Dictionary<string, AnchorFile>();
        /// <summary>
        /// enemies/&lt;texture without _Cnn&gt;/&lt;texture&gt;.anchors.json, beside the HD enemy art (enemies/MN_FF5_028/MN_FF5_028_C00.png
        /// [verified, game folder 2026-11-12]). Not &lt;texture&gt;.json: KupoUI may read a same-named .json as the sprite's sidecar [inferred].
        /// </summary>
        static string AnchorPath(string tex)
        {
            var mm = System.Text.RegularExpressions.Regex.Match(tex, @"^(.*)_C\d+$");
            string folder = mm.Success ? mm.Groups[1].Value : tex;
            return Path.Combine(Path.GetDirectoryName(Plugin.ConfigPath) ?? "", "enemies", folder, tex + ".anchors.json");
        }

        static AnchorFile ReadAnchorFile(string tex)
        {
            if (!anchorFiles.TryGetValue(tex, out var af)) { af = new AnchorFile(); anchorFiles[tex] = af; }
            if (Time.unscaledTime - af.Checked < 2f) return af;
            af.Checked = Time.unscaledTime;
            string p = AnchorPath(tex);
            if (!File.Exists(p)) { af.Exists = false; return af; }
            var t = File.GetLastWriteTimeUtc(p);
            if (af.Exists && t == af.Time) return af;
            af.Time = t; af.Exists = true; af.Points = new List<Vector2>(); af.W = af.H = 0;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(p), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var root = doc.RootElement;
                if (root.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Array && sz.GetArrayLength() == 2) { af.W = sz[0].GetInt32(); af.H = sz[1].GetInt32(); }
                if (root.TryGetProperty("points", out var pa) && pa.ValueKind == JsonValueKind.Array)
                    foreach (var e in pa.EnumerateArray())
                    {
                        if (af.Points.Count >= 8) { Plugin.L.LogWarning($"[Battle] groundShadow: {tex}.anchors.json has more than 8 points; the rest are ignored"); break; }
                        if (e.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.False) continue;
                        if (e.TryGetProperty("x", out var x) && x.ValueKind == JsonValueKind.Number && e.TryGetProperty("y", out var y) && y.ValueKind == JsonValueKind.Number)
                            af.Points.Add(new Vector2(x.GetSingle(), y.GetSingle()));
                    }
                Plugin.L.LogWarning($"[Battle] groundShadow: read {tex}.anchors.json: {af.Points.Count} point(s) on (info, not an error)");
            }
            catch (Exception e) { af.Points.Clear(); Plugin.L.LogWarning($"[Battle] groundShadow: {tex}.anchors.json not readable (auto points used): " + e.Message); }
            return af;
        }

        /// <summary>Writes a starter enemies/.../&lt;texture&gt;.anchors.json (never over an existing file): the auto points on, six spare points off.</summary>
        static void WriteAnchorTemplate(string tex, int w, int h, List<Vector2> auto)
        {
            try
            {
                string p = AnchorPath(tex);
                if (File.Exists(p)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                var sb = new System.Text.StringBuilder();
                sb.Append($"{{ \"_about\": \"groundShadow anchor points for {tex} ({w} x {h} px). x, y = texture pixel from the top-left (as in Photoshop); the bottom of each 'on' pixel touches the ground, joined left to right. Up to 8 points. Live. README: groundShadow.\",\n");
                sb.Append($"  \"texture\": \"{tex}\",\n  \"size\": [{w}, {h}],\n  \"points\": [\n");
                for (int i = 0; i < 8; i++)
                {
                    bool on = i < auto.Count;
                    var q = on ? auto[i] : Vector2.zero;
                    sb.Append($"    {{ \"on\": {(on ? "true" : "false")}, \"x\": {Mathf.RoundToInt(q.x)}, \"y\": {Mathf.RoundToInt(q.y)} }}{(i < 7 ? "," : "")}\n");
                }
                sb.Append("  ] }\n");
                File.WriteAllText(p, sb.ToString());
                Plugin.L.LogWarning($"[Battle] groundShadow: wrote starter {tex}.anchors.json with {auto.Count} auto point(s) (info, not an error)");
            }
            catch (Exception e) { Plugin.L.LogWarning($"[Battle] groundShadow: could not write {tex}.anchors.json: " + e.Message); }
        }

        /// <summary>
        /// The body's ground points in mesh-local units, sorted left to right. Enemies: enemies/.../&lt;texture&gt;.anchors.json
        /// ('on' points; scaled if the texture size differs from the file's "size"), else auto points (and a starter
        /// file is written). Party: auto points, re-read when the texture is refilled (Texture.updateCount [verified,
        /// interop dump]; whether KupoUI's refill raises it is [unverified]) or at most once a second.
        /// </summary>
        static List<Vector2> Anchors(St s, Texture tex, Bounds b, UvMap m)
        {
            int w = tex.width, h = tex.height;
            List<Vector2> texPts = null;
            if (s.Enemy)
            {
                var af = ReadAnchorFile(tex.name);
                if (af.Exists && af.Points.Count > 0)
                {
                    float fx = af.W > 0 ? (float)w / af.W : 1f, fy = af.H > 0 ? (float)h / af.H : 1f;
                    texPts = new List<Vector2>();
                    foreach (var q in af.Points) texPts.Add(new Vector2((q.x + 0.5f) * fx - 0.5f, (q.y + 1f) * fy - 1f));
                    if (s.AnchSrc != "file" || s.AnchStamp != af.Time.Ticks) { s.AnchSrc = "file"; s.AnchStamp = af.Time.Ticks; s.AnchLogged = false; }
                }
            }
            if (texPts == null && !s.Enemy)
            {
                // Party: the mesh bottom; the GPU finds the soles from the outline (steps 3-4), so no pixel reads.
                if (s.AnchSrc != "mesh") { s.AnchSrc = "mesh"; s.AnchLogged = false; }
                s.AnchTexPts = null;
                s.Anch = new List<Vector2> { new Vector2(b.min.x, b.min.y), new Vector2(b.max.x, b.min.y) };
                return s.Anch;
            }
            if (texPts == null)
            {
                uint upd = 0; try { upd = tex.updateCount; } catch { }
                if (!s.Enemy && !s.UpdLogged && s.Anch != null && s.AnchTex == tex.Pointer && upd != s.AnchUpd)
                {
                    s.UpdLogged = true;
                    Plugin.L.LogWarning($"[Battle] groundShadow party slot {s.Slot}: texture '{tex.name}' updateCount changed ({s.AnchUpd} -> {upd}): pose refills are seen at once (info, not an error)");
                }
                if (s.AnchW != w) { s.AnchW = w; s.AnchLogged = false; }
                bool fresh =s.Anch != null && s.AnchTex == tex.Pointer && s.AnchUpd == upd && s.AnchSrc == "auto" && (s.Enemy || Time.unscaledTime - s.AnchTime <= 1f);
                if (fresh) return s.Anch;
                s.AnchTex = tex.Pointer; s.AnchUpd = upd; s.AnchTime = Time.unscaledTime;
                if (s.AnchSrc != "auto") { s.AnchSrc = "auto"; s.AnchLogged = false; }
                texPts = new List<Vector2>();
                try
                {
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(m.UMin * w), 0, w - 1), x1 = Mathf.Clamp(Mathf.CeilToInt(m.UMax * w) - 1, x0, w - 1);
                    int y0 = Mathf.Clamp(Mathf.FloorToInt(m.VMin * h), 0, h - 1), y1 = Mathf.Clamp(Mathf.CeilToInt(m.VMax * h) - 1, y0, h - 1);
                    var px = Pixels(tex, out var tmp);
                    try { texPts = AutoAnchors(px, w, h, x0, x1, y0, y1); }
                    finally { if (tmp != null) UnityEngine.Object.Destroy(tmp); }
                }
                catch (Exception e)
                {
                    if (!footWarned) { footWarned = true; Plugin.L.LogWarning($"[Battle] groundShadow: reading '{tex.name}' for the feet failed (mesh bottom used): " + e.Message); }
                }
                if (s.Enemy && texPts.Count > 0)
                {
                    var af = ReadAnchorFile(tex.name);
                    if (!af.Exists && !af.Written) { af.Written = true; WriteAnchorTemplate(tex.name, w, h, texPts); }
                }
            }
            s.AnchTex = tex.Pointer;
            // texture px (top-left, bottom edge of the pixel) -> uv -> mesh-local
            var loc = new List<Vector2>();
            foreach (var q in texPts) loc.Add(new Vector2(m.X((q.x + 0.5f) / w), m.Y((h - 1 - q.y) / (float)h)));
            if (loc.Count == 0) loc.Add(new Vector2(b.center.x, b.min.y));
            loc.Sort((a, c) => a.x.CompareTo(c.x));
            s.Anch = loc; s.AnchTexPts = texPts;
            return loc;
        }

        /// <summary>Ground height (mesh-local y) under local x: the anchor polyline, flat beyond its ends.</summary>
        static float GroundAt(List<Vector2> a, float x)
        {
            if (a.Count == 1 || x <= a[0].x) return a[0].y;
            if (x >= a[a.Count - 1].x) return a[a.Count - 1].y;
            int i = 0;
            while (i < a.Count - 2 && x > a[i + 1].x) i++;
            var p = a[i]; var q = a[i + 1];
            return q.x - p.x < 0.0001f ? p.y : p.y + (x - p.x) / (q.x - p.x) * (q.y - p.y);
        }

        /// <summary>
        /// groundShadow (GPU, step 2): lists the body for GpuShadow's shadow buffer with its ground points (world) and
        /// lift. No mesh is built: the GPU draws the game's own renderer. Lift: lowest ground point against the game
        /// shadow's centre (see FlyGap); a lifted body's ground points are lowered by the lift, so the gap counts as
        /// part of the sprite (user, 2026-11-13).
        /// </summary>
        static void Ground(St s, Bounds b, Mesh mesh, Texture tex)
        {
            var gs = s.GS;
            bool gsAlive = gs != null && gs.T != null && !gs.T.WasCollected && gs.R != null && !gs.R.WasCollected;
            // The game hides a party member's round shadow once partySlots/backRowShift move it [verified, F8 dumps
            // 2026-11-12 09:11 vs 09:18: only the unmoved member kept it], so for the party only the body's
            // visibility counts; the shadow's transform still marks the ground (we move it with the body).
            bool show = s.R.enabled && s.T.gameObject.activeInHierarchy && (!gsAlive || !s.Enemy || (gs.R.enabled && gs.T.gameObject.activeInHierarchy));
            var m = show ? UvMap.Of(mesh.vertices, mesh.uv) : default;
            // diagnostics (step 2, Zu missing from the buffer): log whenever a body's listing state changes
            string state = show && m.Ok ? "listed"
                : !show ? $"skipped: body enabled {s.R.enabled}, active {s.T.gameObject.activeInHierarchy}" +
                          (gsAlive && s.Enemy ? $", game shadow enabled {gs.R.enabled}, active {gs.T.gameObject.activeInHierarchy}" : "")
                : "skipped: mesh UV map unreadable";
            if (state != s.GsState)
            {
                s.GsState = state;
                Plugin.L.LogWarning($"[Battle] groundShadow {(s.Enemy ? $"enemy '{tex.name}'" : $"party slot {s.Slot}")}: {state} (frame {Time.frameCount}) (info, not an error)");
            }
            if (!show || !m.Ok) return;
            var anch = Anchors(s, tex, b, m);
            float cz = b.center.z;
            int n = Math.Min(8, anch.Count);
            var g = new Vector2[n];
            float lowW = float.MaxValue;
            for (int i = 0; i < n; i++) { var p = s.T.TransformPoint(new Vector3(anch[i].x, anch[i].y, cz)); g[i] = new Vector2(p.x, p.y); lowW = Mathf.Min(lowW, p.y); }
            float lift = 0f, gap = 0f;
            if (gsAlive)
            {
                var gm = gs.F != null ? gs.F.sharedMesh : null;
                var gc = gs.T.TransformPoint(gm != null ? gm.bounds.center : Vector3.zero);
                gap = lowW - gc.y;
                if (gap < s.RestGap) s.RestGap = gap;
                // Standing bodies sit about 2 units above their shadow's centre [verified, F8 dump 2026-11-12: party
                // mesh y -32, shadow y -34]; a flyer never comes down (Zu: 9.09 units [verified, log 2026-11-12]).
                // Up to FlyGap the smallest gap counts as standing (ground points pinned); above it the body is in
                // the air and the ground is the game's shadow itself.
                lift = s.RestGap <= FlyGap ? gap - s.RestGap : gap;
                if (lift < 1f) lift = 0f;   // noise (0.09 seen on a standing member, log 2026-11-13) is not a lift
                // A flyer's own round shadow is replaced by the cast one (user, 2026-11-13).
                gs.FlyHide = s.Enemy && s.RestGap > FlyGap;
            }
            s.LiftW = lift;                                           // the real height: depthScale sizing
            // Shadow gap (user, 2026-11-14, chosen in the simulator): the gap is empty space under the sprite, measured
            // to the BASE of the default shadow (a flyer's lift is to its centre; the base is half its height lower
            // [verified, F8 dump: EnemyShadow 7.81 units tall]), and doubled: the nearest shadow lands
            // 2 x gap / tan(elevation) from the ground point. The shader places the body with its lowest 3D point this
            // far above the ground.
            float liftEff = 0f;
            if (lift > 0f)
            {
                float toBase = gsAlive && s.Enemy && s.RestGap > FlyGap ? gs.R.bounds.size.y * 0.5f : 0f;
                liftEff = 2f * (lift + toBase);
            }
            if (liftEff > 0f) for (int i = 0; i < n; i++) g[i].y -= liftEff;
            GpuShadow.Casters.Add(new GpuShadow.Caster { R = s.R, Tex = tex, G = g, N = n, Lift = liftEff });

            string who = s.Enemy ? $"enemy '{tex.name}'" : $"party slot {s.Slot}";
            if (!s.AnchLogged)
            {
                s.AnchLogged = true;
                var tpl = s.AnchTexPts;
                var tp = tpl == null || tpl.Count == 0 ? "mesh bottom" : string.Join(" ", tpl.ConvertAll(q => $"({q.x:0},{q.y:0})"));
                Plugin.L.LogWarning($"[Battle] groundShadow {who}: {n} ground point(s) from {s.AnchSrc} at texture px {tp} ({tex.width}x{tex.height}), " +
                    (gsAlive ? $"game shadow {gap:0.##} units below the lowest ({(s.RestGap <= FlyGap ? "standing: points pinned" : "flying: ground = game shadow")}), " +
                               $"game shadow {(gs.R.enabled && gs.T.gameObject.activeInHierarchy ? "shown" : "hidden by the game")}"
                             : "no game shadow: ground = points") +
                    $"; lift {lift:0.##} units, shadow gap {liftEff:0.##} units (2 x gap to the default shadow's base) (info, not an error)");
            }
            if (gsAlive && !s.LiftLogged && s.RestGap <= FlyGap && gap - s.RestGap > 2f)
            {
                s.LiftLogged = true;
                Plugin.L.LogWarning($"[Battle] groundShadow {who}: lifted {gap - s.RestGap:0.#} units above its standing height (Float?); shadow stays on the ground (info, not an error)");
            }
        }

        /// <summary>groundShadow.hideGameShadows: the game's shadow stops drawing (Renderer.forceRenderingOff, which the game does not use [inferred]), so its own enabled state stays readable.</summary>
        static void HideGameShadow(St s)
        {
            bool want = GsOn && (GsHideGame || s.FlyHide) && !castBroken;
            if (want == s.HideOn) { if (want && !s.R.forceRenderingOff) s.R.forceRenderingOff = true; return; }
            s.R.forceRenderingOff = want;
            s.HideOn = want;
        }

        /// <summary>battle.enemyOnTop: listed enemies draw above the other enemies (renderer sortingOrder).</summary>
        static void EnemyOrder()
        {
            if (EnemyOnTop.Count == 0) return;
            int maxOther = int.MinValue;
            var top = new List<St>();
            foreach (var s in list.Values)
            {
                if (!s.Enemy || s.Shadow || s.R == null || s.R.WasCollected) continue;
                var tex = s.R.sharedMaterial != null ? s.R.sharedMaterial.mainTexture : null;
                string n = tex != null ? tex.name : "";
                bool isTop = false;
                foreach (var p in EnemyOnTop) if (p.Length > 0 && n.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { isTop = true; break; }
                int cur = s.R.sortingOrder;
                if (!s.OrderOn || cur != s.SetOrder) s.BaseOrder = cur;   // the game's own order
                if (isTop) top.Add(s);
                else
                {
                    if (s.OrderOn && cur == s.SetOrder) s.R.sortingOrder = s.BaseOrder;   // no longer listed
                    s.OrderOn = false;
                    maxOther = Math.Max(maxOther, s.BaseOrder);
                }
            }
            if (top.Count == 0 || maxOther == int.MinValue) return;
            foreach (var s in top)
            {
                int want = Math.Max(s.BaseOrder, maxOther + 1);
                s.SetOrder = want; s.OrderOn = true;
                if (s.R.sortingOrder != want) s.R.sortingOrder = want;
            }
            if (!loggedOrder)
            {
                loggedOrder = true;
                Plugin.L.LogWarning($"[Battle] enemyOnTop: {top.Count} enemy mesh(es) set to sortingOrder {maxOther + 1} (others up to {maxOther}) (info, not an error)");
            }
        }

        static void Restore(St s)
        {
            if (s.HideOn) try { if (s.R != null && !s.R.WasCollected) s.R.forceRenderingOff = false; s.HideOn = false; } catch { }
            try
            {
                if (s.T == null || s.T.WasCollected || !s.On) return;
                if (s.T.localScale == s.SetScale) s.T.localScale = s.BaseScale;
                if (s.T.localPosition == s.SetPos) s.T.localPosition = s.BasePos;
            }
            catch { }
        }
    }

    /// <summary>
    /// The active-character arrow and the target hand are placed by the static Last.UI.BattleCursorUtility
    /// SetSelectedPlayer / SetTargetPlayer (Cursor, BattlePlayerData) and SetTargetEnemy (Cursor, BattleEnemyData)
    /// [verified, interop dump]; their PositionConstraint has no source in battle [verified, log 2026-11-12]. The
    /// cursor is remembered with the member's corpsIndex so elements can add that member's party offset.
    /// </summary>
    static class BattleCursorPatches
    {
        static bool warned, enemyWarned;
        static void Note(Last.UI.Cursor c, int corps)
        {
            try { if (c != null) BattleCharScale.cursorCorps[c.Pointer] = corps; }
            catch (Exception e) { if (!warned) { warned = true; Plugin.L.LogWarning("[Battle] cursor note failed: " + e.Message); } }
        }

        static void PlayerPost(Last.UI.Cursor __0, BattlePlayerData __1) { try { Note(__0, __1 != null ? __1.corpsIndex : -1); } catch { } }
        static void EnemyPost(Last.UI.Cursor __0, Last.Battle.BattleEnemyData __1)
            {
                Note(__0, -1);
                try
                {
                    var a = __1 != null ? __1.GetAnimStateController() : null;
                    if (__0 != null && a != null) BattleCharScale.NoteEnemyCursor(__0.Pointer, a.Pointer);
                }
                catch (Exception e) { if (!enemyWarned) { enemyWarned = true; Plugin.L.LogWarning("[Battle] enemy cursor link failed (hand keeps the game's place): " + e.Message); } }
            }

        /// <summary>Patched on its own (not PatchAll), so a failure here never stops the camera patches.</summary>
        internal static void Install(HarmonyLib.Harmony h)
        {
            var t = typeof(Last.UI.BattleCursorUtility);
            var pp = new HarmonyLib.HarmonyMethod(typeof(BattleCursorPatches).GetMethod(nameof(PlayerPost), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
            var ep = new HarmonyLib.HarmonyMethod(typeof(BattleCursorPatches).GetMethod(nameof(EnemyPost), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
            h.Patch(HarmonyLib.AccessTools.Method(t, nameof(Last.UI.BattleCursorUtility.SetSelectedPlayer)), postfix: pp);
            h.Patch(HarmonyLib.AccessTools.Method(t, nameof(Last.UI.BattleCursorUtility.SetTargetPlayer)), postfix: pp);
            h.Patch(HarmonyLib.AccessTools.Method(t, nameof(Last.UI.BattleCursorUtility.SetTargetEnemy)), postfix: ep);
        }
    }

    /// <summary>
    /// Mini without Mini art (user, 2026-11-15): the game loads a party member's battle poses with
    /// Last.Battle.InstantiateManager.LoadPlayerMotions(Int32, Int32, ConditionType) and swaps them with
    /// ChangePlayerMotions(BattlePlayerEntity, Int32, Int32, ConditionType) [verified, interop dump]. Mini is
    /// Last.Defaine.ConditionType.Child = 401 [verified: enum value; condition.csv row 13 has type 401; Old = Aging = 32
    /// and Zombie = 34 match their named rows]. When the game asks for the Mini set, it is asked for the normal set
    /// (ConditionType.Non) instead, and BattleCharScale shows that member at battle.miniScale. The first Int32 is taken
    /// to be the character status id (BattlePlayerEntity.CharacterStatusId) [inferred; every call is logged with the
    /// entity's own ids to check]. Known gap: Mini together with Old or Zombie loads the plain set [inferred].
    /// </summary>
    static class MiniPatches
    {
        /// <summary>First Int32 of the last motion load (character status id [inferred]) -> Mini on.</summary>
        static readonly Dictionary<int, bool> miniByChara = new Dictionary<int, bool>();
        static readonly HashSet<string> logged = new HashSet<string>();
        static bool warned;

        internal static void Log(string s)
        {
            if (logged.Count < 500 && logged.Add(s)) Plugin.L.LogWarning("[Mini] " + s + " (info, not an error)");
        }

        static void Warn(Exception e)
        {
            if (warned) return;
            warned = true;
            Plugin.L.LogWarning("[Mini] patch failed (the game's own Mini sprites are used): " + e.Message);
        }

        internal static bool IsMini(Last.Battle.BattlePlayerEntity e)
        {
            try { return e != null && !e.WasCollected && miniByChara.TryGetValue(e.CharacterStatusId, out var m) && m; }
            catch (Exception ex) { Warn(ex); return false; }
        }

        static void LoadPre(int __0, int __1, ref Last.Defaine.ConditionType __2)
        {
            try
            {
                if (BattleCharScale.MiniScale <= 0f) return;
                bool mini = __2 == Last.Defaine.ConditionType.Child;
                miniByChara[__0] = mini;
                Log($"LoadPlayerMotions({__0}, {__1}, {__2})" + (mini ? $" -> normal set, shown x{BattleCharScale.MiniScale:0.###}" : ""));
                if (mini) __2 = Last.Defaine.ConditionType.Non;
            }
            catch (Exception e) { Warn(e); }
        }

        static void ChangePre(Last.Battle.BattlePlayerEntity __0, int __1, int __2, ref Last.Defaine.ConditionType __3)
        {
            try
            {
                if (BattleCharScale.MiniScale <= 0f) return;
                bool mini = __3 == Last.Defaine.ConditionType.Child;
                miniByChara[__1] = mini;
                string ids = __0 != null ? $"entity status id {__0.CharacterStatusId}, job {__0.JobId}" : "no entity";
                Log($"ChangePlayerMotions({ids}; {__1}, {__2}, {__3})" + (mini ? $" -> normal set, shown x{BattleCharScale.MiniScale:0.###}" : ""));
                if (mini) __3 = Last.Defaine.ConditionType.Non;
            }
            catch (Exception e) { Warn(e); }
        }

        internal static void Install(HarmonyLib.Harmony h)
        {
            var t = typeof(Last.Battle.InstantiateManager);
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            h.Patch(HarmonyLib.AccessTools.Method(t, nameof(Last.Battle.InstantiateManager.LoadPlayerMotions)),
                prefix: new HarmonyLib.HarmonyMethod(typeof(MiniPatches).GetMethod(nameof(LoadPre), F)));
            h.Patch(HarmonyLib.AccessTools.Method(t, nameof(Last.Battle.InstantiateManager.ChangePlayerMotions)),
                prefix: new HarmonyLib.HarmonyMethod(typeof(MiniPatches).GetMethod(nameof(ChangePre), F)));
        }
    }

    static class BgMosaic
    {
        class Entry { public Material M; public Vector3 Base, Set; public bool Applied; public string From; }
        static readonly Dictionary<IntPtr, Entry> mats = new Dictionary<IntPtr, Entry>();
        static readonly List<Last.Battle.BattleBackgroundController> ctrls = new List<Last.Battle.BattleBackgroundController>();
        static readonly List<MeshRenderer> rends = new List<MeshRenderer>();
        static float nextFind;
        static string lastState = "";
        static readonly string[] Names = { "_MosaicWidth", "_MosaicHeight", "_MosaicBlock" };

        static void State(string s)
        {
            if (s == lastState) return;
            lastState = s;
            Plugin.L.LogWarning("[Render] background mosaic: " + s + " (info, not an error)");
        }

        /// <summary>Only the background's own material copies ("(Instance)"): the shared LastSpriteMaterial asset is
        /// also used by other sprites, so it is left alone.</summary>
        static void Add(Material m, string from)
        {
            if (m == null || mats.ContainsKey(m.Pointer)) return;
            if (m.name == null || !m.name.EndsWith("(Instance)")) return;
            if (!m.HasProperty(Names[0])) { State($"{from} material '{m.name}' has no {Names[0]}"); return; }
            mats[m.Pointer] = new Entry { M = m, From = from };
        }

        internal static void Apply()
        {
            Camera bgCam = null;
            foreach (var c in Camera.allCameras) if (c.name == "CameraBattlebackGround") { bgCam = c; break; }
            // Entries are kept between battles: the game reuses the same material, and a fresh entry would read our
            // scaled value as the game's and multiply again (seen: 512 -> 1012148 after 4 battles [verified, F8]).
            if (bgCam == null) { ctrls.Clear(); rends.Clear(); State("no battle"); return; }
            float k = RenderScale.FactorFor("CameraBattlebackGround");
            ctrls.RemoveAll(x => x == null || x.WasCollected);
            rends.RemoveAll(x => x == null || x.WasCollected);
            // Scene search (slow, ~17 ms each) only until the background objects are found, once per battle.
            if (ctrls.Count == 0 && Time.unscaledTime >= nextFind)
            {
                nextFind = Time.unscaledTime + 1f;
                var cs = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Last.Battle.BattleBackgroundController>());
                if (cs != null) foreach (var o in cs) { var c = o?.TryCast<Last.Battle.BattleBackgroundController>(); if (c != null) ctrls.Add(c); }
                var ras = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Last.Battle.RasterAnimation>());
                if (ras != null) foreach (var o in ras) { var ra = o?.TryCast<Last.Battle.RasterAnimation>(); var mr = ra?.MeshRenderer; if (mr != null) rends.Add(mr); }
            }
            // Cheap each frame: the game may swap in a new material copy.
            foreach (var c in ctrls) Add(c.GetMaterial(), "controller");
            foreach (var mr in rends) Add(mr.sharedMaterial, "renderer");
            State($"battle: {ctrls.Count} controller(s), {rends.Count} renderer(s), {mats.Count} material(s), factor {k:0.##}");
            List<IntPtr> dead = null;
            foreach (var kv in mats)
            {
                var e = kv.Value;
                var m = e.M;
                if (m == null || m.WasCollected) { (dead ??= new List<IntPtr>()).Add(kv.Key); continue; }
                var cur = new Vector3(m.GetFloat(Names[0]), m.GetFloat(Names[1]), m.GetFloat(Names[2]));
                if (e.Applied && cur == e.Set && Mathf.Approximately(e.Set.x, e.Base.x * k)) continue;
                if (!e.Applied || cur != e.Set) e.Base = cur; // the game's value (first time, or the game changed it)
                e.Set = e.Base * k;
                if (cur != e.Set) { m.SetFloat(Names[0], e.Set.x); m.SetFloat(Names[1], e.Set.y); m.SetFloat(Names[2], e.Set.z); }
                if (!e.Applied) Plugin.L.LogWarning($"[Render] background mosaic on {e.From} '{m.name}': {e.Base.x}x{e.Base.y} ({e.Base.z}) -> {e.Set.x:0}x{e.Set.y:0} ({e.Set.z:0}) (info, not an error)");
                e.Applied = true;
            }
            if (dead != null) foreach (var d in dead) mats.Remove(d);
        }
    }

    /// <summary>
    /// "battle.fitBackground": the screen shows only the middle CameraBattle-sized part of what CameraBattlebackGround
    /// sees (orthographic sizes 108 vs 128 at 16:9 [verified, log]; 16:9 shows y 20-236 of the 256-unit background
    /// [verified]), so the background's top and bottom are cut. Zooming the background camera out to
    /// size_bg^2 / size_battle makes its full height (2 * size_bg units) fill the visible height, same centre.
    /// The game's own size is kept as the base; if the game changes it, that becomes the new base.
    /// </summary>
    /// <summary>
    /// "battle.spritesDown": moves everything the battle cameras draw (party, enemies, effects) down by N px at 1440p,
    /// the background stays. Done by raising those cameras by the same world distance: CameraBattle shows
    /// 2 * orthographicSize units over the screen height [verified, log 2026-10-31]; effects only line up with the
    /// characters if their cameras share that world, so all listed cameras get the same world shift [inferred].
    /// Whatever the game derives from these cameras (cursors, damage numbers) should follow [unverified].
    /// The game's own camera position is the base; if the game moves a camera, that becomes the new base. When
    /// CameraBattle is gone (battle over) the cameras go back to their base, so field use is untouched.
    /// </summary>
    static class BattleShift
    {
        internal static readonly string[] DefaultCameras = { "CameraBattle", "CameraEffect1", "CameraEffect2", "CameraEffect3", "CameraEffect4" };
        internal static float Down;
        internal static List<string> Cameras = new List<string>(DefaultCameras);
        class State { public Transform T; public Vector3 Base, Set; }
        static readonly Dictionary<IntPtr, State> states = new Dictionary<IntPtr, State>();
        static bool logged;
        /// <summary>px the battle sprites are moved down right now (0 outside battle); elements with addSpritesDown add it.</summary>
        internal static float ActiveDown;
        static Renderer bgView;
        static float nextBgFind;
        static string bgNote = "";

        /// <summary>
        /// CameraBattlebackGround draws into a render texture [verified, F8 dump] that Last.Battle.BattleBackgroundController
        /// puts on a material (SetMaterialTexture, interop dump). The renderer showing that texture is drawn by
        /// CameraBattle (user 2026-11-06: the background moved with the sprites) [inferred], so it is raised by the same
        /// distance as the cameras and stays put on screen. Found by its texture, and only used if CameraBattle's culling
        /// mask includes its layer, so the background camera's own sprites are never moved.
        /// </summary>
        static Transform BackgroundView(Camera battle)
        {
            try
            {
                if ((bgView == null || bgView.WasCollected) && Time.unscaledTime >= nextBgFind)
                {
                    nextBgFind = Time.unscaledTime + 1f;
                    bgView = null;
                    Camera bgCam = null;
                    foreach (var c in Camera.allCameras) if (c.name == "CameraBattlebackGround") { bgCam = c; break; }
                    var rt = bgCam != null ? bgCam.targetTexture : null;
                    if (rt == null) { bgNote = "background camera has no render texture"; return null; }
                    var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Renderer>());
                    if (found != null)
                        foreach (var o in found)
                        {
                            var r = o?.TryCast<Renderer>();
                            var m = r?.sharedMaterial;
                            if (m == null || m.mainTexture == null || m.mainTexture.Pointer != rt.Pointer) continue;
                            if ((battle.cullingMask & (1 << r.gameObject.layer)) == 0) { bgNote = $"'{r.name}' shows the background but CameraBattle does not draw layer {r.gameObject.layer}"; continue; }
                            bgView = r;
                            bgNote = $"background '{PathOf(r.transform)}' ({r.GetIl2CppType().Name}, layer {r.gameObject.layer})";
                            break;
                        }
                    if (bgView == null && bgNote == "") bgNote = "no renderer shows the background texture";
                }
                return bgView != null && !bgView.WasCollected ? bgView.transform : null;
            }
            catch (Exception e) { bgNote = "background search failed: " + e.Message; return null; }
        }

        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; i < 8 && t != null; i++, t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }

        static void Shift(Transform t, IntPtr key, float units)
        {
            var p = t.position;
            if (!states.TryGetValue(key, out var s)) { s = new State { T = t, Base = p, Set = p }; states[key] = s; }
            else if (p != s.Set) s.Base = p; // the game moved it: its position is the new base
            var want = s.Base + new Vector3(0f, units, 0f);
            s.Set = want;
            if (p != want) t.position = want;
        }

        // The background view (the quad showing the background render texture) keeps a fixed offset to CameraBattle's
        // own (unshifted) position, learned the first time that object is seen. Base-tracking it like the cameras made
        // it climb one shift per battle (user 2026-11-07: TextureRender y 25.5 -> 102 after 4 battles [verified, F8]).
        static IntPtr viewKey;
        static Transform viewT;
        static float viewRel, viewSetY = float.NaN, viewRestY;

        static void PlaceView(Transform view, Camera battle, float units)
        {
            float camBaseY = states.TryGetValue(battle.Pointer, out var cs) ? cs.Base.y : battle.transform.position.y - units;
            if (view.Pointer != viewKey) { viewKey = view.Pointer; viewT = view; viewRel = view.position.y - camBaseY; }
            var p = view.position;
            float want = camBaseY + viewRel + units;
            if (Mathf.Abs(p.y - want) > 0.001f) view.position = new Vector3(p.x, want, p.z);
            viewSetY = want;
            viewRestY = camBaseY + viewRel;
        }

        static void RestoreView()
        {
            try
            {
                if (viewT != null && !viewT.WasCollected && !float.IsNaN(viewSetY) && Mathf.Abs(viewT.position.y - viewSetY) < 0.001f)
                {
                    var p = viewT.position;
                    viewT.position = new Vector3(p.x, viewRestY, p.z);
                }
            }
            catch { }
            viewSetY = float.NaN;
        }

        internal static void Apply()
        {
            if (Down == 0 && states.Count == 0) { ActiveDown = 0; return; }
            Camera battle = null;
            foreach (var c in Camera.allCameras) if (c.name == "CameraBattle") { battle = c; break; }
            if (battle == null || !battle.orthographic || Down == 0)
            {
                ActiveDown = 0;
                RestoreAll();
                RestoreView();
                logged = false;
                return;
            }
            ActiveDown = Down;
            float units = Down * 2f * battle.orthographicSize / 1440f;
            var log = logged ? null : new List<string>();
            foreach (var cam in Camera.allCameras)
            {
                if (!Cameras.Contains(cam.name)) continue;
                Shift(cam.transform, cam.Pointer, units);
                log?.Add($"{cam.name} size {cam.orthographicSize:0.##} y -> {cam.transform.position.y:0.##}");
            }
            var view = BackgroundView(battle);
            if (view != null)
            {
                PlaceView(view, battle, units);
                log?.Add($"{bgNote} y -> {view.position.y:0.##}");
            }
            else if (log != null)
            {
                if (bgNote == "") { logged = false; log = null; } // search not done yet: log next frame
                else log.Add("background stays with the sprites: " + bgNote);
            }
            if (log != null)
            {
                logged = true;
                Plugin.L.LogWarning($"[Battle] sprites down {Down} px = {units:0.##} units: {string.Join("; ", log)} (info, not an error)");
            }
        }

        /// <summary>Puts every shifted camera back on its base position (battle over, or the value set to 0).</summary>
        static void RestoreAll()
        {
            foreach (var s in states.Values)
            {
                try
                {
                    if (s.T == null || s.T.WasCollected) continue;
                    if (s.T.position == s.Set && s.Set != s.Base) s.T.position = s.Base;
                    s.Set = s.Base;
                }
                catch { }
            }
            if (Down == 0) states.Clear();
            else
            {
                var gone = new List<IntPtr>();
                foreach (var kv in states) if (kv.Value.T == null || kv.Value.T.WasCollected) gone.Add(kv.Key);
                foreach (var k in gone) states.Remove(k);
            }
        }
    }

    /// <summary>
    /// "spacing" on a scrolling list (e.g. path "list_window/Scroll View"): the gap between rows. These lists are placed
    /// by Last.UI.InfiniteScrollView, not a Unity layout group [verified, F8 dump: Content has no layout component]; the
    /// row pitch is computed when the list is built from the row size and ContentVerticalMargin [inferred: measured
    /// pitch 73 px = row 58 px + about 16 px]. So the margin is set just before the list builds (prefix on Awake and the
    /// non-generic Initialize* methods, interop dump) and the change shows the next time the list opens.
    /// The game's own value is kept and put back when no rule matches any more.
    /// </summary>
    static class ScrollSpacing
    {
        static readonly Dictionary<IntPtr, float> original = new Dictionary<IntPtr, float>();
        static readonly HashSet<string> logged = new HashSet<string>();

        internal static void Install(HarmonyLib.Harmony h)
        {
            var pre = new HarmonyLib.HarmonyMethod(typeof(ScrollSpacing).GetMethod(nameof(Prefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));
            // Postfix too: if a method computes the stride itself (from the game's margin), it is set again right after.
            var post = new HarmonyLib.HarmonyMethod(typeof(ScrollSpacing).GetMethod(nameof(Postfix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));
            var done = new List<string>();
            // Never OnEnable: its compiled body is shared with other classes' OnEnable (the game crashed at start with
            // 0xc0000005 when Memoria's ModComponent was enabled through our hook; ErrorLog 2026-11-06) [verified].
            foreach (var name in new[] { "Awake", "InitializeInternal", "InitializeInternalNotCoroutine", "InitializeForIndex" })
            {
                try
                {
                    var m = HarmonyLib.AccessTools.Method(typeof(Last.UI.InfiniteScrollView), name);
                    if (m == null) continue;
                    h.Patch(m, prefix: pre, postfix: post);
                    done.Add(name);
                }
                catch (Exception e) { Plugin.L.LogWarning($"[Elements] list row spacing: could not hook InfiniteScrollView.{name}: {e.Message}"); }
            }
            Plugin.L.LogInfo("[Elements] list row spacing hooks: " + string.Join(", ", done));
        }

        /// <summary>True only for a real InfiniteScrollView: a hook on a method whose compiled body other classes share
        /// receives their objects too. Checked on the native class pointer, without touching the object.</summary>
        static bool Real(Last.UI.InfiniteScrollView v)
        {
            if (v == null || v.Pointer == IntPtr.Zero) return false;
            var cls = IL2CPP.il2cpp_object_get_class(v.Pointer);
            return cls == Il2CppClassPointerStore<Last.UI.InfiniteScrollView>.NativeClassPtr;
        }

        static void Prefix(Last.UI.InfiniteScrollView __instance, System.Reflection.MethodBase __originalMethod)
        {
            if (!Real(__instance)) return;
            try { Apply(__instance, "before " + __originalMethod?.Name); }
            catch (Exception e) { Plugin.L.LogWarning("[Elements] list row spacing: " + e.Message); }
        }

        static void Postfix(Last.UI.InfiniteScrollView __instance, System.Reflection.MethodBase __originalMethod)
        {
            if (!Real(__instance)) return;
            try { Apply(__instance, "after " + __originalMethod?.Name); }
            catch (Exception e) { Plugin.L.LogWarning("[Elements] list row spacing: " + e.Message); }
        }

        // strideVertical (Int32, units) is the row pitch the list places rows with. Setting only ContentVerticalMargin
        // changed nothing on screen (user test 2026-11-06: 40, 0, 10 px all looked the same; the log showed the margin
        // set), so the pitch is not recomputed from it at these points [verified]; stride is now set as well:
        // game stride + (our margin - game margin) [inferred: 55 units = row 43 + margin 12].
        static readonly Dictionary<IntPtr, int> originalStride = new Dictionary<IntPtr, int>();

        static void Apply(Last.UI.InfiniteScrollView v, string hook)
        {
            if (v == null) return;
            var r = Elements.MatchSpacing(v.transform);
            float cur = v.ContentVerticalMargin;
            int stride = v.strideVertical;
            if (r == null)
            {
                if (original.TryGetValue(v.Pointer, out var o))
                {
                    if (Mathf.Abs(cur - o) > 0.01f) v.ContentVerticalMargin = o;
                    original.Remove(v.Pointer);
                }
                if (originalStride.TryGetValue(v.Pointer, out var os))
                {
                    if (stride != os && stride != 0) v.strideVertical = os;
                    originalStride.Remove(v.Pointer);
                }
                return;
            }
            var canvas = v.GetComponentInParent<Canvas>();
            if (canvas == null) return;
            float upp = Px.Upp(canvas.rootCanvas);
            float want = r.Spacing * upp;
            if (!original.ContainsKey(v.Pointer)) original[v.Pointer] = cur;
            // Stride 0 = not computed yet (before the list is built): leave it, the next hook sets it.
            if (!originalStride.ContainsKey(v.Pointer) && stride > 0) originalStride[v.Pointer] = stride;
            int wantStride = originalStride.TryGetValue(v.Pointer, out var gs) ? Mathf.Max(1, gs + Mathf.RoundToInt(want - original[v.Pointer])) : 0;
            bool change = Mathf.Abs(cur - want) > 0.01f || (wantStride > 0 && stride != wantStride);
            if (!change) return;
            v.ContentVerticalMargin = want;
            if (wantStride > 0) v.strideVertical = wantStride;
            if (logged.Add(r.Name + "|" + want.ToString("0.##") + "|" + wantStride))
                Plugin.L.LogWarning($"[Elements] '{r.Name}': list row gap {cur:0.##} -> {want:0.##} units, row stride {stride} -> {(wantStride > 0 ? wantStride.ToString() : "not built yet")} " +
                                    $"({r.Spacing} px at 1440p; game's own gap {original[v.Pointer] / upp:0.#} px; at {hook}) (info, not an error)");
        }
    }

    static class BackgroundFit
    {
        internal static bool Enabled;
        static IntPtr camPtr;
        static float baseSize, setSize = -1;

        internal static void Apply()
        {
            Camera battle = null, bg = null;
            foreach (var c in Camera.allCameras)
            {
                if (c.name == "CameraBattle") battle = c;
                else if (c.name == "CameraBattlebackGround") bg = c;
            }
            if (bg == null || !bg.orthographic) return;
            float cur = bg.orthographicSize;
            if (bg.Pointer != camPtr || Mathf.Abs(cur - setSize) > 0.001f) { camPtr = bg.Pointer; baseSize = cur; }
            float want = baseSize;
            if (Enabled && battle != null && battle.orthographic && battle.orthographicSize > 0f)
                want = Mathf.Max(baseSize, baseSize * baseSize / battle.orthographicSize);
            setSize = want;
            SizeSprites(2f * baseSize);
            try { Animtex.Apply(animRenderers, sprites); } catch (Exception e) { Plugin.L.LogWarning("[Render] animtex: " + e.Message); }
            if (Mathf.Abs(cur - want) <= 0.001f) return;
            bg.orthographicSize = want;
            Plugin.L.LogInfo($"[Render] background fit: CameraBattlebackGround size {cur:0.##} -> {want:0.##} (CameraBattle size {(battle != null ? battle.orthographicSize : 0):0.##})");
        }

        static float nextFind;
        static readonly List<SpriteRenderer> sprites = new List<SpriteRenderer>();
        static readonly List<SpriteRenderer> animRenderers = new List<SpriteRenderer>();
        static Transform bgRoot;
        static readonly HashSet<string> logged = new HashSet<string>();

        static bool UnderBattleBackground(Transform t)
        {
            for (int i = 0; i < 10 && t != null; i++, t = t.parent) if (t.name == "BattleBackground") return true;
            return false;
        }

        /// <summary>
        /// High-res background images (from KupoUI's texture folder or a Magicite pack) may arrive with any
        /// pixels-per-unit. Scales each background sprite whose texture is taller than the original 256 px so it is
        /// heightUnits tall (the background camera's own view height, 256 units [verified]), keeping its pivot.
        /// Original-size backgrounds and _Animtex overlay pieces are left alone.
        /// </summary>
        static void SizeSprites(float heightUnits)
        {
            // The background's own tree (RootObject/BattleBackground [verified, F8 dump]) is read every frame: a
            // once-a-second scene search showed the game's own animated pieces for the first moment of a battle
            // (user saw a darker frame flicker; log: first HD frame swapped was A_5, not A_0, 2026-11-10).
            if ((bgRoot == null || bgRoot.WasCollected) && Time.unscaledTime >= nextFind)
            {
                nextFind = Time.unscaledTime + 1f;
                var go = GameObject.Find("RootObject/BattleBackground");
                bgRoot = go != null ? go.transform : null;
            }
            {
                sprites.Clear();
                animRenderers.Clear();
                var found = bgRoot != null && !bgRoot.WasCollected ? bgRoot.GetComponentsInChildren<SpriteRenderer>(true) : null;
                if (found != null)
                    foreach (var sr in found)
                    {
                        var sp = sr?.sprite;
                        if (sp == null) continue;
                        // Animated overlay pieces (<BG>_A_n, <BG>_Animtex; survey of all 9 animated backgrounds,
                        // 2026-11-10) or our HD copies of them; everything else named BG_ is a background.
                        if (Animtex.IsPiece(sp)) { animRenderers.Add(sr); continue; }
                        if (sp.name.StartsWith("BG_")) sprites.Add(sr);
                    }
            }
            foreach (var sr in sprites)
            {
                if (sr == null || sr.WasCollected) continue;
                var sp = sr.sprite;
                if (sp == null || sp.texture == null || sp.texture.height <= 256) continue;
                var t = sr.transform;
                float parent = t.parent != null ? Mathf.Abs(t.parent.lossyScale.y) : 1f;
                float h = sp.bounds.size.y * parent;
                if (h <= 0f) continue;
                float want = heightUnits / h;
                if (Mathf.Abs(Mathf.Abs(t.localScale.y) - want) <= 0.0001f) continue;
                t.localScale = new Vector3(Mathf.Sign(t.localScale.x) * want, Mathf.Sign(t.localScale.y) * want, t.localScale.z);
                if (logged.Add(sp.name + "|" + sp.pixelsPerUnit))
                    Plugin.L.LogInfo($"[Render] background '{sp.name}': {sp.texture.width}x{sp.texture.height} px at ppu {sp.pixelsPerUnit} = {h:0.#} units tall; scale {want:0.###} -> {heightUnits:0} units (pivot {sp.pivot})");
            }
        }
    }

    /// <summary>
    /// Animated background pieces. Each background prefab carries its own animation as data; no game code places
    /// them [verified, UnityPy survey of all 9 animated backgrounds 2026-11-10: Animators whose clips swap the sprite of
    /// fixed SpriteRenderers (05 08 12 20 26: 1-3 pieces; 18: 32 pieces, sheet BG_FF5_18_A; 22: 12 pieces + colour
    /// curves, one sprite on 3 renderers; 23: the whole sheet as a fading overlay; no clip moves a piece; 31 scrolls a
    /// mesh texture instead and is not handled here]. The game's Animator keeps choosing the frame; after it
    /// (LateUpdate) this puts the HD frame on the renderer.
    /// Files in battle_backgrounds/animtex/: one PNG per game sprite, named like it (BG_FF5_12_A_0.png), drawn at the
    /// HD background's own pixel scale. Optional (background)_place.json (not (background).json: KupoUI reads that
    /// name as its own texture settings [verified, KupoUI log 2026-11-10]; written by tools/animtex_fit.py): "source": [x, y, w, h] = the part of the original
    /// background (px, top-left) that the HD image shows (default: all of it); "frameScale": frame px per HD-background
    /// px (default 1); "pieces": { "(renderer name)": [x, y] } = top-left of that piece's frames in the HD background
    /// (px). A piece without an entry is centred where the game's piece is, mapped through "source".
    /// Effects in the prefab (ParticleSystems, e.g. BG_FF5_12 Effect/Sparkle1-4) are moved and sized through "source"
    /// too; "effects": { "(object name)": [x, y] } = their centre in HD-background px instead.
    /// "feather": n = fade each frame's alpha to 0 over its outer n px (HD-background px; colours untouched; edges that
    /// lie on the background's own border are not faded); "featherPieces": { "(renderer name)": n } per piece. Default 0.
    /// No frame file = the game's piece. The folder is re-read when it changes (checked every 2 s).
    /// </summary>
    static class Animtex
    {
        class Cfg { public float[] Source; public float FrameScale = 1f; public Dictionary<string, Vector2> Pieces = new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase), Effects = new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase); public bool File; public float Feather; public Dictionary<string, float> FeatherPieces = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase); }
        class Fx { public Transform T; public Vector3 BasePos, BaseScale, SetPos, SetScale; public bool On; }
        static readonly Dictionary<IntPtr, Fx> fx = new Dictionary<IntPtr, Fx>();

        static readonly Dictionary<string, Texture2D> frames = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Cfg> cfgs = new Dictionary<string, Cfg>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Sprite> made = new Dictionary<string, Sprite>();
        static readonly HashSet<IntPtr> ours = new HashSet<IntPtr>();
        static readonly HashSet<string> logged = new HashSet<string>();
        static readonly System.Text.RegularExpressions.Regex baseRx = new System.Text.RegularExpressions.Regex(@"^(BG_[A-Za-z0-9]+_\d+)_(A_|A$|Animtex)");
        static float nextCheck;
        static long stamp = -1;

        static string Folder => Path.Combine(Path.GetDirectoryName(Plugin.ConfigPath) ?? "", "battle_backgrounds", "animtex");

        internal static bool IsPiece(Sprite sp)
        {
            if (sp == null) return false;
            if (ours.Contains(sp.Pointer)) return true;
            if (baseRx.IsMatch(sp.name)) return true;
            return sp.texture != null && baseRx.IsMatch(sp.texture.name);
        }

        static string BaseName(Sprite sp)
        {
            var m = baseRx.Match(sp.name);
            if (!m.Success && sp.texture != null) m = baseRx.Match(sp.texture.name);
            return m.Success ? m.Groups[1].Value : null;
        }

        static void Once(string key, string msg) { if (logged.Add(key)) Plugin.L.LogWarning(msg + " (info, not an error)"); }

        static void CheckFolder()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 2f;
            string dir = Folder;
            long st = 0;
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir)) st = st * 31 + File.GetLastWriteTimeUtc(f).Ticks + new FileInfo(f).Length;
            if (st == stamp) return;
            stamp = st;
            foreach (var sp in made.Values) if (sp != null && !sp.WasCollected) UnityEngine.Object.Destroy(sp);
            foreach (var t in frames.Values) if (t != null && !t.WasCollected) UnityEngine.Object.Destroy(t);
            foreach (var t in featheredTex) if (t != null && !t.WasCollected) UnityEngine.Object.Destroy(t);
            featheredTex.Clear();
            made.Clear(); frames.Clear(); cfgs.Clear(); ours.Clear(); logged.Clear();
        }

        static Texture2D Frame(string name)
        {
            if (frames.TryGetValue(name, out var have)) return have;
            Texture2D tex = null;
            string f = Path.Combine(Folder, name + ".png");
            if (File.Exists(f))
            {
                try
                {
                    tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(f))) { Plugin.L.LogWarning("[Render] animtex: not a readable PNG: " + f); tex = null; }
                    else
                    {
                        tex.filterMode = FilterMode.Bilinear;
                        tex.wrapMode = TextureWrapMode.Clamp;
                        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                        tex.name = name + "_HD";
                    }
                }
                catch (Exception e) { Plugin.L.LogWarning("[Render] animtex: could not load " + f + ": " + e.Message); tex = null; }
            }
            frames[name] = tex;
            return tex;
        }

        static Cfg Config(string bg)
        {
            if (cfgs.TryGetValue(bg, out var have)) return have;
            var c = new Cfg();
            string f = Path.Combine(Folder, bg + "_place.json");
            if (File.Exists(f))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f), new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    var root = doc.RootElement;
                    if (root.TryGetProperty("source", out var so) && so.ValueKind == System.Text.Json.JsonValueKind.Array && so.GetArrayLength() == 4)
                    {
                        c.Source = new float[4];
                        for (int i = 0; i < 4; i++) c.Source[i] = so[i].GetSingle();
                        if (c.Source[2] <= 0 || c.Source[3] <= 0) { Plugin.L.LogWarning($"[Render] animtex {bg}.json: source width/height must be > 0; ignored"); c.Source = null; }
                    }
                    if (root.TryGetProperty("frameScale", out var fs) && fs.ValueKind == System.Text.Json.JsonValueKind.Number && fs.GetSingle() > 0) c.FrameScale = fs.GetSingle();
                    if (root.TryGetProperty("pieces", out var pc) && pc.ValueKind == System.Text.Json.JsonValueKind.Object)
                        foreach (var pr in pc.EnumerateObject())
                            if (pr.Value.ValueKind == System.Text.Json.JsonValueKind.Array && pr.Value.GetArrayLength() >= 2)
                                c.Pieces[pr.Name] = new Vector2(pr.Value[0].GetSingle(), pr.Value[1].GetSingle());
                    if (root.TryGetProperty("effects", out var ef) && ef.ValueKind == System.Text.Json.JsonValueKind.Object)
                        foreach (var pr in ef.EnumerateObject())
                            if (pr.Value.ValueKind == System.Text.Json.JsonValueKind.Array && pr.Value.GetArrayLength() >= 2)
                                c.Effects[pr.Name] = new Vector2(pr.Value[0].GetSingle(), pr.Value[1].GetSingle());
                    if (root.TryGetProperty("feather", out var fe) && fe.ValueKind == System.Text.Json.JsonValueKind.Number && fe.GetSingle() > 0) c.Feather = fe.GetSingle();
                    if (root.TryGetProperty("featherPieces", out var fp) && fp.ValueKind == System.Text.Json.JsonValueKind.Object)
                        foreach (var pr in fp.EnumerateObject())
                            if (pr.Value.ValueKind == System.Text.Json.JsonValueKind.Number) c.FeatherPieces[pr.Name] = Mathf.Max(0f, pr.Value.GetSingle());
                    c.File = true;
                    Plugin.L.LogWarning($"[Render] animtex {bg}_place.json: source {(c.Source == null ? "whole original" : string.Join(",", c.Source))}, frameScale {c.FrameScale}, {c.Pieces.Count} placed piece(s), {c.Effects.Count} placed effect(s), feather {c.Feather} ({c.FeatherPieces.Count} per piece) (info, not an error)");
                }
                catch (Exception e) { Plugin.L.LogWarning($"[Render] animtex {bg}_place.json not read ({e.Message}); default placement used"); c = new Cfg(); }
            }
            cfgs[bg] = c;
            return c;
        }

        /// <summary>The background's own renderer (BG_FF5_12 / BG_FF5_12_Custom ...), from the background list.</summary>
        static SpriteRenderer FindBackground(List<SpriteRenderer> bgs, string bg)
        {
            SpriteRenderer best = null;
            foreach (var r in bgs)
            {
                if (r == null || r.WasCollected || !r.isVisible && !r.enabled) continue;
                var sp = r.sprite;
                if (sp == null || !sp.name.StartsWith(bg, StringComparison.OrdinalIgnoreCase)) continue;
                string rest = sp.name.Substring(bg.Length);
                if (rest.Length > 0 && !rest.StartsWith("_Custom", StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null || sp.rect.width > best.sprite.rect.width) best = r;
            }
            return best;
        }

        static Sprite Build(SpriteRenderer sr, Sprite game, SpriteRenderer bgR, Texture2D tex, Cfg cfg, string bg)
        {
            var b = bgR.bounds;                            // world box of the (HD) background
            var bsp = bgR.sprite;
            float W = bsp.rect.width, H = bsp.rect.height; // HD background px
            if (b.size.x <= 0 || b.size.y <= 0 || W <= 0 || H <= 0) return null;
            // Prefab units = original background px (the game's backgrounds are ppu 1 [verified, survey]).
            float parent = bgR.transform.parent != null ? Mathf.Abs(bgR.transform.parent.lossyScale.x) : 1f;
            float origW = b.size.x / Mathf.Max(0.0001f, parent), origH = b.size.y / Mathf.Max(0.0001f, parent);
            var src = cfg.Source ?? new[] { 0f, 0f, origW, origH };
            float pxw = W / b.size.x;                      // HD background px per world unit
            float fw = tex.width * cfg.FrameScale / pxw;    // frame size in world units
            float fh = fw * tex.height / tex.width;
            Vector2 tl;
            bool placed = cfg.Pieces.TryGetValue(sr.gameObject.name, out tl);
            if (!placed)
            {
                // Centre of the game's piece, in world units, then in original px (top-left), then in HD px.
                var gr = game.rect; float gppu = Mathf.Max(0.0001f, game.pixelsPerUnit); var gl = sr.transform.lossyScale;
                var R0 = sr.transform.position;
                float cx = R0.x + (gr.width * 0.5f - game.pivot.x) / gppu * gl.x;
                float cy = R0.y + (gr.height * 0.5f - game.pivot.y) / gppu * gl.y;
                float ox = (cx - b.min.x) / b.size.x * origW, oy = (b.max.y - cy) / b.size.y * origH;
                float hx = (ox - src[0]) / src[2] * W, hy = (oy - src[1]) / src[3] * H;
                tl = new Vector2(hx - tex.width * cfg.FrameScale * 0.5f, hy - tex.height * cfg.FrameScale * 0.5f);
            }
            float wx = b.min.x + tl.x / pxw, wy = b.max.y - tl.y / pxw;   // frame top-left, world
            var ls = sr.transform.lossyScale;
            float ppu = tex.width / (fw / Mathf.Max(0.0001f, Mathf.Abs(ls.x)));
            var R = sr.transform.position;
            var pivot = new Vector2((R.x - wx) / fw, (R.y - (wy - fh)) / fh);
            float feather = cfg.FeatherPieces.TryGetValue(sr.gameObject.name, out var fpc) ? fpc : cfg.Feather;
            var useTex = tex;
            if (feather > 0f)
            {
                float fpx = feather * cfg.FrameScale;   // in frame px
                bool l = tl.x > 0.5f, t = tl.y > 0.5f;
                bool r = tl.x + tex.width * cfg.FrameScale < W - 0.5f, btm = tl.y + tex.height * cfg.FrameScale < H - 0.5f;
                useTex = Feathered(tex, fpx, l, r, t, btm);
                Once("f|" + sr.gameObject.name + "|" + game.name, $"[Render] animtex {bg} '{sr.gameObject.name}' frame '{game.name}': feather {feather} px on " +
                     $"{(l ? "L" : "")}{(r ? "R" : "")}{(t ? "T" : "")}{(btm ? "B" : "")} (sides on the background's border left sharp)");
            }
            var s = Sprite.Create(useTex, new Rect(0, 0, useTex.width, useTex.height), pivot, ppu, 0, SpriteMeshType.FullRect);
            s.hideFlags = HideFlags.DontUnloadUnusedAsset;
            s.name = game.name + "_HD";
            ours.Add(s.Pointer);
            Once("b|" + sr.gameObject.name + "|" + game.name, $"[Render] animtex {bg} '{sr.gameObject.name}' frame '{game.name}': {tex.width}x{tex.height} px at HD background ({W:0}x{H:0}) px {tl.x:0.#},{tl.y:0.#} " +
                 (placed ? "(from pieces)" : $"(game's spot via source {src[0]:0.#},{src[1]:0.#},{src[2]:0.#},{src[3]:0.#})") + $", ppu {ppu:0.###}");
            return s;
        }

        static readonly List<Texture2D> featheredTex = new List<Texture2D>();

        /// <summary>Copy of the frame with alpha faded over n px at the chosen sides (smoothstep); RGB unchanged.</summary>
        static Texture2D Feathered(Texture2D src, float n, bool left, bool right, bool top, bool bottom)
        {
            int w = src.width, h = src.height;
            var px = src.GetPixels32();
            for (int y = 0; y < h; y++)
            {
                // texture rows run bottom-up: y = 0 is the bottom edge
                float dy = float.MaxValue;
                if (bottom) dy = Mathf.Min(dy, y + 0.5f);
                if (top) dy = Mathf.Min(dy, h - y - 0.5f);
                for (int x = 0; x < w; x++)
                {
                    float d = dy;
                    if (left) d = Mathf.Min(d, x + 0.5f);
                    if (right) d = Mathf.Min(d, w - x - 0.5f);
                    if (d >= n) continue;
                    float k = Mathf.Clamp01(d / n);
                    k = k * k * (3f - 2f * k);
                    int i = y * w + x;
                    var c = px[i];
                    c.a = (byte)Mathf.RoundToInt(c.a * k);
                    px[i] = c;
                }
            }
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            t.filterMode = src.filterMode; t.wrapMode = TextureWrapMode.Clamp;
            t.hideFlags = HideFlags.DontUnloadUnusedAsset;
            t.name = src.name + "_F";
            t.SetPixels32(px);
            t.Apply(false, false);
            featheredTex.Add(t);
            return t;
        }

        internal static void Apply(List<SpriteRenderer> pieces, List<SpriteRenderer> bgs)
        {
            CheckFolder();
            foreach (var sr in pieces)
            {
                if (sr == null || sr.WasCollected) continue;
                var sp = sr.sprite;
                if (sp == null || ours.Contains(sp.Pointer)) continue;
                string bg = BaseName(sp);
                if (bg == null) continue;
                var tex = Frame(sp.name);
                if (tex == null) { Once("n|" + sp.name, $"[Render] animtex '{sp.name}': no HD frame (animtex/{sp.name}.png), game's piece shown"); continue; }
                var bgR = FindBackground(bgs, bg);
                if (bgR == null) { Once("g|" + bg, $"[Render] animtex {bg}: background renderer not found, game's pieces shown"); continue; }
                string key = sr.gameObject.name + "|" + sp.name + "|" + bgR.sprite.rect.width;
                if (!made.TryGetValue(key, out var hs) || hs == null || hs.WasCollected)
                {
                    hs = Build(sr, sp, bgR, tex, Config(bg), bg);
                    made[key] = hs;
                }
                if (hs != null) sr.sprite = hs;
            }
            try { Effects(bgs); } catch (Exception e) { Plugin.L.LogWarning("[Render] animtex effects: " + e.Message); }
        }

        /// <summary>
        /// Moves the background's particle effects through the same mapping as the frames: game position (original
        /// px) -> HD px via "source" (or "effects" entry) -> world; scale x (original width / source width). Only for
        /// backgrounds with a _place.json, so other backgrounds keep the game's effects untouched.
        /// </summary>
        static void Effects(List<SpriteRenderer> bgs)
        {
            bool find = true;   // the prefab's own subtree, every frame (effects start at once with the background)
            foreach (var bgR in bgs)
            {
                if (bgR == null || bgR.WasCollected || bgR.sprite == null) continue;
                var m = System.Text.RegularExpressions.Regex.Match(bgR.sprite.name, @"^(BG_[A-Za-z0-9]+_\d+)(_Custom)?$");
                if (!m.Success) continue;
                string bg = m.Groups[1].Value;
                var root = bgR.transform.parent;
                if (root == null) continue;
                var cfg = Config(bg);
                if (!cfg.File) continue;
                if (find)
                {
                    var ps = root.GetComponentsInChildren<ParticleSystemRenderer>(true);
                    if (ps != null)
                        foreach (var r in ps)
                        {
                            if (r == null) continue;
                            var t = r.transform;
                            if (!fx.ContainsKey(t.Pointer)) fx[t.Pointer] = new Fx { T = t };
                        }
                }
                var b = bgR.bounds;
                float W = bgR.sprite.rect.width, H = bgR.sprite.rect.height;
                if (b.size.x <= 0 || W <= 0 || H <= 0) continue;
                float parent = Mathf.Abs(root.lossyScale.x);
                float origW = b.size.x / Mathf.Max(0.0001f, parent), origH = b.size.y / Mathf.Max(0.0001f, parent);
                var src = cfg.Source ?? new[] { 0f, 0f, origW, origH };
                float pxw = W / b.size.x;
                float grow = origW / src[2];
                List<IntPtr> dead = null;
                foreach (var kv in fx)
                {
                    var e = kv.Value;
                    if (e.T == null || e.T.WasCollected) { (dead ??= new List<IntPtr>()).Add(kv.Key); continue; }
                    if (!e.T.IsChildOf(root)) continue;
                    var lp = e.T.localPosition; var ls = e.T.localScale;
                    if (!e.On || lp != e.SetPos) e.BasePos = lp;     // the game's own position (or the game moved it)
                    if (!e.On || ls != e.SetScale) e.BaseScale = ls;
                    var par = e.T.parent;
                    Vector3 w0 = par != null ? par.TransformPoint(e.BasePos) : e.BasePos;
                    float hx, hy;
                    if (cfg.Effects.TryGetValue(e.T.name, out var at)) { hx = at.x; hy = at.y; }
                    else
                    {
                        float ox = (w0.x - b.min.x) / b.size.x * origW, oy = (b.max.y - w0.y) / b.size.y * origH;
                        hx = (ox - src[0]) / src[2] * W; hy = (oy - src[1]) / src[3] * H;
                    }
                    var w1 = new Vector3(b.min.x + hx / pxw, b.max.y - hy / pxw, w0.z);
                    e.SetPos = par != null ? par.InverseTransformPoint(w1) : w1;
                    e.SetPos.z = e.BasePos.z;
                    e.SetScale = e.BaseScale * grow;
                    if (lp != e.SetPos) e.T.localPosition = e.SetPos;
                    if (ls != e.SetScale) e.T.localScale = e.SetScale;
                    if (!e.On) Once("fx|" + bg + "|" + e.T.name, $"[Render] animtex {bg} effect '{e.T.name}': HD background px {hx:0.#},{hy:0.#}, size x{grow:0.###}");
                    e.On = true;
                }
                if (dead != null) foreach (var d in dead) fx.Remove(d);
            }
        }
    }

    /// <summary>
    /// Replaces the loading animation (game: 4 frames UI_FF0_LoadingIcon00-03, 90x90, 0.25 s each, listed in
    /// serial_misc_assets.json "loading_anime_settings" [verified, export]) with every PNG in the "loading_icon" folder
    /// next to MenuLayout.json, in file-name order. The icon is the Image Last.Systems.Indicator.SystemIndicator.loadingIcon
    /// [verified, interop]; its sprite is set here after the game's own Update, so the game's frame timer doesn't matter.
    /// MenuLayout "loadingIcon": frameTime (s per frame, default 0.125), size (1440p px, 0 = the game's own size).
    /// No folder or no PNGs = the game's animation.
    /// </summary>
    static class LoadingIcon
    {
        internal static float FrameTime = 0.125f, Size;
        // MenuLayout loadingIcon.priority: UnityEngine.Application.backgroundLoadingPriority while the icon shows
        // (Low/BelowNormal/Normal/High [verified, interop dump]); null = leave the game's value alone.
        internal static UnityEngine.ThreadPriority? Priority;
        internal static bool Showing;  // the loading icon is on screen (for Perf)
        static bool lowered, priorityLogged;
        static UnityEngine.ThreadPriority gamePriority;
        static readonly List<Sprite> frames = new List<Sprite>();

        /// <summary>While the icon shows: keep our priority, remembering the game's own value (also if it sets a new one).</summary>
        static void HoldPriority()
        {
            if (!Priority.HasValue) { ReleasePriority(); return; }
            var cur = Application.backgroundLoadingPriority;
            if (cur == Priority.Value) return;
            gamePriority = cur; // the game's value (first time, or the game changed it during this load)
            Application.backgroundLoadingPriority = Priority.Value;
            lowered = true;
            if (!priorityLogged)
            {
                priorityLogged = true;
                // Warning level so it reaches the disk log with the default log levels; once per session.
                Plugin.L.LogWarning($"[Loading] background loading priority: game {cur}, set to {Priority.Value} while the loading icon shows (info, not an error)");
            }
        }

        internal static void ReleasePriority()
        {
            if (!lowered) return;
            lowered = false;
            try { Application.backgroundLoadingPriority = gamePriority; } catch (Exception) { }
        }
        static long folderStamp = -1;
        static float nextCheck, nextFind;
        static Last.Systems.Indicator.SystemIndicator ind;
        static IntPtr sizedPtr;
        static Vector2 origSize;
        static bool loggedIcon;

        static string Folder => Path.Combine(Path.GetDirectoryName(Plugin.ConfigPath) ?? "", "loading_icon");

        /// <summary>Reloads the frames when the folder's files change (checked every 2 s).</summary>
        static void Reload()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 2f;
            string dir = Folder;
            var files = Directory.Exists(dir) ? new List<string>(Directory.GetFiles(dir, "*.png")) : new List<string>();
            files.Sort(StringComparer.OrdinalIgnoreCase);
            long stamp = files.Count;
            foreach (var f in files) stamp = stamp * 31 + File.GetLastWriteTimeUtc(f).Ticks;
            if (stamp == folderStamp) return;
            folderStamp = stamp;
            foreach (var s in frames) if (s != null && !s.WasCollected) { UnityEngine.Object.Destroy(s.texture); UnityEngine.Object.Destroy(s); }
            frames.Clear();
            foreach (var f in files)
            {
                try
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(f))) { Plugin.L.LogWarning("[Loading] not a readable PNG: " + f); continue; }
                    tex.filterMode = FilterMode.Bilinear;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                    sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    sp.name = "LoadingIcon_" + Path.GetFileNameWithoutExtension(f);
                    frames.Add(sp);
                }
                catch (Exception e) { Plugin.L.LogWarning("[Loading] could not load " + f + ": " + e.Message); }
            }
            if (frames.Count > 0) Plugin.L.LogInfo($"[Loading] {frames.Count} frame(s) from {dir}, first {frames[0].texture.width}x{frames[0].texture.height} px, {FrameTime:0.###} s each");
            else if (files.Count == 0) Plugin.L.LogInfo("[Loading] no frames in " + dir + "; game's animation used");
        }

        internal static void Apply()
        {
            Reload();
            if (ind == null || ind.WasCollected)
            {
                ind = null;
                Showing = false;
                if (Time.unscaledTime < nextFind) return;
                nextFind = Time.unscaledTime + 1f;
                ReleasePriority();
                var o = UnityEngine.Object.FindObjectOfType(Il2CppType.Of<Last.Systems.Indicator.SystemIndicator>());
                ind = o?.TryCast<Last.Systems.Indicator.SystemIndicator>();
                if (ind == null) return;
            }
            var img = ind.loadingIcon;
            if (img == null) { Showing = false; ReleasePriority(); return; }
            var rt = img.rectTransform;
            if (rt.Pointer != sizedPtr) { sizedPtr = rt.Pointer; origSize = rt.sizeDelta; loggedIcon = false; }
            Showing = img.isActiveAndEnabled;
            if (!Showing) { ReleasePriority(); return; }
            HoldPriority();
            if (!loggedIcon)
            {
                loggedIcon = true;
                var cv = img.canvas;
                float px = cv != null ? rt.rect.height * Px.PerUnit(cv.rootCanvas) : 0;
                Plugin.L.LogInfo($"[Loading] icon shown: sprite {img.sprite?.name}, {rt.rect.width:0.#}x{rt.rect.height:0.#} units = {px:0} px tall at 1440p, preserveAspect={img.preserveAspect}");
            }
            if (frames.Count == 0)
            {
                if (rt.sizeDelta != origSize) rt.sizeDelta = origSize;
                return;
            }
            int i = (int)(Time.unscaledTime / Mathf.Max(0.01f, FrameTime)) % frames.Count;
            if (img.sprite == null || img.sprite.Pointer != frames[i].Pointer) img.sprite = frames[i];
            Vector2 want = origSize;
            if (Size > 0 && img.canvas != null)
            {
                float units = Size * Px.Upp(img.canvas.rootCanvas);
                want = new Vector2(units, units);
            }
            if (rt.sizeDelta != want) rt.sizeDelta = want;
        }
    }

    /// <summary>
    /// Highlights the selected title-menu item (main list, Extras, Options): its text fades to a colour and grows.
    /// Selected = the game's cursor item (Last.UI.KeyInput.TitleCommandContentView.isFocus [verified, interop]), or
    /// the item the mouse moved onto (its UnityEngine.UI.Button.isPointerInside [verified, interop]); whichever input
    /// changed last wins. The cursor sits inside the text, so it is scaled back by the same factor.
    /// MenuLayout "titleHighlight": color (#RRGGBB), scale (1 = no growth), fadeTime (s). Missing block = off.
    /// </summary>
    static class TitleHighlight
    {
        internal static bool Enabled;
        internal static Color HlColor;
        internal static float Scale = 1.1f, FadeTime = 0.15f;

        class Item
        {
            public Last.UI.KeyInput.TitleCommandContentView V;
            public float T;                                  // 0 = normal, 1 = fully highlighted
            public Color BaseColor, LastColor;
            public Vector3 BaseScale, LastScale, CurBase, CurLast;
            public bool HasBase;
            public int SeenSize = -1, SeenMin = -1, SeenMax = -1; // diagnostics: last font size / best-fit range seen
        }
        static readonly Dictionary<IntPtr, Item> items = new Dictionary<IntPtr, Item>();
        static float nextFind;
        static IntPtr lastFocus, lastHover, mouseItem;
        static bool mouseWins, wasOn;

        /// <summary>Event-driven: a title command's text ("last_text") was switched on; its view is a parent.</summary>
        internal static void Consider(List<Graphic> batch)
        {
            if (!Enabled) return;
            for (int bi = 0; bi < batch.Count; bi++)
            {
                var g = batch[bi];
                try
                {
                    if (Discovery.Name(bi) != "last_text" || g == null || g.WasCollected) continue;
                    var v = g.GetComponentInParent<Last.UI.KeyInput.TitleCommandContentView>();
                    if (v == null || items.ContainsKey(v.Pointer)) continue;
                    items[v.Pointer] = new Item { V = v };
                    if (Rules.Log) Plugin.L.LogInfo($"[Title] item found: {Describe(v)}");
                }
                catch (Exception e) { if (Rules.Log) Plugin.L.LogInfo("[Title] consider: " + e.Message); }
            }
        }

        /// <summary>One full search, only after the rules change (items already on screen).</summary>
        internal static void FullScan()
        {
            if (!Enabled) return;
            var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Last.UI.KeyInput.TitleCommandContentView>());
            if (found == null) return;
            foreach (var o in found)
            {
                var v = o?.TryCast<Last.UI.KeyInput.TitleCommandContentView>();
                if (v != null && !items.ContainsKey(v.Pointer)) items[v.Pointer] = new Item { V = v };
            }
        }

        internal static void Apply()
        {
            if (!Enabled)
            {
                if (wasOn) { foreach (var it in items.Values) Restore(it); items.Clear(); wasOn = false; }
                return;
            }
            wasOn = true;
            if (Time.unscaledTime >= nextFind)
            {
                // Items are found when their text is switched on (Consider); here only destroyed ones are dropped.
                nextFind = Time.unscaledTime + 2f;
                var gone = new List<IntPtr>();
                foreach (var kv in items) if (kv.Value.V == null || kv.Value.V.WasCollected) gone.Add(kv.Key);
                foreach (var k in gone) { if (Rules.Log) Plugin.L.LogInfo("[Title] item destroyed"); items.Remove(k); }
            }
            if (items.Count == 0) return;

            IntPtr focus = IntPtr.Zero, hover = IntPtr.Zero;
            foreach (var kv in items)
            {
                var v = kv.Value.V;
                if (v == null || v.WasCollected || !v.isActiveAndEnabled) continue;
                if (v.isFocus) focus = kv.Key;
                var btn = v.mouseButton?.button;
                if (btn != null && btn.isPointerInside && btn.interactable) hover = kv.Key;
            }
            if (hover != IntPtr.Zero && hover != lastHover) { mouseWins = true; mouseItem = hover; }
            if (focus != lastFocus) mouseWins = false;
            if (hover == IntPtr.Zero) mouseWins = false;
            lastHover = hover; lastFocus = focus;
            IntPtr selected = mouseWins ? mouseItem : focus;

            float step = FadeTime > 0 ? Time.unscaledDeltaTime / FadeTime : 1f;
            foreach (var kv in items)
            {
                var it = kv.Value;
                try
                {
                    if (it.V == null || it.V.WasCollected) continue;
                    if (!it.V.isActiveAndEnabled)
                    {
                        // Its menu was closed while it was highlighted: put it back to normal now, or it comes back
                        // grown and that size becomes its base (user 2026-11-05: items larger after leaving a submenu).
                        if (it.T > 0f) { it.T = 0f; Restore(it); }
                        continue;
                    }
                    it.T = Mathf.Clamp01(it.T + (kv.Key == selected ? step : -step));
                    Show(it);
                }
                catch (Exception e) { Plugin.L.LogWarning("[Title] item: " + e.Message); it.V = null; }
            }
        }

        static void Show(Item it)
        {
            var txt = it.V.nameText;
            if (txt == null) return;
            var tr = txt.transform;
            var cur = it.V.cursorParent != null ? it.V.cursorParent.transform : null;
            if (Rules.Log && (txt.fontSize != it.SeenSize || txt.resizeTextMinSize != it.SeenMin || txt.resizeTextMaxSize != it.SeenMax))
            {
                // Someone else changed the size: log when, so it lines up with KupoUI's log lines around it.
                Plugin.L.LogInfo($"[Title] size of '{txt.text}' ({it.V.transform.parent?.name}): {it.SeenSize} -> {txt.fontSize}, best fit {it.SeenMin}-{it.SeenMax} -> {txt.resizeTextMinSize}-{txt.resizeTextMaxSize} (bestFit={txt.resizeTextForBestFit}, font={txt.font?.name}) at frame {Time.frameCount}");
                it.SeenSize = txt.fontSize; it.SeenMin = txt.resizeTextMinSize; it.SeenMax = txt.resizeTextMaxSize;
            }
            // Another writer (game, KupoUI) changed a value since our last write: take it as the new base.
            if (!it.HasBase || txt.color != it.LastColor) it.BaseColor = txt.color;
            if (!it.HasBase || tr.localScale != it.LastScale)
            {
                if (Rules.Log) Plugin.L.LogInfo($"[Title] base scale {(it.HasBase ? it.BaseScale.x.ToString("0.###") : "none")} -> {tr.localScale.x:0.###} (we last wrote {it.LastScale.x:0.###}, T={it.T:0.##}, size={txt.fontSize}) '{Describe(it.V)}'");
                it.BaseScale = tr.localScale;
            }
            if (cur != null && (!it.HasBase || cur.localScale != it.CurLast)) it.CurBase = cur.localScale;
            it.HasBase = true;
            float t = Mathf.SmoothStep(0f, 1f, it.T);
            float s = Mathf.Lerp(1f, Mathf.Max(0.01f, Scale), t);
            it.LastColor = Color.Lerp(it.BaseColor, HlColor, t);
            it.LastScale = it.BaseScale * s;
            SetColor(txt, it.LastColor);
            tr.localScale = it.LastScale;
            if (cur != null) { it.CurLast = it.CurBase / s; cur.localScale = it.CurLast; }
        }

        static string Describe(Last.UI.KeyInput.TitleCommandContentView v)
        {
            try
            {
                if (v == null || v.WasCollected) return "(gone)";
                var t = v.nameText;
                return $"'{t?.text}' parent={v.transform.parent?.name} scale={(t != null ? t.transform.localScale.x.ToString("0.###") : "?")} size={t?.fontSize} active={v.isActiveAndEnabled}";
            }
            catch (Exception e) { return "(error " + e.Message + ")"; }
        }

        /// <summary>
        /// Writes the colour field directly: KupoUI's ObjectConfig "TextColorWhite" prefixes Graphic.set_color and
        /// forces white (KupoUI.PR.Patches.ObjectConfigPatch.GraphicColorSetterPrefix [verified, KupoUI IL]).
        /// </summary>
        static void SetColor(Text txt, Color c)
        {
            if (txt.m_Color == c) return;
            txt.m_Color = c;
            txt.SetVerticesDirty();
        }

        static void Restore(Item it)
        {
            try
            {
                if (it.V == null || it.V.WasCollected || !it.HasBase) return;
                var txt = it.V.nameText;
                if (txt != null) { SetColor(txt, it.BaseColor); txt.transform.localScale = it.BaseScale; }
                if (it.V.cursorParent != null) it.V.cursorParent.transform.localScale = it.CurBase;
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// With a camera's render texture enlarged, the game's own pixel maths (damage numbers, target cursor) still
    /// assumes the original size: those elements vanished at 2560x1440 (test 9). These patches on
    /// UnityEngine.Camera (methods as listed in the interop dump) convert to and from the original pixel size for
    /// enlarged cameras only; other cameras are untouched.
    /// </summary>
    static class CameraPointPatches
    {
        static bool Scale(Camera cam, out float k)
        {
            k = 1f;
            return RenderScale.Factor.Count > 0 && cam != null && RenderScale.Factor.TryGetValue(cam.Pointer, out k);
        }

        [HarmonyLib.HarmonyPatch(typeof(Camera), nameof(Camera.WorldToScreenPoint), new[] { typeof(Vector3) })]
        static class W2S
        {
            static void Postfix(Camera __instance, ref Vector3 __result)
            {
                try { if (Scale(__instance, out var k)) { __result.x /= k; __result.y /= k; } } catch { }
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(Camera), nameof(Camera.WorldToScreenPoint), new[] { typeof(Vector3), typeof(Camera.MonoOrStereoscopicEye) })]
        static class W2SEye
        {
            static void Postfix(Camera __instance, ref Vector3 __result)
            {
                try { if (Scale(__instance, out var k)) { __result.x /= k; __result.y /= k; } } catch { }
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(Camera), nameof(Camera.ScreenToWorldPoint), new[] { typeof(Vector3) })]
        static class S2W
        {
            static void Prefix(Camera __instance, ref Vector3 position)
            {
                try { if (Scale(__instance, out var k)) { position.x *= k; position.y *= k; } } catch { }
            }
        }

        [HarmonyLib.HarmonyPatch(typeof(Camera), nameof(Camera.ScreenToWorldPoint), new[] { typeof(Vector3), typeof(Camera.MonoOrStereoscopicEye) })]
        static class S2WEye
        {
            static void Prefix(Camera __instance, ref Vector3 position)
            {
                try { if (Scale(__instance, out var k)) { position.x *= k; position.y *= k; } } catch { }
            }
        }
    }

    /// <summary>
    /// Battle render resolution test. SettingPixelPerfect (global namespace in the dump: isBattle, resolutionScale,
    /// fixedHeightSize, Update) is believed to draw battle into a low-resolution image. Every 2 s (from the watcher)
    /// logs each instance and every camera's target texture whenever that picture changes, and, if MenuLayout.json
    /// "battle.resolutionScale" > 0, writes that value to instances with isBattle.
    /// </summary>
    static class Battle
    {
        internal static float ResolutionScale;
        static string lastState = "";

        internal static void Check()
        {
            var parts = new List<string>();
            var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SettingPixelPerfect>());
            if (found != null)
                foreach (var o in found)
                {
                    var spp = o?.TryCast<SettingPixelPerfect>();
                    if (spp == null) continue;
                    if (ResolutionScale > 0 && spp.isBattle && spp.resolutionScale != ResolutionScale)
                    {
                        Plugin.L.LogInfo($"[Battle] '{spp.name}': resolutionScale {spp.resolutionScale} -> {ResolutionScale}");
                        spp.resolutionScale = ResolutionScale;
                        spp.currentScreen_ = Vector2.zero; // in case it only rebuilds when the screen size changes
                    }
                    parts.Add($"SettingPixelPerfect '{PathOf(spp.transform)}' enabled={spp.isActiveAndEnabled} isBattle={spp.isBattle} resolutionScale={spp.resolutionScale} fixedHeightSize={spp.fixedHeightSize} currentScreen={spp.currentScreen_}");
                }
            foreach (var cam in Camera.allCameras)
            {
                var rt = cam.targetTexture;
                parts.Add($"camera '{cam.name}' ortho={cam.orthographic} size={cam.orthographicSize:0.##} rect={cam.pixelWidth}x{cam.pixelHeight} target={(rt == null ? "screen" : rt.name + " " + rt.width + "x" + rt.height + " filter " + rt.filterMode)}");
            }
            string state = string.Join("\n", parts);
            if (state == lastState) return;
            lastState = state;
            Plugin.L.LogInfo($"[Battle] state (Screen {Screen.width}x{Screen.height}):");
            foreach (var p in parts) Plugin.L.LogInfo("[Battle]   " + p);
        }

        static string PathOf(Transform t)
        {
            var names = new List<string>();
            for (int i = 0; i < 6 && t != null; i++, t = t.parent) names.Insert(0, t.name);
            return string.Join("/", names);
        }
    }

    /// <summary>
    /// Applies MenuLayout.json "elements": finds matching UI graphics (Text, Image) and SpriteRenderers a few times a
    /// second and keeps them moved/resized/recoloured. If the game moves an element again, its new position becomes
    /// the base and the offset is re-added. Everything is put back when the rules change.
    /// </summary>
    static class Elements
    {
        class Item
        {
            public Transform T;
            public Graphic G;          // UI element (null for a SpriteRenderer)
            public SpriteRenderer SR;
            public HorizontalOrVerticalLayoutGroup LG;
            public ElementRule R;
            public bool Applied, TextSaved;
            public Vector3 BasePos, SetPos;
            public bool ScaleApplied;
            public Vector3 BaseScale, SetScale;
            public int BaseSize = -1;
            public bool BaseBestFit;
            public HorizontalWrapMode BaseHOverflow;
            public VerticalWrapMode BaseVOverflow;
            public float BaseSpacing = float.NaN;
            public int[] BaseListPad;           // game's layout padding (left, right, top, bottom), saved on first change
            public TextAnchor? BaseListAlign;
            public Font BaseFont;
            public bool SizeSaved;
            public Vector2 BaseSizeDelta;
            public Vector3 PlaceBasePos;
            public bool PlaceSaved;
            public float WidenSet = -1;
            public bool WidenLogged;
            public bool HeightSaved;
            public float BaseMinH, BasePrefH;
            public bool WidthSaved, HeightLogged, WidthLogged;
            public float BaseMinW, BasePrefW;
            public LayoutElement AddedLE;       // "height"/"width" in a layout group without its own LayoutElement
            public Sprite SwapSet, SwapBase;    // "image": our sprite on the Image, and the game's sprite it replaced
            public bool SwapWarned;
            public CanvasGroup CG;              // "alpha"/"hide": the object's CanvasGroup (ours if AddedCG)
            public bool AddedCG, AlphaApplied, BaseBlocks;
            public float BaseAlpha, SetAlpha;
            public float SetDown;               // moveDown (+ spritesDown) last applied, px
            public float SetRight;              // moveRight (+ party offset) last applied, px
            public int SourceState = -1;        // addSpritesDown: last logged cursor-source state (0 none, 1 party, 2 other)
            public IntPtr CursorPtr;            // addSpritesDown: the game's Last.UI.Cursor above it (party offsets, depthScale)
            public bool CursorIsHand;           // that cursor is the target hand (common_cursor): aims at the figure's edge
            public float LogDepthF = -1f;       // depthScale factor last logged
            public bool FocusSearched;          // "selected": row focus flag looked up once
            public Func<bool> Focus;            // reads the row's isFocus; null = no known list row above
            public bool UnitSearched;           // "selected" without isFocus: cursor unit looked up once
            public Transform CursorUnit;        // nearest ancestor holding a 'cursor_root' (one entry of the list)
            public bool OwnColor;               // the colour shown is ours (OwnSet); otherwise it is the game's
            public Color OwnSet, GameColor;     // GameColor: last colour the game itself gave the object
            public bool GameColorKnown;
            public bool PadSaved;               // "padding": box edges before and after our change
            public Vector2 PadBaseMin, PadBaseMax, PadSetMin, PadSetMax;
            public bool SizeHeldLogged;         // fontScale: another mod kept the size, logged once
            public IntPtr FontSet;              // font we put on the text last (to put the original back)
            public bool Rounded;               // "radius": our rounded sprite was put on a plain box
            public float BasePpuMult;
            public int BaseMaxSize = -1;
            public int BaseMinSize = -1;
            public bool MaxLogged, MaxResetLogged;
            public Dictionary<string, Transform> Refs; // Below/CenterOver objects, found once
            public TextStyler.State StyleState;
            public Canvas AddedCanvas;          // onTop / backdrop: our own sorting canvas on the element
            public GameObject Backdrop;         // backdrop box (child of the element, own canvas one order lower)
        }

        static readonly Dictionary<IntPtr, Item> items = new Dictionary<IntPtr, Item>();

        /// <summary>
        /// onTop / backdrop: the element gets its own Canvas with overrideSorting at (root canvas order + 2), so it draws
        /// over neighbouring windows; the backdrop is a child Image with its own Canvas at (+1): above everything else,
        /// under the text. Sized to the text's letters (preferredWidth/Height) plus padding, aligned like the text.
        /// </summary>
        static void Layer(Item it)
        {
            var r = it.R;
            var parentCanvas = it.T.parent != null ? it.T.parent.GetComponentInParent<Canvas>() : null;
            if (parentCanvas == null) return;
            int baseOrder = parentCanvas.rootCanvas.sortingOrder;
            if (it.AddedCanvas == null || it.AddedCanvas.WasCollected)
            {
                var existing = it.T.GetComponent<Canvas>();
                if (existing != null) return; // the game's own canvas: leave it alone
                it.AddedCanvas = it.T.gameObject.AddComponent<Canvas>();
                it.AddedCanvas.overrideSorting = true;
                it.AddedCanvas.sortingOrder = baseOrder + 2;
                if (Rules.Log) Plugin.L.LogInfo($"[Elements] '{r.Name}' {it.T.name}: drawn on top (order {baseOrder + 2})");
            }
            if (!r.BackdropColor.HasValue) return;
            if (r.BackdropMerge > 0) { JoinMerged(it, parentCanvas); return; }
            var txt = it.G.TryCast<Text>();
            if (it.Backdrop == null || it.Backdrop.WasCollected)
            {
                var go = new GameObject("spritemod_backdrop");
                go.layer = it.T.gameObject.layer;
                go.transform.SetParent(it.T, false);
                go.AddComponent<RectTransform>();
                var bc = go.AddComponent<Canvas>();
                bc.overrideSorting = true;
                bc.sortingOrder = baseOrder + 1;
                var img = go.AddComponent<Image>();
                img.raycastTarget = false;
                it.Backdrop = go;
            }
            var bimg = it.Backdrop.GetComponent<Image>();
            if (bimg != null && bimg.color != r.BackdropColor.Value) bimg.color = r.BackdropColor.Value;
            bool show = txt == null || !string.IsNullOrEmpty(txt.text);
            if (it.Backdrop.activeSelf != show) it.Backdrop.SetActive(show);
            if (!show) return;
            // Size in the element's own units: 1440p px -> root units, divided by the element's scale relative to the root.
            var root = parentCanvas.rootCanvas;
            float upp = Px.Upp(root);
            float rel = it.T.lossyScale.x / Mathf.Max(0.0001f, root.transform.lossyScale.x);
            float k = upp / Mathf.Max(0.0001f, rel);
            if (bimg != null) Round(bimg, r.BackdropRadius, k);
            var own = it.T.GetComponent<RectTransform>();
            float w = txt != null ? Mathf.Min(txt.preferredWidth, own.rect.width) : own.rect.width;
            float h = txt != null ? Mathf.Min(txt.preferredHeight, own.rect.height) : own.rect.height;
            var size = new Vector2(w + 2 * r.BackdropPad.x * k, h + 2 * r.BackdropPad.y * k);
            float ax = 0.5f;
            if (txt != null)
            {
                var a = txt.alignment;
                if (a == TextAnchor.UpperLeft || a == TextAnchor.MiddleLeft || a == TextAnchor.LowerLeft) ax = 0f;
                else if (a == TextAnchor.UpperRight || a == TextAnchor.MiddleRight || a == TextAnchor.LowerRight) ax = 1f;
            }
            var brt = it.Backdrop.GetComponent<RectTransform>();
            var anchor = new Vector2(ax, 0.5f);
            var pos = new Vector2(ax == 0f ? -r.BackdropPad.x * k : ax == 1f ? r.BackdropPad.x * k : 0f, 0f);
            if (brt.anchorMin != anchor) { brt.anchorMin = anchor; brt.anchorMax = anchor; brt.pivot = anchor; }
            if ((brt.sizeDelta - size).sqrMagnitude > 0.01f) brt.sizeDelta = size;
            if ((brt.anchoredPosition - pos).sqrMagnitude > 0.01f) brt.anchoredPosition = pos;
        }

        // backdrop.radius: one shared white rounded-square sprite, drawn 9-sliced. Its corner is RoundPx texture pixels;
        // pixelsPerUnitMultiplier scales that corner to the wanted radius (corner units = RoundPx / multiplier at 100 ppu).
        const int RoundPx = 32;
        static Sprite roundSprite;
        static bool roundFailed;

        static Sprite RoundSprite()
        {
            if (roundSprite != null && !roundSprite.WasCollected) return roundSprite;
            if (roundFailed) return null;
            try
            {
                int n = 2 * RoundPx + 2; // 2 px straight middle so the slices have something to stretch
                var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
                tex.hideFlags = HideFlags.HideAndDontSave;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        // Distance from the pixel centre to the nearest point of the inner (straight-edged) square.
                        float px = x + 0.5f, py = y + 0.5f;
                        float cx = Mathf.Clamp(px, RoundPx, n - RoundPx), cy = Mathf.Clamp(py, RoundPx, n - RoundPx);
                        float d = Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
                        float a = Mathf.Clamp01(RoundPx - d + 0.5f); // 1 px anti-aliased edge
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                    }
                tex.Apply(false, true);
                roundSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                                            new Vector4(RoundPx, RoundPx, RoundPx, RoundPx));
                roundSprite.hideFlags = HideFlags.HideAndDontSave;
                roundSprite.name = "spritemod_round";
                return roundSprite;
            }
            catch (Exception e)
            {
                roundFailed = true;
                Plugin.L.LogWarning("[Elements] backdrop radius: could not make the rounded box, boxes stay square: " + e.Message);
                return null;
            }
        }

        /// <summary>radius in 1440p px; k = the box's local units per 1440p px.</summary>
        static void Round(Image img, float radius, float k)
        {
            if (radius <= 0f)
            {
                if (img.sprite != null) { img.sprite = null; img.type = Image.Type.Simple; }
                return;
            }
            var s = RoundSprite();
            if (s == null) return;
            if (img.sprite == null || img.sprite.Pointer != s.Pointer) img.sprite = s;
            if (img.type != Image.Type.Sliced) img.type = Image.Type.Sliced;
            float mult = RoundPx / Mathf.Max(0.01f, radius * k);
            if (Mathf.Abs(img.pixelsPerUnitMultiplier - mult) > 0.001f) img.pixelsPerUnitMultiplier = mult;
        }

        /// <summary>backdrop.merge: one box per ancestor (N levels up) around all its matching texts, e.g. a column.</summary>
        class Merged
        {
            public Transform Anc;
            public GameObject Box;
            public ElementRule R;
            public Canvas Root;
            public int BaseOrder;
            public readonly List<Item> Members = new List<Item>();
        }
        static readonly Dictionary<IntPtr, Merged> merged = new Dictionary<IntPtr, Merged>();

        static void JoinMerged(Item it, Canvas parentCanvas)
        {
            var anc = it.T;
            for (int i = 0; i < it.R.BackdropMerge && anc != null; i++) anc = anc.parent;
            if (anc == null) return;
            if (!merged.TryGetValue(anc.Pointer, out var m))
                merged[anc.Pointer] = m = new Merged { Anc = anc, R = it.R };
            m.Root = parentCanvas.rootCanvas;
            m.BaseOrder = m.Root.sortingOrder;
            m.Members.Add(it);
        }

        /// <summary>Sizes each merged box around its members' letters (in the ancestor's space); called after each Apply pass.</summary>
        static void UpdateMerged()
        {
            if (merged.Count == 0) return;
            List<IntPtr> dead = null;
            foreach (var kv in merged)
            {
                var m = kv.Value;
                try
                {
                    if (m.Anc == null || m.Anc.WasCollected) { (dead ??= new List<IntPtr>()).Add(kv.Key); continue; }
                    float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                    foreach (var it in m.Members)
                    {
                        var txt = it.G?.TryCast<Text>();
                        if (txt == null || !txt.isActiveAndEnabled || string.IsNullOrEmpty(txt.text)) continue;
                        var own = it.T.GetComponent<RectTransform>();
                        var rc = own.rect;
                        float w = Mathf.Min(txt.preferredWidth, rc.width), h = Mathf.Min(txt.preferredHeight, rc.height);
                        var a = txt.alignment;
                        float x0 = (a == TextAnchor.UpperLeft || a == TextAnchor.MiddleLeft || a == TextAnchor.LowerLeft) ? rc.xMin
                                 : (a == TextAnchor.UpperRight || a == TextAnchor.MiddleRight || a == TextAnchor.LowerRight) ? rc.xMax - w
                                 : rc.center.x - w / 2f;
                        float y0 = rc.center.y - h / 2f;
                        foreach (var p in new[] { new Vector3(x0, y0, 0f), new Vector3(x0 + w, y0 + h, 0f) })
                        {
                            var q = m.Anc.InverseTransformPoint(it.T.TransformPoint(p));
                            minX = Mathf.Min(minX, q.x); maxX = Mathf.Max(maxX, q.x);
                            minY = Mathf.Min(minY, q.y); maxY = Mathf.Max(maxY, q.y);
                        }
                    }
                    bool show = minX <= maxX;
                    if (m.Box == null || m.Box.WasCollected)
                    {
                        if (!show) continue;
                        var go = new GameObject("spritemod_backdrop_merged");
                        go.layer = m.Anc.gameObject.layer;
                        go.transform.SetParent(m.Anc, false);
                        go.AddComponent<RectTransform>();
                        go.AddComponent<LayoutElement>().ignoreLayout = true; // the ancestor may lay out its children
                        var bc = go.AddComponent<Canvas>();
                        bc.overrideSorting = true;
                        bc.sortingOrder = m.BaseOrder + 1;
                        var img = go.AddComponent<Image>();
                        img.raycastTarget = false;
                        m.Box = go;
                    }
                    if (m.Box.activeSelf != show) m.Box.SetActive(show);
                    if (!show) continue;
                    var bimg = m.Box.GetComponent<Image>();
                    if (bimg != null && bimg.color != m.R.BackdropColor.Value) bimg.color = m.R.BackdropColor.Value;
                    float upp = Px.Upp(m.Root);
                    float rel = m.Anc.lossyScale.x / Mathf.Max(0.0001f, m.Root.transform.lossyScale.x);
                    float k = upp / Mathf.Max(0.0001f, rel);
                    if (bimg != null) Round(bimg, m.R.BackdropRadius, k);
                    var size = new Vector2(maxX - minX + 2 * m.R.BackdropPad.x * k, maxY - minY + 2 * m.R.BackdropPad.y * k);
                    var brt = m.Box.GetComponent<RectTransform>();
                    var half = new Vector2(0.5f, 0.5f);
                    if (brt.anchorMin != half) { brt.anchorMin = half; brt.anchorMax = half; brt.pivot = half; }
                    if ((brt.sizeDelta - size).sqrMagnitude > 0.01f) brt.sizeDelta = size;
                    var center = new Vector3((minX + maxX) / 2f, (minY + maxY) / 2f, 0f);
                    if ((brt.localPosition - center).sqrMagnitude > 0.01f) brt.localPosition = center;
                }
                catch (Exception e) { (dead ??= new List<IntPtr>()).Add(kv.Key); if (Rules.Log) Plugin.L.LogInfo("[Elements] merged backdrop: " + e.Message); }
                finally { m.Members.Clear(); }
            }
            if (dead != null) foreach (var k in dead) { DestroyMerged(merged[k]); merged.Remove(k); }
        }

        static void DestroyMerged(Merged m)
        {
            try { if (m.Box != null && !m.Box.WasCollected) UnityEngine.Object.Destroy(m.Box); } catch (Exception) { }
            m.Box = null;
        }

        static void Unlayer(Item it)
        {
            try
            {
                if (it.Backdrop != null && !it.Backdrop.WasCollected) UnityEngine.Object.Destroy(it.Backdrop);
                if (it.AddedCanvas != null && !it.AddedCanvas.WasCollected) UnityEngine.Object.Destroy(it.AddedCanvas);
            }
            catch (Exception) { }
            it.Backdrop = null; it.AddedCanvas = null;
        }

        /// <summary>The style an element rule sets on this graphic (for FontTypeStyles to leave those properties alone).</summary>
        internal static TextStyle StyleOf(IntPtr graphic) => items.TryGetValue(graphic, out var it) ? it.R.Style : null;

        /// <summary>True if an element rule picks this graphic's font ("font" or a face via fontStyle).</summary>
        internal static bool OwnsFont(IntPtr graphic) =>
            items.TryGetValue(graphic, out var it) && (!string.IsNullOrEmpty(it.R.Font) || it.R.Style?.Face != null ||
            // a size change swaps in our own copy of the font, so a font-type face must not swap it back
            it.R.FontScale > 0 || (it.R.Selected != null && (it.R.Selected.FontScale > 0 || it.R.Selected.Font != null)) ||
            (it.R.Greyed != null && (it.R.Greyed.FontScale > 0 || it.R.Greyed.Font != null)));

        internal static string NoClone(string s) => s.Replace("(Clone)", "");

        internal static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; i < 14 && t != null; i++, t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }

        /// <summary>The "spacing" rule for a list that is not a Unity layout group (ScrollSpacing).</summary>
        internal static ElementRule MatchSpacing(Transform t)
        {
            var rules = Rules.Elements;
            if (rules.Count == 0) return null;
            var r = Match(rules, InBattle(), PathOf(t), null, true);
            return r != null && r.Spacing >= 0 ? r : null; // scrolling lists: spacing only
        }

        internal static bool InBattle()
        {
            foreach (var cam in Camera.allCameras) if (cam.name == "CameraBattle") return true;
            return false;
        }

        static readonly HashSet<string> seenSprites = new HashSet<string>();

        /// <summary>
        /// With "log" on, lists each sprite drawn in battle once (UI Image or SpriteRenderer, sprite name, object path,
        /// 1440p screen position for UI), to find names for "sprite" rules.
        /// </summary>
        static void LogBattleSprites()
        {
            var imgs = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Image>());
            if (imgs != null)
                foreach (var o in imgs)
                {
                    var img = o?.TryCast<Image>();
                    if (img == null || img.sprite == null || !img.isActiveAndEnabled) continue;
                    string path = PathOf(img.transform);
                    if (!seenSprites.Add("I|" + img.sprite.name + "|" + NoClone(path))) continue;
                    var root = img.canvas != null ? img.canvas.rootCanvas : null;
                    string where = "";
                    if (root != null && root.renderMode != RenderMode.WorldSpace)
                    {
                        var c = new Il2CppStructArray<Vector3>(4);
                        img.rectTransform.GetWorldCorners(c);
                        Vector3 a = root.transform.InverseTransformPoint(c[0]), b = root.transform.InverseTransformPoint(c[2]);
                        float ppu = Px.PerUnit(root);
                        where = $" x {a.x * ppu + 1280:0}-{b.x * ppu + 1280:0} y {720 - b.y * ppu:0}-{720 - a.y * ppu:0}";
                    }
                    Plugin.L.LogInfo($"[Sprites] Image sprite='{img.sprite.name}' '{path}'{where}");
                }
            var srs = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SpriteRenderer>());
            if (srs != null)
                foreach (var o in srs)
                {
                    var sr = o?.TryCast<SpriteRenderer>();
                    if (sr == null || sr.sprite == null || !sr.enabled || !sr.gameObject.activeInHierarchy) continue;
                    string path = PathOf(sr.transform);
                    if (!seenSprites.Add("S|" + sr.sprite.name + "|" + NoClone(path))) continue;
                    Plugin.L.LogInfo($"[Sprites] SpriteRenderer sprite='{sr.sprite.name}' '{path}' world {sr.transform.position}");
                }
        }

        internal static void Scan()
        {
            var rules = Rules.Elements;
            bool battle = InBattle();
            if (Rules.Log && battle) LogBattleSprites();
            if (rules.Count == 0) return;
            var leaves = new HashSet<string>();
            bool bySprite = false;
            foreach (var r in rules)
            {
                if (r.BattleOnly && !battle) continue;
                if (r.Leaf != null) leaves.Add(r.Leaf);
                if (r.Sprite != null) bySprite = true;
            }
            UpdateTextFlags(rules);
            if (leaves.Count == 0 && !bySprite && !textWithoutPath) return;

            var graphics = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Graphic>());
            if (graphics != null)
                foreach (var o in graphics)
                {
                    var g = o?.TryCast<Graphic>();
                    if (g == null) continue;
                    bool leaf = leaves.Contains(NoClone(g.name));
                    bool textCand = textWithoutPath && g.TryCast<Text>() != null;
                    if (items.ContainsKey(g.Pointer)) { if (leaf || textCand) MaybeWatch(rules, battle, g, null); continue; }
                    string spriteName = null;
                    if (bySprite) { var img = g.TryCast<Image>(); spriteName = img?.sprite?.name; }
                    if (!leaf && spriteName == null && !textCand) continue;
                    string gpath = leaf || textCand ? PathOf(g.transform) : null;
                    var r = Match(rules, battle, gpath, spriteName, false, g.transform);
                    if (r != null) Add(new Item { T = g.transform, G = g, R = r });
                    if (gpath != null) MaybeWatch(rules, battle, g, gpath);
                }
            var groups = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<HorizontalOrVerticalLayoutGroup>());
            if (groups != null)
                foreach (var o in groups)
                {
                    var lg = o?.TryCast<HorizontalOrVerticalLayoutGroup>();
                    if (lg == null || !leaves.Contains(NoClone(lg.name)) || items.ContainsKey(lg.Pointer)) continue;
                    var r = Match(rules, battle, PathOf(lg.transform), null, true);
                    if (r != null) Add(new Item { T = lg.transform, LG = lg, R = r });
                }
            // Containers that draw nothing themselves (e.g. a row's number block): moves still apply to them.
            if (leaves.Count > 0)
            {
                var rts = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<RectTransform>());
                if (rts != null)
                    foreach (var o in rts)
                    {
                        var rt = o?.TryCast<RectTransform>();
                        if (rt == null || !leaves.Contains(NoClone(rt.name)) || items.ContainsKey(rt.Pointer)) continue;
                        var g = rt.GetComponent<Graphic>();
                        if (g != null) continue; // handled above
                        var r = Match(rules, battle, PathOf(rt), null, false);
                        if (r != null && r.ForContainer) Add(new Item { T = rt, R = r });
                    }
            }
            if (!bySprite) return;
            var srs = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SpriteRenderer>());
            if (srs != null)
                foreach (var o in srs)
                {
                    var sr = o?.TryCast<SpriteRenderer>();
                    if (sr == null || sr.sprite == null || items.ContainsKey(sr.Pointer)) continue;
                    var r = Match(rules, battle, null, sr.sprite.name, false);
                    if (r != null) Add(new Item { T = sr.transform, SR = sr, R = r });
                }
        }

        /// <summary>
        /// Event-driven discovery (replaces the old 5x/s scene sweeps, ~48 ms each [perf log 2026-11-05]): checks graphics
        /// that were just switched on (Graphic.OnEnable postfix), plus their parents up to 16 levels for layout-group
        /// "spacing" rules and container moves. Same matching as Scan().
        /// </summary>
        internal static void Consider(List<Graphic> batch, bool battle)
        {
            var rules = Rules.Elements;
            if (rules.Count == 0 || batch.Count == 0) return;
            var leaves = new HashSet<string>();
            bool bySprite = false;
            foreach (var r in rules)
            {
                if (r.BattleOnly && !battle) continue;
                if (r.Leaf != null) leaves.Add(r.Leaf);
                if (r.Sprite != null) bySprite = true;
            }
            UpdateTextFlags(rules);
            if (leaves.Count == 0 && !bySprite && !textWithoutPath) return;
            var seenParents = new HashSet<IntPtr>(); // rows share parents: walk each parent chain once per batch
            for (int bi = 0; bi < batch.Count; bi++)
            {
                var g = batch[bi];
                try
                {
                    if (g == null || g.WasCollected) continue;
                    bool leaf = leaves.Contains(NoClone(Discovery.Name(bi)));
                    bool textCand = textWithoutPath && g.TryCast<Text>() != null;
                    if (!items.ContainsKey(g.Pointer))
                    {
                        string spriteName = null;
                        if (bySprite) { var img = g.TryCast<Image>(); spriteName = img?.sprite?.name; }
                        if (leaf || spriteName != null || textCand)
                        {
                            string gpath = leaf || textCand ? PathOf(g.transform) : null;
                            var r = Match(rules, battle, gpath, spriteName, false, g.transform);
                            if (r != null) Add(new Item { T = g.transform, G = g, R = r });
                            if (gpath != null) MaybeWatch(rules, battle, g, gpath);
                        }
                    }
                    else if (leaf || textCand) MaybeWatch(rules, battle, g, null);
                    var a = g.transform.parent;
                    for (int up = 0; up < 16 && a != null; up++, a = a.parent) // 16: the battle menu container is 6+ levels above its graphics [verified, F8 dump 2026-11-07]
                    {
                        if (!seenParents.Add(a.Pointer)) break; // this chain was walked already for a sibling
                        if (items.ContainsKey(a.Pointer) || !leaves.Contains(NoClone(a.name))) continue;
                        string path = PathOf(a);
                        var lg = a.GetComponent<HorizontalOrVerticalLayoutGroup>();
                        if (lg != null && !items.ContainsKey(lg.Pointer))
                        {
                            var r = Match(rules, battle, path, null, true);
                            if (r != null) { Add(new Item { T = a, LG = lg, R = r }); continue; }
                        }
                        // A container that draws nothing (it may still hold a layout group, e.g. the equip screen's
                        // number row): move rules apply to it. The old sweep did this; the first rewrite skipped it.
                        if (a.GetComponent<Graphic>() != null) continue; // a graphic: handled when it is switched on
                        var rc = Match(rules, battle, path, null, false);
                        if (rc != null && rc.ForContainer) Add(new Item { T = a, R = rc });
                    }
                }
                catch (Exception e) { if (Rules.Log) Plugin.L.LogInfo("[Elements] consider: " + e.Message); }
            }
        }

        /// <summary>1-based position of t among its parent's children that have the same name.</summary>
        static int SameNameIndex(Transform t)
        {
            var p = t.parent;
            if (p == null) return 1;
            string n = t.name;
            int k = 0;
            for (int i = 0; i < p.childCount; i++)
            {
                var c = p.GetChild(i);
                if (c.name != n) continue;
                k++;
                if (c.Pointer == t.Pointer) return k;
            }
            return 0;
        }

        internal static bool HasSpriteRules()
        {
            foreach (var r in Rules.Elements) if (r.Sprite != null) return true;
            return false;
        }

        /// <summary>First rule that fits; spacing rules only fit layout groups (group=true) and the others only graphics.</summary>
        static ElementRule Match(List<ElementRule> rules, bool battle, string path, string spriteName, bool group, Transform t = null)
        {
            // "text" rules first (they are narrower, and add-on rules come after the main file's): pass 0 = text rules only.
            string shown = null; bool read = false;
            for (int pass = anyTextRule ? 0 : 1; pass < 2; pass++)
                foreach (var r in rules)
                {
                    if ((r.TextRx != null) != (pass == 0)) continue;
                    if (!Fits(r, battle, path, spriteName, group, t)) continue;
                    if (r.TextRx != null)
                    {
                        if (!read) { read = true; shown = t?.GetComponent<Text>()?.text; }
                        if (shown == null || !r.TextRx.IsMatch(shown.Trim())) continue;
                    }
                    return r;
                }
            return null;
        }

        /// <summary>Every selector except "text".</summary>
        static bool Fits(ElementRule r, bool battle, string path, string spriteName, bool group, Transform t)
        {
            if (r.BattleOnly && !battle) return false;
            if (r.Nth > 0 && (t == null || SameNameIndex(t) != r.Nth)) return false;
            if (r.IsGroup != group) return false;
            // "(Clone)" is ignored on both sides: rows the game instantiates get it, prefab children do not.
            if (r.Within != null && (path == null || !NoClone(path).Contains(NoClone(r.Within)))) return false;
            if (r.Path != null && (path == null || !NoClone(path).EndsWith(NoClone(r.Path)))) return false;
            if (r.Sprite != null && (spriteName == null || !spriteName.StartsWith(r.Sprite))) return false;
            if (r.TextRx != null && (t == null || t.GetComponent<Text>() == null)) return false;
            if (r.FontType != null)
            {
                var fs = t?.GetComponent<Last.UI.FontSelector>();
                if (fs == null || fs.type.ToString() != r.FontType) return false;
            }
            return true;
        }

        /// <summary>
        /// "text" rules: texts that fit a text rule's other selectors are watched, because the game changes a row's words
        /// without switching it off and on (list rows are reused). WatchText re-matches them when their words change.
        /// </summary>
        class Watched { public Graphic G; public string Path, Last; }
        static readonly Dictionary<IntPtr, Watched> watch = new Dictionary<IntPtr, Watched>();
        static bool anyTextRule, textWithoutPath;

        static void UpdateTextFlags(List<ElementRule> rules)
        {
            anyTextRule = textWithoutPath = false;
            foreach (var r in rules)
                if (r.TextRx != null) { anyTextRule = true; if (r.Path == null && r.Sprite == null) textWithoutPath = true; }
        }

        static void MaybeWatch(List<ElementRule> rules, bool battle, Graphic g, string path)
        {
            if (!anyTextRule || watch.ContainsKey(g.Pointer) || g.TryCast<Text>() == null) return;
            if (path == null) path = PathOf(g.transform);
            foreach (var r in rules)
                if (r.TextRx != null && Fits(r, battle, path, null, false, g.transform))
                {
                    watch[g.Pointer] = new Watched { G = g, Path = path, Last = g.TryCast<Text>().text };
                    if (Rules.Log) Plugin.L.LogInfo($"[Elements] watching words of '{path}' for '{r.Name}' (now '{watch[g.Pointer].Last}')");
                    return;
                }
        }

        /// <summary>Called ~10x/s: a watched text whose words changed gets the rule that fits now (or none).</summary>
        internal static void WatchText()
        {
            if (watch.Count == 0) return;
            var rules = Rules.Elements;
            bool battle = InBattle();
            List<IntPtr> dead = null;
            foreach (var kv in watch)
            {
                var w = kv.Value;
                try
                {
                    if (w.G == null || w.G.WasCollected) { (dead ??= new List<IntPtr>()).Add(kv.Key); continue; }
                    if (!w.G.gameObject.activeInHierarchy) continue;
                    string now = w.G.TryCast<Text>()?.text;
                    if (now == w.Last) continue;
                    w.Last = now;
                    var r = Match(rules, battle, w.Path, null, false, w.G.transform);
                    items.TryGetValue(kv.Key, out var cur);
                    if (cur?.R == r) continue;
                    if (Rules.Log) Plugin.L.LogInfo($"[Elements] words now '{now}' on '{w.Path}': rule '{cur?.R.Name ?? "-"}' -> '{r?.Name ?? "-"}'");
                    if (cur != null) { RestoreItem(cur); items.Remove(kv.Key); }
                    if (r != null) Add(new Item { T = w.G.transform, G = w.G, R = r });
                }
                catch (Exception e)
                {
                    (dead ??= new List<IntPtr>()).Add(kv.Key);
                    if (Rules.Log) Plugin.L.LogInfo("[Elements] watch: " + e.Message);
                }
            }
            if (dead != null) foreach (var k in dead) watch.Remove(k);
        }

        static void Add(Item it)
        {
            var key = it.G != null ? it.G.Pointer : it.LG != null ? it.LG.Pointer : it.SR != null ? it.SR.Pointer : it.T.Pointer;
            items[key] = it;
            if (Rules.Log)
                Plugin.L.LogInfo($"[Elements] '{it.R.Name}': {(it.G != null ? it.G.GetIl2CppType().Name : it.LG != null ? "LayoutGroup" : it.SR != null ? "SpriteRenderer" : "RectTransform")} '{PathOf(it.T)}'" +
                                 $" layoutGroup={(it.T.parent != null && it.T.parent.GetComponent<LayoutGroup>() != null)}");
        }

        /// <summary>1440p screen px -> a world-space vector for this element (right, down).</summary>
        /// <summary>
        /// addSpritesDown elements that follow a party member (the game's Last.UI.Cursor, PositionConstraint source 0
        /// [verified, interop]: active-character arrow, target hand on a party member) move with that member's
        /// partyRight/partyDown/partySlots/backRowShift offset, px. Zero for anything else (enemies, no cursor).
        /// </summary>
        static Vector2 PartyOffsetOf(Item it)
        {
            try
            {
                Last.UI.Cursor cur = null;
                var a = it.T;
                for (int d = 0; d < 4 && a != null && cur == null; d++, a = a.parent) cur = a.GetComponent<Last.UI.Cursor>();
                if (cur == null)
                {
                    if (it.SourceState != 0) { it.SourceState = 0; Plugin.L.LogWarning($"[Battle] '{it.R.Name}': no game cursor above it, party offsets not added (info, not an error)"); }
                    return Vector2.zero;
                }
                it.CursorPtr = cur.Pointer;
                it.CursorIsHand = cur.name.StartsWith("common_cursor");
                bool ok = BattleCharScale.OffsetOfCursor(cur.Pointer, out var px, out int corps);
                int st = ok ? 10 + corps : 2;
                if (st != it.SourceState)
                {
                    it.SourceState = st;
                    Plugin.L.LogWarning($"[Battle] '{it.R.Name}': " + (ok ? $"on corpsIndex {corps}, party offset {px.x:0.#}, {px.y:0.#} px added" : "not on a party member, no party offset") + " (info, not an error)");
                }
                return ok ? px : Vector2.zero;
            }
            catch (Exception e)
            {
                if (it.SourceState != 3) { it.SourceState = 3; Plugin.L.LogWarning($"[Battle] '{it.R.Name}': reading the game cursor failed, party offsets not added: " + e.Message); }
                return Vector2.zero;
            }
        }

        /// <summary>
        /// Cursor on a party member: the game's place (+ party offset, spritesDown) is scaled around the member's feet
        /// by shown size / game size (HD scale x depthScale), px; the rule's moveRight/moveDown are added after.
        /// </summary>
        static Vector2 DepthOffsetOf(Item it, Vector3 pos, float right, float down)
        {
            try
            {
                if (it.CursorPtr == IntPtr.Zero || !BattleCharScale.FeetOfCursor(it.CursorPtr, out var feet) || (Mathf.Abs(feet.z - 1f) < 0.001f && Mathf.Abs(feet.w - 1f) < 0.001f)) return Vector2.zero;
                var canvas = it.G != null ? it.G.canvas : it.T.GetComponentInParent<Canvas>();
                if (canvas == null) return Vector2.zero;
                var root = canvas.rootCanvas;
                if (root.renderMode == RenderMode.WorldSpace) return Vector2.zero;
                var basePos = (!it.Applied || pos != it.SetPos) ? pos : it.BasePos;   // the game's place, not ours
                var p = it.T.parent;
                var world = (p != null ? p.TransformPoint(basePos) : basePos) + Offset(it, right - it.R.MoveRight, down - it.R.MoveDown);
                var local = root.transform.InverseTransformPoint(world);
                float ppu = Px.PerUnit(root);
                float x = local.x * ppu, y = -local.y * ppu;   // px from the screen centre, y down
                var d = new Vector2((feet.x - x) * (1f - feet.z), (feet.y - y) * (1f - feet.z));
                // The hand points at the visible figure's left edge (+ the rule's moveRight): the HD figures fill their
                // canvas differently, so the scaled place was a bit far on 512 px art, a bit close on 248 px (user).
                float edge = float.NaN, eu = -1f;
                if (it.CursorIsHand && BattleCharScale.EdgeOfCursor(it.CursorPtr, out var ex, out eu)) { edge = ex; d.x = ex - x; }
                float logKey = feet.z + (float.IsNaN(edge) ? 0f : Mathf.Round(edge) * 1000f);
                if (Mathf.Abs(logKey - it.LogDepthF) > 0.001f)
                {
                    it.LogDepthF = logKey;
                    Plugin.L.LogWarning($"[Battle] '{it.R.Name}': member shown x{feet.z:0.###} of the game's size (depth x{feet.w:0.###}), moved {d.x:0.#}, {d.y:0.#} px toward the feet (cursor {x:0}, {y:0}; feet {feet.x:0}, {feet.y:0} px from the centre)" +
                        (float.IsNaN(edge) ? "" : $"; figure's left edge {edge:0} px (texture column {eu:0.###})") + " (info, not an error)");
                }
                return d;
            }
            catch (Exception e)
            {
                if (it.LogDepthF != -2f) { it.LogDepthF = -2f; Plugin.L.LogWarning($"[Battle] '{it.R.Name}': depthScale cursor move failed: " + e.Message); }
                return Vector2.zero;
            }
        }

        static Vector3 Offset(Item it, float right, float down)
        {
            if (it.SR == null)
            {
                var canvas = it.G != null ? it.G.canvas : it.T.GetComponentInParent<Canvas>();
                if (canvas == null) return Vector3.zero;
                var root = canvas.rootCanvas;
                if (root.renderMode != RenderMode.WorldSpace)
                {
                    float unitsPerPx = Px.Upp(root);
                    return root.transform.TransformVector(new Vector3(right * unitsPerPx, -down * unitsPerPx, 0f));
                }
            }
            // World-space sprite: the battle camera shows 2 * orthographicSize units over the screen height.
            Camera cam = null;
            foreach (var c in Camera.allCameras) if (c.name == "CameraBattle") { cam = c; break; }
            if (cam == null || !cam.orthographic) return Vector3.zero;
            float worldPerPx = 2f * cam.orthographicSize / 1440f;
            return new Vector3(right * worldPerPx, -down * worldPerPx, 0f);
        }

        internal static void Apply()
        {
            if (items.Count == 0) return;
            var dead = new List<IntPtr>();
            foreach (var kv in items)
            {
                var it = kv.Value;
                try
                {
                    if (it.T == null || it.T.WasCollected || it.T.gameObject == null) { dead.Add(kv.Key); continue; }
                    if (!it.T.gameObject.activeInHierarchy) continue;
                    var r = it.R;
                    if (it.LG != null)
                    {
                        var canvas = it.LG.GetComponentInParent<Canvas>();
                        if (canvas == null) continue;
                        float upp = Px.Upp(canvas.rootCanvas);
                        float want = r.Spacing * upp;
                        if (r.ListPad != null || r.ListAlign.HasValue) ListLayout(it, upp);
                        if (r.Spacing >= 0 && Mathf.Abs(it.LG.spacing - want) > 0.01f)
                        {
                            if (float.IsNaN(it.BaseSpacing)) it.BaseSpacing = it.LG.spacing;
                            if (Rules.Log)
                            {
                                // Child widths and preferred widths show whether a column sizes itself to its text.
                                var kids = new List<string>();
                                for (int i = 0; i < it.T.childCount; i++)
                                {
                                    var ch = it.T.GetChild(i).GetComponent<RectTransform>();
                                    var le = ch?.GetComponent<LayoutElement>();
                                    if (ch != null) kids.Add($"{ch.name} w={ch.rect.width:0.#} layoutPrefW={(le == null ? "none" : le.preferredWidth.ToString("0.#"))}");
                                }
                                Plugin.L.LogInfo($"[Elements] '{r.Name}' {it.T.name}: spacing {it.LG.spacing} -> {want}; children: {string.Join(", ", kids)}");
                            }
                            it.LG.spacing = want;
                            LayoutRebuilder.MarkLayoutForRebuild(it.T.GetComponent<RectTransform>());
                        }
                        continue;
                    }
                    if (r.Width > 0) SetSize(it, false);
                    if (it.G != null && r.Placed) Place(it);
                    if (r.Widen != 0) Widen(it);
                    var pos = it.T.localPosition;
                    float down = r.MoveDown + (r.AddSpritesDown ? BattleShift.ActiveDown : 0f);
                    float right = r.MoveRight;
                    if (r.AddSpritesDown)
                    {
                        var po = PartyOffsetOf(it);
                        right += po.x; down += po.y;
                        var dpo = DepthOffsetOf(it, pos, right, down);
                        right += dpo.x; down += dpo.y;
                    }
                    if (!r.Placed && (down != 0 || right != 0 || it.Applied) && (!it.Applied || pos != it.SetPos || down != it.SetDown || right != it.SetRight))
                    {
                        // The game moved it: its position is the new base. Only our own value changed (spritesDown
                        // switched on/off, battle over): keep the old base.
                        if (!it.Applied || pos != it.SetPos) it.BasePos = pos;
                        var world = Offset(it, right, down);
                        var p = it.T.parent;
                        it.SetPos = it.BasePos + (p != null ? p.InverseTransformVector(world) : world);
                        it.SetDown = down; it.SetRight = right;
                        if (it.T.localPosition != it.SetPos) it.T.localPosition = it.SetPos;
                        if (Rules.Log && !it.Applied)
                            Plugin.L.LogInfo($"[Elements] '{r.Name}' {it.T.name}: local position {pos} -> {it.SetPos}");
                        it.Applied = true;
                    }
                    // The game's own colour (anything we did not set ourselves) tells greyed-out and cursor rows apart.
                    if (it.G != null && (!it.OwnColor || it.G.color != it.OwnSet)) { it.GameColor = it.G.color; it.GameColorKnown = true; it.OwnColor = false; }
                    bool sel = r.Selected != null && IsSelected(it);
                    bool grey = !sel && r.Greyed != null && IsGreyed(it);
                    var state = sel ? r.Selected : grey ? r.Greyed : null;
                    float mult = (r.Scale > 0 ? r.Scale : 1f) * (state != null && state.Scale > 0 ? state.Scale : 1f);
                    if (r.HasPad) Pad(it);
                    if ((r.Scale > 0 || (r.Selected != null && r.Selected.Scale > 0) || (r.Greyed != null && r.Greyed.Scale > 0)) &&
                        (!it.ScaleApplied || it.T.localScale != it.SetScale || it.T.localScale != it.BaseScale * mult))
                    {
                        // Base = the game's own scale (re-read whenever the game changes it), so the result never compounds.
                        if (!it.ScaleApplied || it.T.localScale != it.SetScale) it.BaseScale = it.T.localScale;
                        it.SetScale = it.BaseScale * mult;
                        it.T.localScale = it.SetScale;
                        if (Rules.Log && !it.ScaleApplied) Plugin.L.LogInfo($"[Elements] '{r.Name}' {it.T.name}: scale {it.BaseScale} -> {it.SetScale}");
                        it.ScaleApplied = true;
                    }
                    if (r.Height > 0) SetHeight(it);
                    if (r.Alpha >= 0) SetAlpha(it);
                    if (it.G == null) continue;
                    if (r.Image != null) SwapImage(it);
                    if (r.Radius > 0) RoundBox(it);
                    // Colour: the state's, else the rule's (a greyed text keeps the game's grey unless "greyed" sets one),
                    // else the game's own colour is put back.
                    Color? wantColor = state != null && state.Color.HasValue ? state.Color : grey ? null : r.Color;
                    if (wantColor.HasValue)
                    {
                        if (it.G.color != wantColor.Value) it.G.color = wantColor.Value;
                        it.OwnColor = true; it.OwnSet = wantColor.Value;
                    }
                    else if (it.OwnColor)
                    {
                        if (it.GameColorKnown) it.G.color = it.GameColor;
                        it.OwnColor = false;
                    }
                    float fontScale = state != null && state.FontScale > 0 ? state.FontScale : r.FontScale;
                    {
                        // Font: the state's, else the rule's. With fontScale and no font named, the text's own font is
                        // swapped for our copy of it (same face) so the size can be changed (see the KupoUI note below).
                        var txt = it.G.TryCast<Text>();
                        string fontName = state != null && state.Font != null ? state.Font : r.Font;
                        bool sizes = r.FontScale > 0 || (r.Selected != null && r.Selected.FontScale > 0) || (r.Greyed != null && r.Greyed.FontScale > 0);
                        Font f = null;
                        if (txt != null && !string.IsNullOrEmpty(fontName)) f = sizes ? Fonts.Own(fontName, true) : Fonts.Get(fontName);
                        else if (txt != null && sizes && txt.font != null) f = Fonts.OwnCopy(txt.font);
                        // A face of this font (fontStyle "SemiBold" etc.) counts as this font.
                        if (f != null && !Fonts.IsFamily(txt.font, f))
                        {
                            if (it.BaseFont == null) it.BaseFont = txt.font;
                            txt.font = f;
                            it.FontSet = f.Pointer;
                        }
                        else if (f == null && txt != null && string.IsNullOrEmpty(fontName) && it.BaseFont != null && txt.font != null && txt.font.Pointer == it.FontSet)
                        {
                            // A state's font is over: the text's original font again.
                            txt.font = it.BaseFont;
                            it.FontSet = IntPtr.Zero;
                        }
                    }
                    if (fontScale <= 0 && it.TextSaved)
                    {
                        // "selected.fontScale" only and the cursor left: the game's own size and fitting again.
                        var txt = it.G.TryCast<Text>();
                        if (txt != null && it.BaseSize > 0 && txt.fontSize != it.BaseSize)
                        {
                            txt.fontSize = it.BaseSize;
                            txt.resizeTextForBestFit = it.BaseBestFit;
                            txt.horizontalOverflow = it.BaseHOverflow;
                            txt.verticalOverflow = it.BaseVOverflow;
                        }
                    }
                    if (fontScale > 0)
                    {
                        var txt = it.G.TryCast<Text>();
                        if (txt != null)
                        {
                            if (!it.TextSaved)
                            {
                                it.TextSaved = true;
                                it.BaseSize = txt.fontSize;
                                it.BaseBestFit = txt.resizeTextForBestFit;
                                it.BaseHOverflow = txt.horizontalOverflow;
                                it.BaseVOverflow = txt.verticalOverflow;
                                if (Rules.Log) Plugin.L.LogInfo($"[Elements] '{r.Name}' {it.T.name}: font size {it.BaseSize} x {fontScale} (best fit was {it.BaseBestFit})");
                            }
                            // Best fit would shrink the larger text back into its box, so draw it unclipped instead.
                            int want = Mathf.Max(1, Mathf.RoundToInt(it.BaseSize * fontScale));
                            if (txt.resizeTextForBestFit) txt.resizeTextForBestFit = false;
                            if (txt.horizontalOverflow != HorizontalWrapMode.Overflow) txt.horizontalOverflow = HorizontalWrapMode.Overflow;
                            if (txt.verticalOverflow != VerticalWrapMode.Overflow) txt.verticalOverflow = VerticalWrapMode.Overflow;
                            if (txt.fontSize != want) txt.fontSize = want;
                            // KupoUI.PR patches the Text fontSize / resizeTextForBestFit setters (FontSizeSetterPrefix,
                            // ResizeTextForBestFitSetterPrefix: strings in KupoUI.PR.dll) and keeps its own size for texts in
                            // its fonts (user 2026-11-06: fontScale ignored with a KupoUI font, works with our own) [inferred].
                            // Our own copy of the font (set above) avoids that; if the size still does not stick, say so once.
                            if (!it.SizeHeldLogged && (txt.fontSize != want || txt.resizeTextForBestFit))
                            {
                                it.SizeHeldLogged = true;
                                Plugin.L.LogWarning($"[Elements] '{r.Name}' {it.T.name}: font size {want} did not stick (size {txt.fontSize}, best fit {txt.resizeTextForBestFit}, font '{txt.font?.name}'); another mod may own this text's size");
                            }
                        }
                    }
                    if (r.Style != null)
                    {
                        var txt = it.G.TryCast<Text>();
                        if (txt != null) TextStyler.Apply(txt, r.Style, it.StyleState ??= new TextStyler.State());
                    }
                    if (r.OnTop || r.BackdropColor.HasValue) Layer(it);
                    if (r.FontSize > 0 && r.FontScale <= 0)
                    {
                        var txt = it.G.TryCast<Text>();
                        var canvas = it.G.canvas;
                        if (txt != null && canvas != null)
                        {
                            float unitsPerPx = Px.Upp(canvas.rootCanvas);
                            int want = Mathf.Max(1, Mathf.RoundToInt(r.FontSize * unitsPerPx));
                            if (txt.fontSize != want)
                            {
                                if (it.BaseSize < 0) it.BaseSize = txt.fontSize;
                                txt.fontSize = want;
                            }
                        }
                    }
                    if (r.FontSizeMax > 0)
                    {
                        // Shrink-to-fit texts never exceed resizeTextMaxSize; re-applied each frame in case the font swap resets it.
                        var txt = it.G.TryCast<Text>();
                        var canvas = it.G.canvas;
                        if (txt != null && canvas != null)
                        {
                            float unitsPerPx = Px.Upp(canvas.rootCanvas);
                            int want = Mathf.Max(1, Mathf.RoundToInt(r.FontSizeMax * unitsPerPx));
                            if (txt.resizeTextMaxSize != want)
                            {
                                if (it.BaseMaxSize < 0) it.BaseMaxSize = txt.resizeTextMaxSize;
                                else if (Rules.Log && !it.MaxResetLogged) { it.MaxResetLogged = true; Plugin.L.LogInfo($"[Elements] '{r.Name}' {it.T.name}: max size was reset to {txt.resizeTextMaxSize} by something else; setting {want} again"); }
                                if (Rules.Log && !it.MaxLogged) { it.MaxLogged = true; Plugin.L.LogInfo($"[Elements] '{r.Name}' {it.T.name}: max size {txt.resizeTextMaxSize} -> {want} (best fit {txt.resizeTextForBestFit}, min {txt.resizeTextMinSize})"); }
                                txt.resizeTextMaxSize = want;
                            }
                        }
                    }
                    if (r.FontSizeMin > 0)
                    {
                        // Shrink-to-fit lower limit; re-applied each frame like the max (the font swap may reset it).
                        var txt = it.G.TryCast<Text>();
                        var canvas = it.G.canvas;
                        if (txt != null && canvas != null)
                        {
                            float unitsPerPx = Px.Upp(canvas.rootCanvas);
                            int want = Mathf.Max(1, Mathf.RoundToInt(r.FontSizeMin * unitsPerPx));
                            if (txt.resizeTextMinSize != want)
                            {
                                if (it.BaseMinSize < 0) it.BaseMinSize = txt.resizeTextMinSize;
                                txt.resizeTextMinSize = want;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    dead.Add(kv.Key);
                    if (Rules.Log) Plugin.L.LogInfo($"[Elements] dropped '{it.R.Name}': {e.Message}");
                }
            }
            foreach (var k in dead) items.Remove(k);
            UpdateMerged();
        }

        /// <summary>Corners of a RectTransform in root-canvas units: (minX, minY, maxX, maxY).</summary>
        static Vector4 Bounds(RectTransform rt, Transform root)
        {
            var c = new Il2CppStructArray<Vector3>(4);
            rt.GetWorldCorners(c);
            Vector3 a = root.InverseTransformPoint(c[0]), b = root.InverseTransformPoint(c[2]);
            return new Vector4(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }

        /// <summary>
        /// Finds the Below/CenterOver objects: the nearest ancestor (up to 8 levels) whose descendants include an
        /// object for every listed path (path ends with it, "(Clone)" ignored).
        /// </summary>
        static Dictionary<string, Transform> FindRefs(Item it)
        {
            var wanted = new List<string>(it.R.Below);
            wanted.AddRange(it.R.CenterOver);
            Transform scope = it.T.parent;
            for (int up = 0; up < 8 && scope != null; up++, scope = scope.parent)
            {
                var found = new Dictionary<string, Transform>();
                foreach (var rt in scope.GetComponentsInChildren<RectTransform>(true))
                {
                    string p = NoClone(PathOf(rt));
                    foreach (var w in wanted)
                        if (!found.ContainsKey(w) && p.EndsWith(NoClone(w))) found[w] = rt;
                }
                if (found.Count == wanted.Count) return found;
            }
            return null;
        }

        /// <summary>Width, then position from Below/CenterOver (plus moveDown/moveRight), recomputed each frame.</summary>
        /// <summary>
        /// Adds Widen px on the right, keeping the left edge. A parent layout resets the width on rebuild; the width it
        /// sets is taken as the new base, so the result is always base + Widen.
        /// </summary>
        static void Widen(Item it)
        {
            var rt = it.T.GetComponent<RectTransform>();
            var canvas = it.T.GetComponentInParent<Canvas>();
            if (rt == null || canvas == null) return;
            var root = canvas.rootCanvas.transform;
            float upp = Px.Upp(canvas.rootCanvas);
            float scale = rt.lossyScale.x / Mathf.Max(0.0001f, root.lossyScale.x);
            float extra = it.R.Widen * upp / Mathf.Max(0.0001f, scale);
            float w = rt.rect.width;
            if (it.WidenSet >= 0 && Mathf.Abs(w - it.WidenSet) < 0.01f) return; // still ours
            if (!it.SizeSaved) { it.SizeSaved = true; it.BaseSizeDelta = rt.sizeDelta; it.PlaceBasePos = it.T.localPosition; it.PlaceSaved = true; }
            float left = Bounds(rt, root).x;
            it.WidenSet = Mathf.Max(1f, w + extra);
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, it.WidenSet);
            float dx = left - Bounds(rt, root).x; // root-canvas units
            var p = it.T.parent;
            var world = root.TransformVector(new Vector3(dx, 0f, 0f));
            it.T.localPosition += p != null ? p.InverseTransformVector(world) : world;
            // This runs after the UI's own redraw, so the text was already laid out for the old width (user: "Magic
            // Defense" showed only "Magic"); lay it out again for the new width now.
            if (it.G != null) it.G.Rebuild(CanvasUpdate.PreRender);
            // Resizing a layout child makes the layout rebuild next frame, which resets it, so this repeats each frame; log once.
            if (Rules.Log && !it.WidenLogged) { it.WidenLogged = true; Plugin.L.LogInfo($"[Elements] '{it.R.Name}' {it.T.name}: width {w:0.#} -> {it.WidenSet:0.#} (left edge kept)"); }
        }

        /// <summary>
        /// "height": absolute height in 1440p px for any object. Inside a layout group the group sets the size, so the
        /// object's LayoutElement (added if missing, removed on restore) gets min and preferred height; else the
        /// RectTransform height is set (anchors kept). Written only when it differs, so the game's own changes win
        /// for at most one frame.
        /// </summary>
        static string Tex(Texture t) => t == null ? "null" : $"'{t.name}' {t.width}x{t.height} filter={t.filterMode}";

        static string Mat(Material m)
        {
            if (m == null) return "material null";
            var parts = new List<string> { $"material '{m.name}' shader '{m.shader?.name}'" };
            try { var kw = m.shaderKeywords; if (kw != null && kw.Length > 0) parts.Add("keywords " + string.Join(",", kw)); } catch { }
            var sh = m.shader;
            if (sh != null)
                for (int i = 0; i < sh.GetPropertyCount(); i++)
                {
                    string n = sh.GetPropertyName(i);
                    var ty = sh.GetPropertyType(i);
                    try
                    {
                        string v = ty == UnityEngine.Rendering.ShaderPropertyType.Texture ? Tex(m.GetTexture(n)) + $" st={m.GetVector(n + "_ST")}"
                                 : ty == UnityEngine.Rendering.ShaderPropertyType.Vector || ty == UnityEngine.Rendering.ShaderPropertyType.Color ? m.GetVector(n).ToString()
                                 : m.GetFloat(n).ToString("0.####");
                        parts.Add($"{n}={v}");
                    }
                    catch (Exception e) { parts.Add($"{n}=? {e.Message}"); }
                }
            return string.Join(" | ", parts);
        }

        /// <summary>F8 dump: every active renderer that is not a SpriteRenderer (battle characters are not), with its
        /// world size, approximate height in 1440p px for CameraBattle, and texture.</summary>
        internal static List<string> OtherRenderers()
        {
            var r = new List<string>();
            Camera battle = null;
            foreach (var c in Camera.allCameras) if (c.name == "CameraBattle") { battle = c; break; }
            float pxPerUnit = battle != null && battle.orthographic ? 1440f / (2f * battle.orthographicSize) : 0f;
            var rends = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Renderer>());
            if (rends == null) return r;
            foreach (var o in rends)
            {
                var rd = o?.TryCast<Renderer>();
                if (rd == null || !rd.enabled || !rd.gameObject.activeInHierarchy || rd.TryCast<SpriteRenderer>() != null) continue;
                var m = rd.sharedMaterial;
                var t = m == null ? null : m.mainTexture;
                var b = rd.bounds.size;
                string tex = t == null ? "tex=null" : $"tex='{t.name}' {t.width}x{t.height}";
                r.Add($"'{PathOf(rd.transform)}' {rd.GetIl2CppType().Name} layer={rd.gameObject.layer} pos={rd.transform.position} scale={rd.transform.lossyScale} bounds={b.x:0.##}x{b.y:0.##}" +
                      (pxPerUnit > 0 ? $" (~{b.y * pxPerUnit:0} px tall)" : "") + $" material='{m?.name}' {tex} sortLayer={rd.sortingLayerName} order={rd.sortingOrder} queue={(m != null ? m.renderQueue : -1)}");
                if (r.Count >= 150) { r.Add("(more not listed)"); break; }
            }
            return r;
        }

        /// <summary>F8 dump: what draws the battle background (controller render textures, raster mesh, materials).</summary>
        internal static List<string> BgInfo()
        {
            var r = new List<string>();
            var cs = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Last.Battle.BattleBackgroundController>());
            if (cs != null)
                foreach (var o in cs)
                {
                    var c = o?.TryCast<Last.Battle.BattleBackgroundController>();
                    if (c == null) continue;
                    r.Add($"controller '{PathOf(c.transform)}' enabled={c.enabled} originalRT={Tex(c.originalRenderTexture)} prevRT={Tex(c.prevRenderTexture)}");
                    try { r.Add("controller " + Mat(c.GetMaterial())); } catch (Exception e) { r.Add("controller material: " + e.Message); }
                }
            var ras = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Last.Battle.RasterAnimation>());
            if (ras != null)
                foreach (var o in ras)
                {
                    var ra = o?.TryCast<Last.Battle.RasterAnimation>();
                    if (ra == null) continue;
                    var mr = ra.MeshRenderer;
                    var mesh = ra.mesh;
                    r.Add($"raster '{PathOf(ra.transform)}' enabled={ra.enabled} active={ra.gameObject.activeInHierarchy} width={ra.width} height={ra.height} status={ra.rasterStatus} " +
                          $"mesh={(mesh == null ? "null" : mesh.vertexCount + " verts bounds " + mesh.bounds.size)} renderer={(mr == null ? "null" : "enabled=" + mr.enabled + " pos=" + mr.transform.position + " scale=" + mr.transform.lossyScale)}");
                    try { r.Add("raster " + Mat(ra.material)); } catch (Exception e) { r.Add("raster material: " + e.Message); }
                }
            // Anything else that draws a render texture.
            var rends = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Renderer>());
            if (rends != null)
                foreach (var o in rends)
                {
                    var rd = o?.TryCast<Renderer>();
                    if (rd == null || !rd.enabled || !rd.gameObject.activeInHierarchy) continue;
                    var m = rd.sharedMaterial;
                    var t = m == null ? null : m.mainTexture;
                    if (t == null || t.TryCast<RenderTexture>() == null) continue;
                    r.Add($"renderer '{PathOf(rd.transform)}' {rd.GetIl2CppType().Name} layer={rd.gameObject.layer} {Mat(m)}");
                }
            return r;
        }

        /// <summary>F8 dump: a sprite's texture size, mip and streaming state (to find why a texture shows below its size).</summary>
        internal static string TexInfo(Sprite sp)
        {
            try
            {
                if (sp == null || sp.texture == null) return "";
                var t = sp.texture;
                string st = t.streamingMipmaps ? $" streaming loaded={t.loadedMipmapLevel} desired={t.desiredMipmapLevel} requested={t.requestedMipmapLevel}" : "";
                return $" tex={t.name} {t.width}x{t.height} mips={t.mipmapCount} filter={t.filterMode} bias={t.mipMapBias:0.##}{st} rect={sp.rect.width:0}x{sp.rect.height:0} ppu={sp.pixelsPerUnit:0.##}";
            }
            catch (Exception e) { return " tex=? " + e.Message; }
        }

        static void SetHeight(Item it) => SetSize(it, true);

        /// <summary>"listPadding" / "listAlign" on a row or column layout (LayoutGroup.padding, childAlignment).</summary>
        static void ListLayout(Item it, float upp)
        {
            var lg = it.LG;
            bool changed = false;
            if (it.R.ListPad != null)
            {
                var cur = lg.padding;
                var w = it.R.ListPad;
                int l = Mathf.RoundToInt(w[0] * upp), rr = Mathf.RoundToInt(w[1] * upp), t = Mathf.RoundToInt(w[2] * upp), b = Mathf.RoundToInt(w[3] * upp);
                if (cur == null || cur.left != l || cur.right != rr || cur.top != t || cur.bottom != b)
                {
                    if (it.BaseListPad == null) it.BaseListPad = cur == null ? new int[4] : new[] { cur.left, cur.right, cur.top, cur.bottom };
                    lg.padding = new RectOffset(l, rr, t, b);
                    changed = true;
                }
            }
            if (it.R.ListAlign.HasValue && lg.childAlignment != it.R.ListAlign.Value)
            {
                if (!it.BaseListAlign.HasValue) it.BaseListAlign = lg.childAlignment;
                lg.childAlignment = it.R.ListAlign.Value;
                changed = true;
            }
            if (!changed) return;
            LayoutRebuilder.MarkLayoutForRebuild(it.T.GetComponent<RectTransform>());
            if (Rules.Log) Plugin.L.LogInfo($"[Elements] '{it.R.Name}' {it.T.name}: list padding {(lg.padding == null ? "-" : lg.padding.left + "/" + lg.padding.right + "/" + lg.padding.top + "/" + lg.padding.bottom)} units, align {lg.childAlignment}");
        }

        static readonly Dictionary<string, (long Stamp, Sprite S, float Next)> swapSprites = new Dictionary<string, (long, Sprite, float)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// "image": PNG (path relative to MenuLayout.json's folder) loaded once into a sprite; reloaded when the file
        /// changes (checked at most every 2 s). null = missing or unreadable (warned once per file version).
        /// </summary>
        static Sprite SwapSprite(string rel)
        {
            string dir = string.IsNullOrEmpty(Plugin.ConfigPath) ? null : Path.GetDirectoryName(Plugin.ConfigPath);
            if (dir == null) return null;
            string full = Path.GetFullPath(Path.Combine(dir, rel));
            bool known = swapSprites.TryGetValue(full, out var c);
            if (known && Time.unscaledTime < c.Next) return c.S;
            long stamp = File.Exists(full) ? File.GetLastWriteTimeUtc(full).Ticks : 0;
            if (known && c.Stamp == stamp) { swapSprites[full] = (stamp, c.S, Time.unscaledTime + 2f); return c.S; } // unchanged (or still missing: warned already)
            Sprite sp = null;
            try
            {
                if (stamp == 0) Plugin.L.LogWarning("[Elements] image not found: " + full);
                else
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(full))) Plugin.L.LogWarning("[Elements] not a readable PNG: " + full);
                    else
                    {
                        tex.filterMode = FilterMode.Bilinear;
                        tex.wrapMode = TextureWrapMode.Clamp;
                        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                        sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
                        sp.name = "spritemod_" + Path.GetFileNameWithoutExtension(full);
                        if (Rules.Log) Plugin.L.LogInfo($"[Elements] image loaded: {full} ({tex.width}x{tex.height})");
                    }
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("[Elements] could not load image " + full + ": " + e.Message); }
            swapSprites[full] = (stamp, sp, Time.unscaledTime + 2f);
            return sp;
        }

        /// <summary>
        /// "image": puts the PNG's sprite on a UI Image; stretched to the object's box (size unchanged). If the game
        /// sets another sprite later, that one becomes the base to restore and ours goes back on.
        /// </summary>
        static void SwapImage(Item it)
        {
            var img = it.G.TryCast<Image>();
            if (img == null)
            {
                if (!it.SwapWarned) { it.SwapWarned = true; Plugin.L.LogWarning($"[Elements] '{it.R.Name}' {it.T.name}: 'image' works on images only (this is a {it.G.GetIl2CppType().Name})"); }
                return;
            }
            var sp = SwapSprite(it.R.Image);
            if (sp == null) return;
            var cur = img.sprite;
            if (cur != null && cur.Pointer == sp.Pointer) return;
            if (cur == null || it.SwapSet == null || cur.Pointer != it.SwapSet.Pointer) it.SwapBase = cur; // the game's sprite (or our older version of the file)
            img.sprite = sp;
            if (Rules.Log && it.SwapSet == null) Plugin.L.LogInfo($"[Elements] '{it.R.Name}' {it.T.name}: sprite {it.SwapBase?.name} -> {sp.name}");
            it.SwapSet = sp;
        }

        /// <summary>
        /// "alpha"/"hide": a CanvasGroup on the object (added if missing, removed on restore). If the game has its own
        /// group (e.g. a fade), the game's alpha is the base and ours multiplies it; a change by the game becomes the
        /// new base, like "scale".
        /// </summary>
        static void SetAlpha(Item it)
        {
            if (it.CG == null || it.CG.WasCollected)
            {
                it.CG = it.T.GetComponent<CanvasGroup>();
                if (it.CG == null) { it.CG = it.T.gameObject.AddComponent<CanvasGroup>(); it.AddedCG = true; }
                it.AlphaApplied = false;
            }
            var cg = it.CG;
            if (!it.AlphaApplied || Mathf.Abs(cg.alpha - it.SetAlpha) > 0.001f)
            {
                if (!it.AlphaApplied) it.BaseBlocks = cg.blocksRaycasts;
                it.BaseAlpha = cg.alpha;
                it.SetAlpha = it.BaseAlpha * it.R.Alpha;
                cg.alpha = it.SetAlpha;
                if (it.R.Hide) cg.blocksRaycasts = false;
                if (Rules.Log && !it.AlphaApplied)
                    Plugin.L.LogInfo($"[Elements] '{it.R.Name}' {it.T.name}: {(it.R.Hide ? "hidden" : "alpha " + it.BaseAlpha.ToString("0.##") + " -> " + it.SetAlpha.ToString("0.##"))}{(it.AddedCG ? "" : " (game's own CanvasGroup)")}");
                it.AlphaApplied = true;
            }
        }

        /// <summary>"height" (vertical) or "width": same handling on either axis. A width keeps the centre (pivot).</summary>
        static void SetSize(Item it, bool vertical)
        {
            var rt = it.T.GetComponent<RectTransform>();
            if (rt == null) return;
            var canvas = it.T.GetComponentInParent<Canvas>();
            if (canvas == null) return;
            var root = canvas.rootCanvas.transform;
            float upp = Px.Upp(canvas.rootCanvas);
            float scale = vertical ? rt.lossyScale.y / Mathf.Max(0.0001f, root.lossyScale.y) : rt.lossyScale.x / Mathf.Max(0.0001f, root.lossyScale.x);
            float px = vertical ? it.R.Height : it.R.Width;
            float want = px * upp / Mathf.Max(0.0001f, scale);
            string what = vertical ? "height" : "width";
            bool inLayout = it.T.parent != null && it.T.parent.GetComponent<LayoutGroup>() != null;
            if (inLayout)
            {
                var le = it.T.GetComponent<LayoutElement>();
                if (le == null)
                {
                    le = it.T.gameObject.AddComponent<LayoutElement>();
                    it.AddedLE = le;
                }
                if (vertical && !it.HeightSaved) { it.HeightSaved = true; it.BaseMinH = le.minHeight; it.BasePrefH = le.preferredHeight; }
                if (!vertical && !it.WidthSaved) { it.WidthSaved = true; it.BaseMinW = le.minWidth; it.BasePrefW = le.preferredWidth; }
                float pref = vertical ? le.preferredHeight : le.preferredWidth, min = vertical ? le.minHeight : le.minWidth;
                if (Mathf.Abs(pref - want) > 0.01f || Mathf.Abs(min - want) > 0.01f)
                {
                    if (vertical) { le.minHeight = want; le.preferredHeight = want; }
                    else { le.minWidth = want; le.preferredWidth = want; }
                    LayoutRebuilder.MarkLayoutForRebuild(it.T.parent.GetComponent<RectTransform>());
                    if (Rules.Log) Plugin.L.LogInfo($"[Elements] '{it.R.Name}' {it.T.name}: layout {what} -> {want:0.#} units ({px} px at 1440p)");
                }
            }
            else if (Mathf.Abs((vertical ? rt.rect.height : rt.rect.width) - want) > 0.01f)
            {
                if (!it.SizeSaved) { it.SizeSaved = true; it.BaseSizeDelta = rt.sizeDelta; }
                rt.SetSizeWithCurrentAnchors(vertical ? RectTransform.Axis.Vertical : RectTransform.Axis.Horizontal, want);
                // A text was already laid out for the old size this frame; lay it out again.
                if (it.G != null) it.G.Rebuild(CanvasUpdate.PreRender);
                if (Rules.Log && !(vertical ? it.HeightLogged : it.WidthLogged))
                    Plugin.L.LogInfo($"[Elements] '{it.R.Name}' {it.T.name}: {what} -> {want:0.#} units ({px} px at 1440p)");
                if (vertical) it.HeightLogged = true; else it.WidthLogged = true;
            }
        }

        /// <summary>
        /// "radius": gives a plain box (Image without a sprite, e.g. the battle list's focus_image) rounded corners,
        /// with the same 9-sliced sprite as backdrop.radius. Images that already have a game sprite are left alone.
        /// </summary>
        static void RoundBox(Item it)
        {
            var img = it.G.TryCast<Image>();
            if (img == null) return;
            var s = RoundSprite();
            if (s == null) return;
            if (img.sprite != null && img.sprite.Pointer != s.Pointer) return; // a game sprite: not ours to replace
            var canvas = img.canvas;
            if (canvas == null) return;
            var root = canvas.rootCanvas;
            float upp = Px.Upp(root);
            float rel = it.T.lossyScale.x / Mathf.Max(0.0001f, root.transform.lossyScale.x);
            if (!it.Rounded)
            {
                it.Rounded = true;
                it.BasePpuMult = img.pixelsPerUnitMultiplier;
                if (Rules.Log) Plugin.L.LogInfo($"[Elements] '{it.R.Name}' {it.T.name}: rounded corners {it.R.Radius} px");
            }
            Round(img, it.R.Radius, upp / Mathf.Max(0.0001f, rel));
        }

        /// <summary>
        /// "selected": true while the list row this object sits in has the cursor. The row is the nearest ancestor (up
        /// to 6 levels, the object itself included) with one of the game's row views that expose isFocus [verified,
        /// interop dump 2026-11-06]: item lists (Items, equip choices), command menus, battle/menu command rows,
        /// character select, abilities, names, title commands. Looked up once per object; logged when none is found.
        /// </summary>
        static bool IsSelected(Item it)
        {
            if (!it.FocusSearched)
            {
                it.FocusSearched = true;
                it.Focus = FocusOf(it.T);
                if (it.Focus == null && Rules.Log)
                    Plugin.L.LogInfo($"[Elements] '{it.R.Name}': '{PathOf(it.T)}' has no known row focus flag; 'selected' = the game's cursor yellow");
            }
            if (it.Focus != null)
            {
                try { return it.Focus(); }
                catch { it.Focus = null; }
            }
            // Entries with their own cursor anchor (job list: job_image/root/cursor_root [verified, dump 2026-11-05]):
            // the game's hand (Last.UI.Cursor, which has SetSource(GameObject) and a PositionConstraint [verified,
            // interop]) follows a source object; the entry is selected while that source sits inside it [inferred].
            if (!it.UnitSearched)
            {
                it.UnitSearched = true;
                it.CursorUnit = CursorUnitOf(it.T);
                if (Rules.Log) Plugin.L.LogInfo($"[Elements] '{it.R.Name}': cursor unit {(it.CursorUnit != null ? "'" + it.CursorUnit.name + "'" : "none")}");
            }
            if (it.CursorUnit != null)
            {
                CollectCursorSources();
                foreach (var s in cursorSources)
                    if (s != null && s.IsChildOf(it.CursorUnit)) return true;
                // no hit: fall through to the yellow check, so lists that already worked by colour keep working
            }
            // Rows without a focus flag (magic list, battle lists...): the game colours the cursor row's text #FFD800
            // [verified, F8 dumps 2026-11-06: 'Use', 'Cure', 'Attack', 'Galuf', item list ':' and count].
            return it.GameColorKnown && Near(it.GameColor, GameYellow);
        }

        static int cursorFrame = -1;
        static bool cursorWarned;
        static readonly List<Transform> cursorSources = new List<Transform>();

        /// <summary>Once per frame: the objects the game's active cursors follow (PositionConstraint source 0).</summary>
        static void CollectCursorSources()
        {
            if (cursorFrame == Time.frameCount) return;
            cursorFrame = Time.frameCount;
            cursorSources.Clear();
            try
            {
                var list = Last.UI.Cursor.list;
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    if (c == null || !c.isActiveAndEnabled) continue;
                    var pc = c.GetComponent<UnityEngine.Animations.PositionConstraint>();
                    if (pc == null || !pc.enabled || pc.sourceCount == 0) continue;
                    var t = pc.GetSource(0).sourceTransform;
                    if (t != null && t.gameObject.activeInHierarchy) cursorSources.Add(t);
                }
            }
            catch (Exception e)
            {
                if (!cursorWarned) { cursorWarned = true; Plugin.L.LogWarning("[Elements] reading the game's cursors failed; 'selected' falls back to the cursor yellow: " + e.Message); }
            }
        }

        /// <summary>Nearest ancestor (1-5 levels up) that holds a child named 'cursor_root': one entry of a list.</summary>
        static Transform CursorUnitOf(Transform t)
        {
            var a = t.parent;
            for (int d = 0; d < 5 && a != null; d++, a = a.parent)
            {
                var all = a.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < all.Length; i++)
                    if (all[i].name == "cursor_root") return a;
            }
            return null;
        }

        // Colours the game gives texts [verified, F8 dumps 2026-11-06]: the cursor row and greyed-out (unusable) entries
        // (Tent outside save points, spells without MP, job commands not set).
        static readonly Color GameYellow = new Color(1f, 216f / 255f, 0f, 1f);   // #FFD800
        static readonly Color GameGrey = new Color(0.6f, 0.6f, 0.6f, 1f);        // #999999

        static bool Near(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) < 0.03f && Mathf.Abs(a.g - b.g) < 0.03f && Mathf.Abs(a.b - b.b) < 0.03f;

        /// <summary>"greyed": the game shows the text in its unusable grey (#999999).</summary>
        static bool IsGreyed(Item it) => it.GameColorKnown && Near(it.GameColor, GameGrey);

        /// <summary>
        /// "padding": grows the box by Pad px (1440p) on each side, the same on both sides whatever its pivot (edges
        /// moved through offsetMin/offsetMax). Re-applied when the game resizes the box; put back by RestoreAll.
        /// </summary>
        static void Pad(Item it)
        {
            var rt = it.T.GetComponent<RectTransform>();
            if (rt == null) return;
            var canvas = it.T.GetComponentInParent<Canvas>();
            if (canvas == null) return;
            var root = canvas.rootCanvas.transform;
            float upp = Px.Upp(canvas.rootCanvas);
            float sx = rt.lossyScale.x / Mathf.Max(0.0001f, root.lossyScale.x);
            float sy = rt.lossyScale.y / Mathf.Max(0.0001f, root.lossyScale.y);
            var d = new Vector2(it.R.Pad.x * upp / Mathf.Max(0.0001f, Mathf.Abs(sx)), it.R.Pad.y * upp / Mathf.Max(0.0001f, Mathf.Abs(sy)));
            if (it.PadSaved && rt.offsetMin == it.PadSetMin && rt.offsetMax == it.PadSetMax) return; // still ours
            if (!it.PadSaved || rt.offsetMin != it.PadSetMin || rt.offsetMax != it.PadSetMax)
            {
                it.PadBaseMin = rt.offsetMin; it.PadBaseMax = rt.offsetMax;
                if (Rules.Log && !it.PadSaved) Plugin.L.LogInfo($"[Elements] '{it.R.Name}' {it.T.name}: padding {it.R.Pad.x}x{it.R.Pad.y} px");
                it.PadSaved = true;
            }
            it.PadSetMin = it.PadBaseMin - d;
            it.PadSetMax = it.PadBaseMax + d;
            rt.offsetMin = it.PadSetMin;
            rt.offsetMax = it.PadSetMax;
            it.PadSetMin = rt.offsetMin; it.PadSetMax = rt.offsetMax; // as Unity stored them (float rounding)
        }

        static Func<bool> FocusOf(Transform t)
        {
            for (int up = 0; up < 7 && t != null; up++, t = t.parent)
            {
                var a = t.GetComponent<Last.UI.ItemListContentView>(); if (a != null) return () => a.isFocus;
                var b = t.GetComponent<Last.UI.CommandMenuContentView>(); if (b != null) return () => b.isFocus;
                var c = t.GetComponent<Last.UI.KeyInput.ItemCommandContentView>(); if (c != null) return () => c.isFocus;
                var d = t.GetComponent<Last.UI.KeyInput.CharacterSelectCommandContentView>(); if (d != null) return () => d.isFocus;
                var e = t.GetComponent<Serial.FF5.UI.KeyInput.AbilityCommandContentView>(); if (e != null) return () => e.isFocus;
                var f = t.GetComponent<Last.UI.KeyInput.NameContentView>(); if (f != null) return () => f.isFocus;
                var g = t.GetComponent<Last.UI.KeyInput.TitleCommandContentView>(); if (g != null) return () => g.isFocus;
            }
            return null;
        }

        /// <summary>For the F8 dump: object -> names of the rules that matched it.</summary>
        internal static Dictionary<IntPtr, string> RuleMap()
        {
            var map = new Dictionary<IntPtr, string>();
            foreach (var it in items.Values)
            {
                try
                {
                    if (it.T == null || it.T.WasCollected) continue;
                    var k = it.T.Pointer;
                    map[k] = map.TryGetValue(k, out var s) ? s + ", " + it.R.Name : it.R.Name;
                }
                catch { }
            }
            return map;
        }

        static void Place(Item it)
        {
            var r = it.R;
            var rt = it.T.GetComponent<RectTransform>();
            var canvas = it.G.canvas;
            if (rt == null || canvas == null) return;
            var root = canvas.rootCanvas.transform;
            float upp = Px.Upp(canvas.rootCanvas);
            if (!r.Placed) return;
            if (it.Refs == null)
            {
                it.Refs = FindRefs(it);
                if (it.Refs == null)
                {
                    it.Refs = new Dictionary<string, Transform>();
                    Plugin.L.LogWarning($"[Elements] '{r.Name}': could not find every 'below'/'centerOver' object near '{PathOf(it.T)}'; not placed");
                }
            }
            if (it.Refs.Count == 0) return;
            var b = Bounds(rt, root);
            float dx = 0, dy = 0;
            if (r.Below.Count > 0)
            {
                float bottom = float.MaxValue;
                foreach (var w in r.Below) if (it.Refs.TryGetValue(w, out var t) && t != null) bottom = Mathf.Min(bottom, Bounds(t.GetComponent<RectTransform>(), root).y);
                dy = (bottom - (r.Gap + r.MoveDown) * upp) - b.w;
            }
            else dy = 0;
            if (r.CenterOver.Count > 0)
            {
                float minX = float.MaxValue, maxX = float.MinValue;
                foreach (var w in r.CenterOver)
                    if (it.Refs.TryGetValue(w, out var t) && t != null)
                    {
                        var tb = Bounds(t.GetComponent<RectTransform>(), root);
                        minX = Mathf.Min(minX, tb.x); maxX = Mathf.Max(maxX, tb.z);
                    }
                dx = ((minX + maxX) / 2f + r.MoveRight * upp) - (b.x + b.z) / 2f;
            }
            if (Mathf.Abs(dx) < 0.01f && Mathf.Abs(dy) < 0.01f) return;
            if (!it.PlaceSaved) { it.PlaceSaved = true; it.PlaceBasePos = it.T.localPosition; }
            var world = root.TransformVector(new Vector3(dx, dy, 0f));
            var p = it.T.parent;
            it.T.localPosition += p != null ? p.InverseTransformVector(world) : world;
            if (Rules.Log)
            {
                var nb = Bounds(rt, root);
                float px = 1f / upp;
                Plugin.L.LogInfo($"[Elements] '{r.Name}' {it.T.name}: placed at x {nb.x * px + 1280:0}-{nb.z * px + 1280:0} y {720 - nb.w * px:0}-{720 - nb.y * px:0} (1440p px)");
            }
        }

        internal static void RestoreAll()
        {
            foreach (var it in items.Values) RestoreItem(it, false);
            items.Clear();
            watch.Clear();
            foreach (var m in merged.Values) DestroyMerged(m);
            merged.Clear();
        }

        /// <summary>Puts back what this item's rule changed. single: also leaves its merged backdrop box.</summary>
        static void RestoreItem(Item it, bool single = true)
        {
            {
                try
                {
                    if (single)
                    {
                        List<IntPtr> empty = null;
                        foreach (var kv in merged)
                            if (kv.Value.Members.Remove(it) && kv.Value.Members.Count == 0) (empty ??= new List<IntPtr>()).Add(kv.Key);
                        if (empty != null) foreach (var k in empty) { DestroyMerged(merged[k]); merged.Remove(k); }
                    }
                    if (it.T == null || it.T.WasCollected) return;
                    if (it.Applied && it.T.localPosition == it.SetPos) it.T.localPosition = it.BasePos;
                    if (it.ScaleApplied && it.T.localScale == it.SetScale) it.T.localScale = it.BaseScale;
                    if (it.PlaceSaved) it.T.localPosition = it.PlaceBasePos;
                    if (it.SizeSaved) { var srt = it.T.GetComponent<RectTransform>(); if (srt != null) srt.sizeDelta = it.BaseSizeDelta; }
                    if (it.AddedLE != null && !it.AddedLE.WasCollected) UnityEngine.Object.Destroy(it.AddedLE);
                    else if (it.HeightSaved || it.WidthSaved)
                    {
                        var hle = it.T.GetComponent<LayoutElement>();
                        if (hle != null && it.HeightSaved) { hle.minHeight = it.BaseMinH; hle.preferredHeight = it.BasePrefH; }
                        if (hle != null && it.WidthSaved) { hle.minWidth = it.BaseMinW; hle.preferredWidth = it.BasePrefW; }
                    }
                    if (it.CG != null && !it.CG.WasCollected)
                    {
                        if (it.AddedCG) UnityEngine.Object.Destroy(it.CG);
                        else if (it.AlphaApplied)
                        {
                            if (Mathf.Abs(it.CG.alpha - it.SetAlpha) < 0.001f) it.CG.alpha = it.BaseAlpha;
                            it.CG.blocksRaycasts = it.BaseBlocks;
                        }
                    }
                    if ((it.HeightSaved || it.WidthSaved) && it.T.parent != null) LayoutRebuilder.MarkLayoutForRebuild(it.T.parent.GetComponent<RectTransform>());
                    if (it.LG != null && (it.BaseListPad != null || it.BaseListAlign.HasValue))
                    {
                        var bp = it.BaseListPad;
                        if (bp != null) it.LG.padding = new RectOffset(bp[0], bp[1], bp[2], bp[3]);
                        if (it.BaseListAlign.HasValue) it.LG.childAlignment = it.BaseListAlign.Value;
                        LayoutRebuilder.MarkLayoutForRebuild(it.T.GetComponent<RectTransform>());
                    }
                    if (it.LG != null && !float.IsNaN(it.BaseSpacing))
                    {
                        it.LG.spacing = it.BaseSpacing;
                        LayoutRebuilder.MarkLayoutForRebuild(it.T.GetComponent<RectTransform>());
                    }
                    if (it.G != null)
                    {
                        if (it.OwnColor && it.GameColorKnown && it.G.color == it.OwnSet) it.G.color = it.GameColor;
                        if (it.PadSaved)
                        {
                            var prt = it.T.GetComponent<RectTransform>();
                            if (prt != null && prt.offsetMin == it.PadSetMin && prt.offsetMax == it.PadSetMax) { prt.offsetMin = it.PadBaseMin; prt.offsetMax = it.PadBaseMax; }
                        }
                        if (it.SwapSet != null)
                        {
                            var simg = it.G.TryCast<Image>();
                            if (simg != null && simg.sprite != null && simg.sprite.Pointer == it.SwapSet.Pointer) simg.sprite = it.SwapBase;
                            it.SwapSet = null;
                        }
                        if (it.Rounded)
                        {
                            var rimg = it.G.TryCast<Image>();
                            if (rimg != null) { rimg.sprite = null; rimg.type = Image.Type.Simple; rimg.pixelsPerUnitMultiplier = it.BasePpuMult; }
                        }
                        if (it.BaseFont != null) { var tf = it.G.TryCast<Text>(); if (tf != null) tf.font = it.BaseFont; }
                        var txt = it.G.TryCast<Text>();
                        if (txt != null && it.BaseSize > 0) txt.fontSize = it.BaseSize;
                        if (txt != null && it.BaseMaxSize > 0) txt.resizeTextMaxSize = it.BaseMaxSize;
                        if (txt != null && it.BaseMinSize > 0) txt.resizeTextMinSize = it.BaseMinSize;
                        if (txt != null && it.TextSaved)
                        {
                            txt.resizeTextForBestFit = it.BaseBestFit;
                            txt.horizontalOverflow = it.BaseHOverflow;
                            txt.verticalOverflow = it.BaseVOverflow;
                        }
                        if (txt != null) TextStyler.Restore(txt, it.StyleState);
                        Unlayer(it);
                    }
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Applies a TextStyle to one UnityEngine.UI.Text and remembers the values it replaced, so they can be put back.
    /// Values are compared before writing (the game's Last.UI.FontSelector may set them again on enable), so an
    /// unchanged text costs only reads. Shadow / Outline: the component of exactly that type on the text's object is
    /// used (Outline derives from Shadow); missing ones are added and removed again on restore.
    /// </summary>
    static class TextStyler
    {
        internal class Effect
        {
            public Shadow C;
            public bool Added, Saved, BaseEnabled;
            public Color BaseColor;
            public Vector2 BaseDist;
            public List<Shadow> Extra = new List<Shadow>(); // blur copies (added by us, removed on restore)
            public bool HasFirst;                           // FirstColor captured (the colour before we touched it)
            public Color FirstColor;
        }

        internal class State
        {
            public bool StyleSaved, LineSaved, AlignSaved;
            public string Family;   // family the face is picked from (the text's font before we swapped it)
            public Font BaseFont;   // font before the first face swap
            public FontStyle BaseStyle;
            public float BaseLine;
            public TextAnchor BaseAlign;
            public Effect Sh = new Effect(), Ol = new Effect();
        }

        /// <summary>Applies the style; skip = properties another rule owns (an element rule beats a font-type rule).</summary>
        internal static void Apply(Text txt, TextStyle s, State st, TextStyle skip = null, bool skipFace = false)
        {
            if (s.Face != null && !skipFace && skip?.Face == null && txt.font != null)
            {
                // Family = the font someone else (game, KupoUI, an element "font") set; re-read when it changes.
                if (!Fonts.IsFace(txt.font)) st.Family = Fonts.FamilyName(txt.font);
                if (st.Family != null)
                {
                    var face = Fonts.Face(st.Family, s.Face);
                    if (face != null && txt.font.Pointer != face.Pointer)
                    {
                        if (st.BaseFont == null) st.BaseFont = txt.font;
                        txt.font = face;
                    }
                }
            }
            if (s.Style.HasValue && !(skip?.Style.HasValue ?? false) && txt.fontStyle != s.Style.Value)
            {
                if (!st.StyleSaved) { st.StyleSaved = true; st.BaseStyle = txt.fontStyle; }
                txt.fontStyle = s.Style.Value;
            }
            if (!float.IsNaN(s.LineSpacing) && !(skip != null && !float.IsNaN(skip.LineSpacing)) && Mathf.Abs(txt.lineSpacing - s.LineSpacing) > 0.0001f)
            {
                if (!st.LineSaved) { st.LineSaved = true; st.BaseLine = txt.lineSpacing; }
                txt.lineSpacing = s.LineSpacing;
            }
            if (s.Align.HasValue && !(skip?.Align.HasValue ?? false) && txt.alignment != s.Align.Value)
            {
                if (!st.AlignSaved) { st.AlignSaved = true; st.BaseAlign = txt.alignment; }
                txt.alignment = s.Align.Value;
            }
            if (s.Shadow != null && skip?.Shadow == null) ApplyEffect(txt, s.Shadow, st.Sh, "Shadow");
            if (s.Outline != null && skip?.Outline == null) ApplyEffect(txt, s.Outline, st.Ol, "Outline");
        }

        static Shadow Find(GameObject go, string type)
        {
            foreach (var c in go.GetComponents<Shadow>())
                if (c != null && c.GetIl2CppType().Name == type) return c;
            return null;
        }

        static void ApplyEffect(Text txt, EffectSpec spec, Effect e, string type)
        {
            if (e.C == null || e.C.WasCollected) { e.C = Find(txt.gameObject, type); e.Added = false; e.Saved = false; e.HasFirst = false; }
            if (spec.Off)
            {
                RemoveExtras(e);
                if (e.C != null && e.C.enabled) { Save(e); e.C.enabled = false; }
                return;
            }
            if (e.C == null)
            {
                e.C = type == "Outline" ? txt.gameObject.AddComponent<Outline>() : txt.gameObject.AddComponent<Shadow>();
                e.Added = true;
                e.C.effectColor = new Color(0f, 0f, 0f, 0.5f);
                if (Rules.Log) Plugin.L.LogInfo($"[TextStyles] added {type} to '{txt.name}'");
            }
            if (!e.HasFirst) { e.HasFirst = true; e.FirstColor = e.C.effectColor; }
            if (!e.C.enabled) { Save(e); e.C.enabled = true; }
            // 1440p px -> the text's own units: root-canvas units per px, divided by the text's scale relative to the root.
            float k = 0f;
            var canvas = txt.canvas;
            if (canvas != null)
            {
                var root = canvas.rootCanvas;
                float upp = Px.Upp(root);
                float rel = txt.transform.lossyScale.x / Mathf.Max(0.0001f, root.transform.lossyScale.x);
                k = upp / Mathf.Max(0.0001f, rel);
            }
            if (spec.Distance.HasValue && k > 0f)
            {
                var want = new Vector2(spec.Distance.Value.x, -spec.Distance.Value.y) * k;
                if ((e.C.effectDistance - want).sqrMagnitude > 0.0001f) { Save(e); e.C.effectDistance = want; }
            }
            // Colour; with blur the main copy is fainter and 8 copies in a ring around it fill in a soft edge.
            Color col = spec.Color ?? e.FirstColor;
            bool blur = spec.Blur > 0f && type == "Shadow" && k > 0f;
            var main = blur ? new Color(col.r, col.g, col.b, col.a * 0.5f) : col;
            if ((spec.Color.HasValue || blur) && e.C.effectColor != main) { Save(e); e.C.effectColor = main; }
            if (!blur) { RemoveExtras(e); return; }
            var ring = new Color(col.r, col.g, col.b, col.a * 0.2f);
            while (e.Extra.Count < 8) e.Extra.Add(txt.gameObject.AddComponent<Shadow>());
            for (int i = 0; i < 8; i++)
            {
                var x = e.Extra[i];
                if (x == null || x.WasCollected) { x = e.Extra[i] = txt.gameObject.AddComponent<Shadow>(); }
                float a = i * Mathf.PI / 4f;
                var d = e.C.effectDistance + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (spec.Blur * k);
                if ((x.effectDistance - d).sqrMagnitude > 0.0001f) x.effectDistance = d;
                if (x.effectColor != ring) x.effectColor = ring;
                if (!x.enabled) x.enabled = true;
            }
        }

        static void RemoveExtras(Effect e)
        {
            foreach (var x in e.Extra) if (x != null && !x.WasCollected) UnityEngine.Object.Destroy(x);
            e.Extra.Clear();
        }

        static void Save(Effect e)
        {
            if (e.Saved || e.Added) return;
            e.Saved = true;
            e.BaseEnabled = e.C.enabled; e.BaseColor = e.C.effectColor; e.BaseDist = e.C.effectDistance;
        }

        internal static void Restore(Text txt, State st)
        {
            if (st == null) return;
            try
            {
                if (txt != null && !txt.WasCollected)
                {
                    if (st.BaseFont != null && Fonts.IsFace(txt.font)) txt.font = st.BaseFont;
                    if (st.StyleSaved) txt.fontStyle = st.BaseStyle;
                    if (st.LineSaved) txt.lineSpacing = st.BaseLine;
                    if (st.AlignSaved) txt.alignment = st.BaseAlign;
                }
                foreach (var e in new[] { st.Sh, st.Ol })
                {
                    RemoveExtras(e);
                    if (e.C == null || e.C.WasCollected) continue;
                    if (e.Added) UnityEngine.Object.Destroy(e.C);
                    else if (e.Saved) { e.C.enabled = e.BaseEnabled; e.C.effectColor = e.BaseColor; e.C.effectDistance = e.BaseDist; }
                }
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// MenuLayout "textStyles": style rules for every text of a font type. The type is read from the text's
    /// Last.UI.FontSelector component (fields myText, type : FontType) [verified, interop dump]; texts without one are
    /// not affected. Found a few times a second, applied every frame; element rules win where both set a property.
    /// </summary>
    static class FontTypeStyles
    {
        internal static List<TextStyle> Rules = new List<TextStyle>();

        class Item { public Text T; public TextStyle S; public TextStyler.State St = new TextStyler.State(); }
        static readonly Dictionary<IntPtr, Item> items = new Dictionary<IntPtr, Item>();
        static readonly HashSet<string> logged = new HashSet<string>();

        /// <summary>Event-driven: texts just switched on; their FontSelector (same object) gives the font type.</summary>
        internal static void Consider(List<Graphic> batch)
        {
            if (Rules.Count == 0) return;
            foreach (var g in batch)
            {
                try
                {
                    if (g == null || g.WasCollected) continue;
                    // Known text switched on again: the game may have reset it (FontSelector re-applies on enable).
                    if (items.ContainsKey(g.Pointer)) { dirty.Add(g.Pointer); continue; }
                    var txt = g.TryCast<Text>();
                    if (txt == null) continue;
                    var fs = txt.GetComponent<Last.UI.FontSelector>();
                    if (fs == null) continue;
                    string type = fs.type.ToString();
                    TextStyle rule = null;
                    foreach (var r in Rules) if (r.FontTypes.Contains(type)) { rule = r; break; }
                    if (rule == null) continue;
                    Add(txt, rule);
                    if (global::FF5MenuSprites.Rules.Log && logged.Add(type + "|" + txt.name))
                        Plugin.L.LogInfo($"[TextStyles] {type}: '{txt.name}' text='{txt.text}'");
                }
                catch (Exception e) { if (global::FF5MenuSprites.Rules.Log) Plugin.L.LogInfo("[TextStyles] consider: " + e.Message); }
            }
        }

        internal static void Scan()
        {
            if (Rules.Count == 0) return;
            var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Last.UI.FontSelector>());
            if (found == null) return;
            foreach (var o in found)
            {
                var fs = o?.TryCast<Last.UI.FontSelector>();
                var txt = fs?.myText;
                if (txt == null || items.ContainsKey(txt.Pointer)) continue;
                string type = fs.type.ToString();
                TextStyle rule = null;
                foreach (var r in Rules) if (r.FontTypes.Contains(type)) { rule = r; break; }
                if (rule == null) continue;
                Add(txt, rule);
                if (global::FF5MenuSprites.Rules.Log && logged.Add(type + "|" + txt.name))
                    Plugin.L.LogInfo($"[TextStyles] {type}: '{txt.name}' text='{txt.text}'");
            }
        }

        // Applying every text every frame cost ~1.8 ms/frame and grew with every screen visited [perf log 2026-11-05].
        // Now: texts are styled when found and whenever they are switched on again (dirty), plus a rolling re-check of
        // RollPerFrame texts per frame in case something changes a text without re-enabling it.
        const int RollPerFrame = 25;
        static readonly HashSet<IntPtr> dirty = new HashSet<IntPtr>();
        static readonly List<IntPtr> order = new List<IntPtr>();
        static int roll;

        static void Add(Text txt, TextStyle rule)
        {
            items[txt.Pointer] = new Item { T = txt, S = rule };
            order.Add(txt.Pointer);
            dirty.Add(txt.Pointer);
        }

        static bool ApplyOne(IntPtr key)
        {
            if (!items.TryGetValue(key, out var it)) return true;
            try
            {
                if (it.T == null || it.T.WasCollected) return false;
                if (!it.T.isActiveAndEnabled) return true;
                TextStyler.Apply(it.T, it.S, it.St, Elements.StyleOf(key), Elements.OwnsFont(key));
                return true;
            }
            catch (Exception e) { Plugin.L.LogWarning("[TextStyles] dropped a text: " + e.Message); return false; }
        }

        internal static void Apply()
        {
            if (items.Count == 0) return;
            List<IntPtr> dead = null;
            if (dirty.Count > 0)
            {
                foreach (var k in dirty) if (!ApplyOne(k)) (dead ??= new List<IntPtr>()).Add(k);
                dirty.Clear();
            }
            for (int n = 0; n < RollPerFrame && order.Count > 0; n++)
            {
                if (roll >= order.Count) roll = 0;
                var k = order[roll];
                if (!ApplyOne(k)) { (dead ??= new List<IntPtr>()).Add(k); order.RemoveAt(roll); }
                else roll++;
            }
            if (dead != null) foreach (var k in dead) { items.Remove(k); order.Remove(k); }
        }

        internal static void RestoreAll()
        {
            foreach (var it in items.Values) TextStyler.Restore(it.T, it.St);
            items.Clear(); order.Clear(); dirty.Clear(); roll = 0;
        }
    }

    /// <summary>
    /// Font lookup for "elements" rules: first a UnityEngine.Font already loaded (game fonts and fonts other mods
    /// loaded, matched by object name, case-insensitive), else Font.CreateDynamicFontFromOSFont (installed Windows
    /// fonts). Results are cached; a name that is not found is warned about once. The first lookup logs the loaded
    /// font names so the right one can be picked.
    /// </summary>
    static class Fonts
    {
        static readonly Dictionary<string, Font> cache = new Dictionary<string, Font>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, float> retryAt = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        static bool listed;

        static readonly Dictionary<string, Font> faces = new Dictionary<string, Font>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<IntPtr, string> facePtrs = new Dictionary<IntPtr, string>(); // face -> family
        static string[] osNames;

        /// <summary>True if this font is a face made by Face() (so its family is not its own name).</summary>
        internal static bool IsFace(Font f) => f != null && facePtrs.ContainsKey(f.Pointer);

        /// <summary>True if the text's font is this family itself or a face Face() made from it.</summary>
        internal static bool IsFamily(Font f, Font family) =>
            f != null && family != null && (f.Pointer == family.Pointer ||
            (facePtrs.TryGetValue(f.Pointer, out var fam) && string.Equals(fam, family.name, StringComparison.OrdinalIgnoreCase)));

        /// <summary>
        /// The installed face "family modifiers" (e.g. "Josefin Sans" + "SemiBold"), as a dynamic OS font; null if
        /// Windows has no such face (warned once, with the faces of that family that do exist). Matching ignores case
        /// and spaces; Windows cuts face names at 31 characters ("Segoe UI Variable Small Semibol"), so a cut name
        /// that is the start of the wanted one also matches.
        /// </summary>
        internal static Font Face(string family, string mods)
        {
            string want = family + " " + mods;
            if (faces.TryGetValue(want, out var f)) return f != null && !f.WasCollected ? f : null;
            osNames ??= Font.GetOSInstalledFontNames();
            string Key(string s) => s.Replace(" ", "").ToLowerInvariant();
            string wk = Key(want), found = null;
            foreach (var n in osNames)
            {
                string nk = Key(n);
                if (nk == wk || (n.Length >= 31 && wk.StartsWith(nk) && nk.Length > Key(family).Length)) { found = n; break; }
            }
            if (found != null)
            {
                f = Font.CreateDynamicFontFromOSFont(found, 32);
                if (f != null) { UnityEngine.Object.DontDestroyOnLoad(f); facePtrs[f.Pointer] = family; }
                Plugin.L.LogInfo($"[Fonts] face '{want}' -> installed '{found}'");
            }
            else
            {
                // Unity files faces under the typographic family (name ID 16) and keeps only Regular/Bold/Italic, so a
                // static weight such as JosefinSans-Light.ttf is not listed [inferred; list verified, log 2026-11-05].
                // Asking for the bare name does not reach it either: the font is created but draws as a stand-in (user,
                // 2026-11-05; the equip screen's % signs vanished). So an unlisted face leaves the text's font alone.
                var same = new List<string>();
                foreach (var n in osNames) if (n.StartsWith(family, StringComparison.OrdinalIgnoreCase)) same.Add(n);
                Plugin.L.LogWarning($"[Fonts] no face '{want}' in the game's font list; text keeps its font. Faces of '{family}' there: {(same.Count > 0 ? string.Join(", ", same) : "none")}. " +
                                    "Weights other than Regular/Bold/Italic need a font file whose family is its own (README: Font faces).");
            }
            faces[want] = f;
            return f;
        }

        // Fonts we created from installed Windows fonts: pointer -> the Windows name. They are renamed "<name> (SpriteMod)"
        // so another mod that recognises its own fonts by name (KupoUI.PR keeps the size of texts in its fonts) leaves
        // texts in our copy alone [inferred, user test 2026-11-06].
        static readonly Dictionary<IntPtr, string> own = new Dictionary<IntPtr, string>();
        const string OwnSuffix = " (SpriteMod)";

        internal static bool IsOwn(Font f) => f != null && (own.ContainsKey(f.Pointer) || facePtrs.ContainsKey(f.Pointer));

        /// <summary>The font's family name: the Windows name for our own copies, else the object name.</summary>
        internal static string FamilyName(Font f) =>
            f == null ? null : own.TryGetValue(f.Pointer, out var n) ? n : f.name.EndsWith(OwnSuffix) ? f.name.Substring(0, f.name.Length - OwnSuffix.Length) : f.name;

        /// <summary>Our own copy of a font (same Windows font), so its size can be changed; null if it is not an installed font.</summary>
        internal static Font OwnCopy(Font f)
        {
            if (f == null) return null;
            if (IsOwn(f)) return f;
            return Own(FamilyName(f), false);
        }

        static readonly Dictionary<string, Font> ownCache = new Dictionary<string, Font>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> notOwnWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Used by rules that change the font size: our own renamed copy of the installed Windows font; if it is not
        /// installed, Get(name) (fallback = true) or null. Other rules use Get, so texts that only change font keep the
        /// copy KupoUI loaded and whatever KupoUI does with it (e.g. its YOffset).
        /// </summary>
        internal static Font Own(string name, bool fallback)
        {
            if (ownCache.TryGetValue(name, out var f) && f != null && !f.WasCollected) return f;
            if (!ownCache.ContainsKey(name))
            {
                string osName = null;
                foreach (var n in osNames ??= Font.GetOSInstalledFontNames())
                    if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) { osName = n; break; }
                f = null;
                if (osName != null)
                {
                    f = Font.CreateDynamicFontFromOSFont(osName, 32);
                    if (f != null)
                    {
                        UnityEngine.Object.DontDestroyOnLoad(f);
                        f.name = osName + OwnSuffix;
                        own[f.Pointer] = osName;
                        Plugin.L.LogInfo($"[Fonts] '{osName}': own copy made from the installed Windows font (for a size change)");
                    }
                }
                else if (notOwnWarned.Add(name))
                    Plugin.L.LogWarning($"[Fonts] '{name}' is not an installed Windows font, so fontScale uses the game's/another mod's copy and may not work with it (info)");
                ownCache[name] = f;
            }
            return f ?? (fallback ? Get(name) : null);
        }

        internal static Font Get(string name)
        {
            if (cache.TryGetValue(name, out var f) && f != null && !f.WasCollected) return f;
            // Not found before: look again at most every 5 s (other mods may load their fonts later).
            if (retryAt.TryGetValue(name, out var t) && Time.unscaledTime < t) return null;
            retryAt[name] = Time.unscaledTime + 5f;
            f = null;
            var loaded = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Font>());
            var names = new List<string>();
            if (loaded != null)
                foreach (var o in loaded)
                {
                    var lf = o?.TryCast<Font>();
                    if (lf == null) continue;
                    names.Add(lf.name);
                    if (f == null && string.Equals(lf.name, name, StringComparison.OrdinalIgnoreCase)) f = lf;
                }
            if (!listed)
            {
                listed = true;
                Plugin.L.LogInfo("[Fonts] loaded fonts: " + string.Join(", ", names));
            }
            if (f == null)
            {
                bool installed = false;
                foreach (var n in osNames ??= Font.GetOSInstalledFontNames())
                    if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) { installed = true; name = n; break; }
                if (installed)
                {
                    f = Font.CreateDynamicFontFromOSFont(name, 32);
                    if (f != null) UnityEngine.Object.DontDestroyOnLoad(f);
                    Plugin.L.LogInfo($"[Fonts] '{name}': created from the installed Windows font");
                }
                else if (!cache.ContainsKey(name))
                    Plugin.L.LogWarning($"[Fonts] '{name}' is neither a loaded font nor an installed Windows font; text keeps its font");
            }
            cache[name] = f;
            return f;
        }
    }

    /// <summary>
    /// Screen dump: pressing the key from MenuLayout.json "dumpKey" (default F8; F1-F12; "" = off) while the game
    /// window has focus writes every active UI object and sprite on screen to
    /// Modules/System/FF5/_dumps/screen_&lt;time&gt;.tsv: object path, components, 1440p box (y down from the top),
    /// and text / font / size / colour / sprite / layout settings, to find paths and names for rules.
    /// The key is read with Unity's own input (UnityEngine.Input.GetKey, InputLegacyModule [verified, interop dump
    /// 2026-11-14]), not a Windows keyboard API: user32 GetAsyncKeyState is a keylogger signature for virus scanners.
    /// If Unity's input fails (e.g. switched off in the game), the dump key is disabled with one warning.
    /// </summary>
    static class ScreenDump
    {
        internal static string Key = "F8";
        static bool wasDown, broken;

        static KeyCode FKey(string k)
        {
            if (string.IsNullOrEmpty(k) || k.Length < 2 || (k[0] != 'F' && k[0] != 'f')) return KeyCode.None;
            return int.TryParse(k.Substring(1), out int n) && n >= 1 && n <= 12 ? KeyCode.F1 + (n - 1) : KeyCode.None;
        }

        internal static void Poll()
        {
            if (broken) return;
            var key = FKey(Key);
            if (key == KeyCode.None) return;
            bool down;
            try { down = Input.GetKey(key); }
            catch (Exception e)
            {
                broken = true;
                Plugin.L.LogWarning($"[Dump] Unity input not available, dump key '{Key}' disabled: {e.GetType().Name}: {e.Message}");
                return;
            }
            if (down && !wasDown && Application.isFocused) Write();
            wasDown = down;
        }

        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; i < 24 && t != null; i++, t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }

        static string Clean(string s) => s == null ? "" : s.Replace("\t", " ").Replace("\r", "").Replace("\n", "\\n");

        static string Hex(Color c) => "#" + ((int)Mathf.Round(c.r * 255)).ToString("X2") + ((int)Mathf.Round(c.g * 255)).ToString("X2") +
                                      ((int)Mathf.Round(c.b * 255)).ToString("X2") + (c.a < 1f ? ((int)Mathf.Round(c.a * 255)).ToString("X2") : "");

        static Dictionary<IntPtr, string> ruleMap = new Dictionary<IntPtr, string>();

        static void Write()
        {
            var rows = new List<string>();
            try { ruleMap = Elements.RuleMap(); } catch (Exception) { ruleMap = new Dictionary<IntPtr, string>(); }
            // Cameras: name, view and render target (battle/field layers are separate cameras).
            foreach (var cam in Camera.allCameras)
            {
                try
                {
                    var tt = cam.targetTexture;
                    rows.Add($"{Clean(PathOf(cam.transform))}\tCamera\t\tcamera enabled={cam.enabled} depth={cam.depth} position={cam.transform.position} orthographic={cam.orthographic} size={cam.orthographicSize:0.##} target={(tt == null ? "screen" : $"'{tt.name}' {tt.width}x{tt.height}")}");
                }
                catch (Exception e) { rows.Add("camera\t\t\terror: " + e.Message); }
            }
            var rts = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<RectTransform>());
            if (rts != null)
                foreach (var o in rts)
                {
                    var rt = o?.TryCast<RectTransform>();
                    if (rt == null || !rt.gameObject.activeInHierarchy) continue;
                    try { rows.Add(Row(rt.transform, rt)); } catch (Exception e) { rows.Add(Clean(PathOf(rt.transform)) + "\t\t\terror: " + e.Message); }
                }
            var srs = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SpriteRenderer>());
            if (srs != null)
                foreach (var o in srs)
                {
                    var sr = o?.TryCast<SpriteRenderer>();
                    if (sr == null || !sr.gameObject.activeInHierarchy) continue;
                    try { rows.Add(Row(sr.transform, null)); } catch (Exception e) { rows.Add(Clean(PathOf(sr.transform)) + "\t\t\terror: " + e.Message); }
                }
            rows.Sort(StringComparer.Ordinal);
            string dir = Path.Combine(Path.GetDirectoryName(Plugin.ConfigPath), "_dumps");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "screen_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".tsv");
            using (var w = new StreamWriter(file, false, new System.Text.UTF8Encoding(false)))
            {
                w.WriteLine($"# FF5MenuSprites screen dump, screen {Screen.width}x{Screen.height}; boxes in 1440p px (x from left, y from top); world sprites: world position and approx. screen box");
                try { w.WriteLine($"# textures: masterTextureLimit={QualitySettings.masterTextureLimit} streamingMipmapsActive={QualitySettings.streamingMipmapsActive} budget={QualitySettings.streamingMipmapsMemoryBudget:0}MB maxReduction={QualitySettings.streamingMipmapsMaxLevelReduction} aniso={QualitySettings.anisotropicFiltering}"); } catch (Exception e) { w.WriteLine("# textures: " + e.Message); }
                try { foreach (var line in Elements.BgInfo()) w.WriteLine("# bg: " + line); } catch (Exception e) { w.WriteLine("# bg: " + e.Message); }
                try { foreach (var line in Elements.OtherRenderers()) w.WriteLine("# renderer: " + line); } catch (Exception e) { w.WriteLine("# renderer: " + e.Message); }
                w.WriteLine("path\tcomponents\tbox1440\tdetails");
                foreach (var r in rows) w.WriteLine(r);
            }
            Plugin.L.LogInfo($"[Dump] {rows.Count} objects -> {file}");
        }

        static string Row(Transform t, RectTransform rt)
        {
            var comps = new List<string>();
            var details = new List<string>();
            if (ruleMap.TryGetValue(t.Pointer, out var ruleNames)) details.Add($"RULE='{ruleNames}'");
            var sc = t.localScale;
            if (sc != Vector3.one) details.Add($"scale={sc.x:0.###}x{sc.y:0.###}");
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null) continue;
                string n = c.GetIl2CppType().Name;
                if (n == "Transform" || n == "RectTransform" || n == "CanvasRenderer") continue;
                var beh = c.TryCast<Behaviour>();
                comps.Add(beh != null && !beh.enabled ? n + "(off)" : n);
                var txt = c.TryCast<Text>();
                if (txt != null)
                {
                    details.Add($"text='{Clean(txt.text).Substring(0, Math.Min(80, Clean(txt.text).Length))}' font={txt.font?.name} size={txt.fontSize} bestFit={txt.resizeTextForBestFit}{(txt.resizeTextForBestFit ? $" fit={txt.resizeTextMinSize}-{txt.resizeTextMaxSize} shown={txt.cachedTextGenerator.fontSizeUsedForBestFit}" : "")} style={txt.fontStyle} align={txt.alignment} lineSpacing={txt.lineSpacing:0.##} color={Hex(txt.color)}");
                    continue;
                }
                var fsel = c.TryCast<Last.UI.FontSelector>();
                if (fsel != null) { details.Add($"fontType={fsel.type}"); continue; }
                var fx = c.TryCast<Shadow>();
                if (fx != null) { details.Add($"{n.ToLowerInvariant()} color={Hex(fx.effectColor)} distance={fx.effectDistance.x:0.##},{fx.effectDistance.y:0.##}"); continue; }
                var img = c.TryCast<Image>();
                if (img != null) { details.Add($"sprite={img.sprite?.name} color={Hex(img.color)} type={img.type}"); continue; }
                var sr = c.TryCast<SpriteRenderer>();
                if (sr != null) { details.Add($"sprite={sr.sprite?.name} color={Hex(sr.color)} world={sr.transform.position} order={sr.sortingOrder} scale={sr.transform.lossyScale}{Elements.TexInfo(sr.sprite)}"); continue; }
                var lg = c.TryCast<HorizontalOrVerticalLayoutGroup>();
                if (lg != null) { details.Add($"spacing={lg.spacing:0.#} align={lg.childAlignment} controlW={lg.childControlWidth} controlH={lg.childControlHeight}"); continue; }
                var le = c.TryCast<LayoutElement>();
                if (le != null) { details.Add($"layout pref={le.preferredWidth:0.#}x{le.preferredHeight:0.#} ignore={le.ignoreLayout}"); continue; }
                var cv = c.TryCast<Canvas>();
                if (cv != null) { details.Add($"canvas mode={cv.renderMode} scale={cv.scaleFactor:0.###} order={cv.sortingOrder}"); continue; }
                var cs = c.TryCast<CanvasScaler>();
                if (cs != null) details.Add($"scaler mode={cs.uiScaleMode} reference={cs.referenceResolution.x:0}x{cs.referenceResolution.y:0} match={cs.screenMatchMode} widthOrHeight={cs.matchWidthOrHeight:0.##} ppu={cs.referencePixelsPerUnit:0.#}");
            }
            string box = "";
            if (rt != null)
            {
                var canvas = rt.GetComponentInParent<Canvas>();
                var root = canvas != null ? canvas.rootCanvas : null;
                if (root != null && root.renderMode != RenderMode.WorldSpace)
                {
                    var c = new Il2CppStructArray<Vector3>(4);
                    rt.GetWorldCorners(c);
                    Vector3 a = root.transform.InverseTransformPoint(c[0]), b = root.transform.InverseTransformPoint(c[2]);
                    float ppu = Px.PerUnit(root);
                    float hw = Screen.width * 1440f / Screen.height / 2f;
                    box = $"x {a.x * ppu + hw:0}-{b.x * ppu + hw:0} y {720 - b.y * ppu:0}-{720 - a.y * ppu:0}";
                }
            }
            return $"{Clean(PathOf(t))}\t{string.Join(",", comps)}\t{box}\t{string.Join(" | ", details)}";
        }
    }

    /// <summary>Finds menu character slots a few times a second and keeps their images laid out by the rules.</summary>
    /// <summary>
    /// Collects UI graphics as the game switches them on (Harmony postfix on UnityEngine.UI.Graphic.OnEnable, the same
    /// hook KupoUI uses: "GraphicOnEnablePostfix" [KupoUI strings]). The watcher hands them to the rule systems once per
    /// frame. This replaces the periodic FindObjectsOfType sweeps, which cost ~17 ms each whatever they searched for and
    /// took 47% of frame time at 5 sweeps per 0.2 s [verified, perf log 2026-11-05].
    /// </summary>
    static class Discovery
    {
        internal static List<Graphic> Pending = new List<Graphic>();
        static List<Graphic> spare = new List<Graphic>();
        internal static bool Hooked; // false = the OnEnable patch failed; the watcher then sweeps once a second

        static readonly List<string> names = new List<string>();

        /// <summary>The graphics switched on since the last call (the list is reused; don't keep it).</summary>
        internal static List<Graphic> Take()
        {
            var b = Pending;
            Pending = spare;
            Pending.Clear();
            spare = b;
            // Object names, read once per batch (each read crosses into the game and allocates a string).
            names.Clear();
            foreach (var g in b)
            {
                string n = null;
                try { if (g != null && !g.WasCollected) n = g.name; } catch (Exception) { }
                names.Add(n ?? "");
            }
            return b;
        }

        /// <summary>Name of batch[i] from the last Take().</summary>
        internal static string Name(int i) => i < names.Count ? names[i] : "";

        /// <summary>
        /// While loading, hand out at most this many per frame (the loading animation stutters on long frames); the
        /// rest waits in Pending. Outside loading everything is handled at once, so screens never show unstyled.
        /// </summary>
        internal static List<Graphic> TakeSome(int max)
        {
            if (Pending.Count <= max) return Take();
            var b = spare;
            b.Clear();
            b.AddRange(Pending.GetRange(0, max));
            Pending.RemoveRange(0, max);
            names.Clear();
            foreach (var g in b)
            {
                string n = null;
                try { if (g != null && !g.WasCollected) n = g.name; } catch (Exception) { }
                names.Add(n ?? "");
            }
            return b;
        }
    }

    /// <summary>Patched by hand in Plugin.Load (own try/catch), so a failure can't stop the camera patches.</summary>
    static class GraphicOnEnablePatch
    {
        internal static void Postfix(Graphic __instance)
        {
            try { if (__instance != null) Discovery.Pending.Add(__instance); }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// MenuLayout "perf": true measures frame times and the time each plugin system takes, and logs a summary every
    /// 5 s at Warning level (so it reaches the disk log with default settings). Frames while the loading icon shows
    /// are counted separately. Off = two cheap checks per system.
    /// </summary>
    static class Perf
    {
        internal static bool Enabled;
        class Stat { public double Total, Max; public int Calls; }
        static readonly Dictionary<string, Stat> stats = new Dictionary<string, Stat>();
        static readonly List<float> frames = new List<float>(), loadFrames = new List<float>();
        static float nextReport;
        static int gc0;

        internal static long Start() => Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

        internal static void Stop(string name, long t0)
        {
            if (!Enabled || t0 == 0) return;
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (!stats.TryGetValue(name, out var s)) stats[name] = s = new Stat();
            s.Total += ms; s.Calls++; if (ms > s.Max) s.Max = ms;
        }

        /// <summary>Called once per frame (Update).</summary>
        internal static void Frame()
        {
            if (!Enabled) return;
            (LoadingIcon.Showing ? loadFrames : frames).Add(Time.unscaledDeltaTime * 1000f);
            if (Time.unscaledTime < nextReport) return;
            nextReport = Time.unscaledTime + 5f;
            int gc = GC.CollectionCount(0);
            var sb = new System.Text.StringBuilder("[Perf] 5 s: ");
            sb.Append(Summary("frames", frames)).Append(Summary(" | loading frames", loadFrames));
            sb.Append($" | .NET GC gen0 +{gc - gc0}; plugin ms (total / worst call / calls):");
            double total = 0;
            foreach (var kv in stats) { total += kv.Value.Total; sb.Append($" {kv.Key} {kv.Value.Total:0.0}/{kv.Value.Max:0.0}/{kv.Value.Calls}"); }
            sb.Append($" | plugin total {total:0.0} ms | {Cleanup.Report()} (info, not an error)");
            Plugin.L.LogWarning(sb.ToString());
            gc0 = gc;
            frames.Clear(); loadFrames.Clear(); stats.Clear();
        }

        static string Summary(string label, List<float> f)
        {
            if (f.Count == 0) return label + " none";
            f.Sort();
            float sum = 0; foreach (var x in f) sum += x;
            int slow = 0; foreach (var x in f) if (x > 33.4f) slow++;
            return $"{label} {f.Count}, avg {sum / f.Count:0.0} ms, 95% {f[(int)(f.Count * 0.95f)]:0.0} ms, worst {f[f.Count - 1]:0.0} ms, over 33 ms: {slow}";
        }
    }

    public class LayoutWatcher : MonoBehaviour
    {
        public LayoutWatcher(IntPtr ptr) : base(ptr) { }

        class Target
        {
            public Image Img;
            public Rule Rule;
            public string Path;
            public bool Applied;
            public Vector3 OrigScale, OrigPos, SetScale, SetPos;
        }

        // Character pose sprites (BC_FF5_Pnnn_Jnn/<pose>), also when KupoUI renamed them "<pose>_Custom".
        static readonly System.Text.RegularExpressions.Regex PoseName =
            new System.Text.RegularExpressions.Regex(@"^(Default|Dying|Down|Win|Damage|Ready|SkillReady|LeftAttack|RightAttack)_\d\d");
        readonly Dictionary<IntPtr, Target> targets = new Dictionary<IntPtr, Target>();
        readonly HashSet<string> seenPaths = new HashSet<string>();
        float nextScan, nextBattleCheck, nextFallback, nextWatch;
        bool alive;

        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; i < 14 && t != null; i++, t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts);
        }

        public void Update()
        {
            try
            {
                if (!alive) { alive = true; Plugin.L.LogInfo("[MenuLayout] watcher running"); Plugin.HookAfterLayout(); }
                try { Perf.Frame(); } catch (Exception e) { Plugin.L.LogWarning("[Perf] " + e.Message); }
                try { GameProfiler.Frame(); } catch (Exception e) { Plugin.L.LogWarning("[GameProf] " + e.Message); }
                try { ScreenDump.Poll(); }
                catch (Exception e) { Plugin.L.LogWarning("[Dump] " + e.Message); }
                try { FieldSprites.Tick(); }
                catch (Exception e) { Plugin.L.LogWarning("[Field] " + e.Message); }
                try { FieldTiles.Tick(); }
                catch (Exception e) { Plugin.L.LogWarning("[MapTiles] " + e.Message); }
                // Objects the game switched on since last frame: check only those (no scene sweeps).
                long t0 = Perf.Start();
                if (Discovery.Pending.Count > 0)
                {
                    var batch = LoadingIcon.Showing ? Discovery.TakeSome(60) : Discovery.Take();
                    bool battle = Elements.InBattle();
                    try { Elements.Consider(batch, battle); } catch (Exception e) { Plugin.L.LogWarning("[Elements] consider: " + e.Message); }
                    try { FontTypeStyles.Consider(batch); } catch (Exception e) { Plugin.L.LogWarning("[TextStyles] consider: " + e.Message); }
                    try { TitleHighlight.Consider(batch); } catch (Exception e) { Plugin.L.LogWarning("[Title] consider: " + e.Message); }
                    try { ConsiderSlots(batch); } catch (Exception e) { Plugin.L.LogWarning("[MenuLayout] consider: " + e.Message); }
                    batch.Clear();
                }
                Perf.Stop("discovery", t0);
                if (Time.unscaledTime >= nextWatch)
                {
                    nextWatch = Time.unscaledTime + 0.1f;
                    t0 = Perf.Start();
                    try { Elements.WatchText(); } catch (Exception e) { Plugin.L.LogWarning("[Elements] watch: " + e.Message); }
                    Perf.Stop("textWatch", t0);
                }
                if (Time.unscaledTime < nextScan) return;
                nextScan = Time.unscaledTime + 0.2f;
                t0 = Perf.Start();
                bool changed = Rules.Refresh();
                if (changed)
                {
                    foreach (var t in targets.Values) { try { Restore(t); } catch { } }
                    targets.Clear();
                    Elements.RestoreAll();
                    FontTypeStyles.RestoreAll();
                }
                Perf.Stop("rulesFile", t0);
                bool sweep = changed;
                if (!Discovery.Hooked && Time.unscaledTime >= nextFallback) { nextFallback = Time.unscaledTime + 1f; sweep = true; }
                if (sweep)
                {
                    // One full search for what is already on screen; from then on the OnEnable hook finds new objects.
                    t0 = Perf.Start();
                    try { Scan(); } catch (Exception e) { Plugin.L.LogWarning("[MenuLayout] scan: " + e.Message); }
                    try { Elements.Scan(); } catch (Exception e) { Plugin.L.LogWarning("[Elements] scan: " + e.Message); }
                    try { FontTypeStyles.Scan(); } catch (Exception e) { Plugin.L.LogWarning("[TextStyles] scan: " + e.Message); }
                    try { TitleHighlight.FullScan(); } catch (Exception e) { Plugin.L.LogWarning("[Title] scan: " + e.Message); }
                    Perf.Stop("fullScan", t0);
                }
                if (Time.unscaledTime >= nextBattleCheck)
                {
                    nextBattleCheck = Time.unscaledTime + 2f;
                    t0 = Perf.Start();
                    // Sprite-name rules can't be found by the hook (a sprite can change without re-enabling): slow sweep,
                    // only when such rules exist. Battle state: only for the log or the resolution test setting.
                    if (Elements.HasSpriteRules()) { try { Elements.Scan(); } catch (Exception e) { Plugin.L.LogWarning("[Elements] scan: " + e.Message); } }
                    if (TextureLimit.Want >= 0) TextureLimit.Apply();
                    if (Rules.Log || Battle.ResolutionScale > 0) Battle.Check();
                    Perf.Stop("slowChecks", t0);
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("[MenuLayout] update: " + e.Message); }
        }

        public void LateUpdate()
        {
            long t0 = Perf.Start();
            try { BackgroundFit.Apply(); }
            catch (Exception e) { Plugin.L.LogWarning("[Render] background fit: " + e.Message); }
            Perf.Stop("backgroundFit", t0); t0 = Perf.Start();
            try { BattleShift.Apply(); }
            catch (Exception e) { Plugin.L.LogWarning("[Battle] sprites down: " + e.Message); }
            Perf.Stop("battleShift", t0); t0 = Perf.Start();
            try { RenderScale.Apply(); }
            catch (Exception e) { Plugin.L.LogWarning("[Render] " + e.Message); }
            try { BattleCharScale.Apply(); }
            catch (Exception e) { Plugin.L.LogWarning("[Battle] character size: " + e.Message); }
            try { BgMosaic.Apply(); }
            catch (Exception e) { Plugin.L.LogWarning("[Render] background mosaic: " + e.Message); }
            Perf.Stop("renderScale", t0); t0 = Perf.Start();
            try { LoadingIcon.Apply(); }
            catch (Exception e) { Plugin.L.LogWarning("[Loading] " + e.Message); }
            Perf.Stop("loadingIcon", t0); t0 = Perf.Start();
            try { FontTypeStyles.Apply(); }
            catch (Exception e) { Plugin.L.LogWarning("[TextStyles] " + e.Message); }
            Perf.Stop("textStyleApply", t0); t0 = Perf.Start();
            try { Elements.Apply(); }
            catch (Exception e) { Plugin.L.LogWarning("[Elements] " + e.Message); }
            Perf.Stop("elementApply", t0); t0 = Perf.Start();
            try { TitleHighlight.Apply(); }
            catch (Exception e) { Plugin.L.LogWarning("[Title] " + e.Message); }
            Perf.Stop("titleHighlight", t0); t0 = Perf.Start();
            try
            {
                var dead = new List<IntPtr>();
                foreach (var kv in targets)
                {
                    var t = kv.Value;
                    try
                    {
                        // A destroyed Unity object can still look non-null here; any failure drops the target.
                        if (t.Img == null || t.Img.WasCollected || t.Img.gameObject == null) { dead.Add(kv.Key); continue; }
                        if (!t.Img.isActiveAndEnabled) continue;
                        Apply(t);
                    }
                    catch (Exception e)
                    {
                        dead.Add(kv.Key);
                        if (Rules.Log) Plugin.L.LogInfo($"[MenuLayout] dropped a '{t.Rule.Name}' image: {e.Message}");
                    }
                }
                foreach (var k in dead) targets.Remove(k);
            }
            catch (Exception e) { Plugin.L.LogWarning("[MenuLayout] late update: " + e.Message); }
            Perf.Stop("spriteApply", t0);
        }

        /// <summary>Event-driven: a character image was switched on; its slot (MenuCharacterController) is a parent.</summary>
        void ConsiderSlots(List<Graphic> batch)
        {
            HashSet<IntPtr> done = null;
            for (int bi = 0; bi < batch.Count; bi++)
            {
                var g = batch[bi];
                if (g == null || g.WasCollected) continue;
                string n = Discovery.Name(bi);
                if (n != "chara_image" && n != "charactor_image")
                {
                    var img = g.TryCast<Image>();
                    if (img == null || img.sprite == null || !PoseName.IsMatch(img.sprite.name)) continue;
                }
                var ctrl = g.GetComponentInParent<MenuCharacterController>();
                if (ctrl == null) continue;
                done ??= new HashSet<IntPtr>();
                if (done.Add(ctrl.Pointer)) ScanOne(ctrl);
            }
        }

        void Scan()
        {
            var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<MenuCharacterController>());
            if (found == null) return;
            foreach (var o in found)
            {
                var ctrl = o?.TryCast<MenuCharacterController>();
                if (ctrl != null) ScanOne(ctrl);
            }
        }

        void ScanOne(MenuCharacterController ctrl)
        {
            {
                if (ctrl == null || !ctrl.isActiveAndEnabled) return;
                string path = PathOf(ctrl.transform);
                var rule = Rules.For(path);
                if (Rules.Log && seenPaths.Add(path))
                {
                    Plugin.L.LogInfo($"[MenuLayout] slot '{path}' -> rule {(rule == null ? "none" : "'" + rule.Name + "'")}");
                    LogLines(ctrl.transform);
                    LogCharacterImages(ctrl);
                }
                if (rule == null) return;
                // The slot's own image plus any second character image under it (main menu: front/back row).
                var imgs = new List<Image>();
                var main = ctrl.CharacterImageView;
                if (main != null) imgs.Add(main);
                foreach (var img in ctrl.GetComponentsInChildren<Image>(true))
                    if (img.name == "chara_image" || img.name == "charactor_image" ||
                        (img.sprite != null && PoseName.IsMatch(img.sprite.name)))
                        imgs.Add(img);
                if (imgs.Count == 0 && Rules.Log && seenPaths.Add(path + "#noimg"))
                {
                    var names = new List<string>();
                    foreach (var img in ctrl.GetComponentsInChildren<Image>(true)) names.Add($"{img.name}({img.sprite?.name})");
                    Plugin.L.LogInfo($"[MenuLayout] '{rule.Name}': no character image found under the slot; images: {string.Join(", ", names)}");
                }
                foreach (var img in imgs)
                {
                    if (targets.ContainsKey(img.Pointer)) continue;
                    targets[img.Pointer] = new Target { Img = img, Rule = rule, Path = path };
                }
                if (rule.Hide.Count > 0) HideImages(ctrl.transform, rule);
            }
        }

        /// <summary>Disables images on the slot's screen whose path contains a hide entry's path (and, if given, use its sprite).</summary>
        static void HideImages(Transform slot, Rule rule)
        {
            Transform top = slot;
            for (int i = 0; i < 6 && top.parent != null; i++) top = top.parent;
            foreach (var img in top.GetComponentsInChildren<Image>(true))
            {
                if (!img.enabled) continue;
                string p = PathOf(img.transform);
                foreach (var (hp, hs) in rule.Hide)
                {
                    if (!p.Contains(hp)) continue;
                    if (hs != null && (img.sprite == null || img.sprite.name != hs)) continue;
                    img.enabled = false;
                    if (Rules.Log) Plugin.L.LogInfo($"[MenuLayout] '{rule.Name}': hid '{p}' ({img.sprite?.name})");
                }
            }
        }

        /// <summary>
        /// Logs the slot's CharacterImageView and every character-pose image on its screen (6 levels up), with active
        /// state and 1440p rectangle, to find which image is actually drawn.
        /// </summary>
        static void LogCharacterImages(MenuCharacterController ctrl)
        {
            var view = ctrl.CharacterImageView;
            Plugin.L.LogInfo($"[MenuLayout]   CharacterImageView: {(view == null ? "null" : "'" + view.name + "' sprite=" + view.sprite?.name + " active=" + view.isActiveAndEnabled + " " + Rect1440(view.rectTransform))}");
            Transform top = ctrl.transform;
            for (int i = 0; i < 6 && top.parent != null; i++) top = top.parent;
            foreach (var img in top.GetComponentsInChildren<Image>(true))
            {
                if (img.sprite == null || !PoseName.IsMatch(img.sprite.name)) continue;
                Plugin.L.LogInfo($"[MenuLayout]   pose image '{PathOf(img.transform).Replace(PathOf(top), top.name)}' sprite={img.sprite.name} active={img.isActiveAndEnabled} {Rect1440(img.rectTransform)}");
            }
        }

        /// <summary>Rectangle in 1440p screen px, y down from the top.</summary>
        static string Rect1440(RectTransform rt)
        {
            var canvas = rt.GetComponentInParent<Canvas>();
            if (canvas == null) return "(no canvas)";
            var root = canvas.rootCanvas;
            float pxPerUnit = Px.PerUnit(root);
            var c = Corners(rt);
            Vector3 a = root.transform.InverseTransformPoint(c[0]), b = root.transform.InverseTransformPoint(c[2]);
            return $"x {a.x * pxPerUnit + 1280:0}-{b.x * pxPerUnit + 1280:0} y {720 - b.y * pxPerUnit:0}-{720 - a.y * pxPerUnit:0}";
        }

        /// <summary>Logs the divider-line images on the slot's screen (6 levels up) with their 1440p y range.</summary>
        static void LogLines(Transform slot)
        {
            Transform top = slot;
            for (int i = 0; i < 6 && top.parent != null; i++) top = top.parent;
            foreach (var img in top.GetComponentsInChildren<Image>(false))
            {
                if (img.sprite == null || img.sprite.name.IndexOf("Line", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var canvas = img.canvas;
                if (canvas == null) continue;
                var root = canvas.rootCanvas;
                float pxPerUnit = Px.PerUnit(root);
                var c = Corners(img.rectTransform);
                Vector3 a = root.transform.InverseTransformPoint(c[0]), b = root.transform.InverseTransformPoint(c[2]);
                // canvas y is up from the centre; convert to y down from the top of a 1440p screen
                float half = 720f;
                Plugin.L.LogInfo($"[MenuLayout]   line '{PathOf(img.transform).Replace(PathOf(top), top.name)}' sprite={img.sprite.name} y {half - b.y * pxPerUnit:0}-{half - a.y * pxPerUnit:0} x {a.x * pxPerUnit + 1280:0}-{b.x * pxPerUnit + 1280:0}");
            }
        }

        static Il2CppStructArray<Vector3> Corners(RectTransform rt)
        {
            // GetWorldCorners fills the array in place: it must be an il2cpp array, a managed Vector3[] is copied.
            var c = new Il2CppStructArray<Vector3>(4);
            rt.GetWorldCorners(c);
            return c;
        }

        static void Restore(Target t)
        {
            if (!t.Applied || t.Img == null || t.Img.WasCollected) return;
            var rt = t.Img.rectTransform;
            rt.localScale = t.OrigScale;
            rt.localPosition = t.OrigPos;
            t.Applied = false;
        }

        void Apply(Target t)
        {
            var rt = t.Img.rectTransform;
            if (t.Applied)
            {
                bool posSame = rt.localPosition == t.SetPos, scaleSame = rt.localScale == t.SetScale;
                if (posSame && scaleSame) return;
                // The game moved or rescaled the image: its new value is the original, but only for what it changed.
                // Put our own change back on the other one first, or the offset is added again on every character
                // switch (status/magic sprites crept left and down; user 2026-11-05).
                if (posSame) rt.localPosition = t.OrigPos;
                if (scaleSame) rt.localScale = t.OrigScale;
                t.Applied = false;
            }
            var canvas = t.Img.canvas;
            if (canvas == null) return;
            var root = canvas.rootCanvas;
            var rootT = root.transform;
            // 1440p screen pixels -> root canvas units at the current resolution.
            float unitsPerPx = Px.Upp(root);

            var c = Corners(rt);
            Vector3 a = rootT.InverseTransformPoint(c[0]), b = rootT.InverseTransformPoint(c[2]);
            float h0 = b.y - a.y;
            if (h0 < 1f) return; // not laid out yet
            var bottom0 = new Vector3((a.x + b.x) / 2f, a.y, 0f);

            // Keep the layout system from moving the image back.
            var p = rt.parent;
            if (p != null && p.GetComponent<LayoutGroup>() != null && t.Img.GetComponent<LayoutElement>() == null)
                t.Img.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;

            t.OrigScale = rt.localScale;
            t.OrigPos = rt.localPosition;
            float s = t.Rule.Height > 0 ? t.Rule.Height * unitsPerPx / h0 : 1f;
            rt.localScale = new Vector3(t.OrigScale.x * s, t.OrigScale.y * s, t.OrigScale.z);

            c = Corners(rt);
            a = rootT.InverseTransformPoint(c[0]); b = rootT.InverseTransformPoint(c[2]);
            var bottom1 = new Vector3((a.x + b.x) / 2f, a.y, 0f);
            var want = bottom0 + new Vector3(t.Rule.MoveRight * unitsPerPx, -t.Rule.MoveDown * unitsPerPx, 0f);
            if (t.Rule.Top >= 0 && t.Rule.Height > 0)
            {
                // Absolute: feet at Top + Height (1440p px down from the screen top). Root canvas origin = screen centre.
                // C# 'as' does not work on il2cpp objects (always null); use GetComponent.
                float rootHalfHeightPx = root.GetComponent<RectTransform>().rect.height / 2f / unitsPerPx;
                want.y = (rootHalfHeightPx - (t.Rule.Top + t.Rule.Height)) * unitsPerPx;
            }
            var world = rootT.TransformVector(want - bottom1);
            rt.localPosition += p != null ? p.InverseTransformVector(world) : world;

            t.SetScale = rt.localScale;
            t.SetPos = rt.localPosition;
            t.Applied = true;
            if (Rules.Log)
            {
                float pxPerUnit = 1f / unitsPerPx;
                Plugin.L.LogInfo($"[MenuLayout] '{t.Rule.Name}' {t.Img.name}: height {h0 * pxPerUnit:0.#}px -> {(b.y - a.y) * pxPerUnit:0.#}px, " +
                                 $"feet y {(-bottom0.y) * pxPerUnit:0.#} -> {(-want.y) * pxPerUnit:0.#} (canvas centre = 0, 1440p px)");
            }
        }
    }
}
