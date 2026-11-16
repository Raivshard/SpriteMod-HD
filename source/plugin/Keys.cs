using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FF5MenuSprites
{
    /// <summary>
    /// Unknown-key warnings for MenuLayout files. JSON keys are case-sensitive and an unknown key is silently ignored by
    /// the parser, so a typo ("fontsclae", "FontScale") used to do nothing without a trace. Each check logs one warning
    /// per unknown key with the closest known key. Keys starting with "_" are notes and never warned about.
    /// Keep these lists in step with the parser (Plugin.cs Rules.Refresh, TextStyle.Parse) and MenuLayout.schema.json.
    /// </summary>
    static class Keys
    {
        internal static readonly string[] Top = {
            "$schema", "log", "dumpKey", "perf", "profileGame", "characterNames", "cleanup", "kupoTextureCache", "fieldSprites", "fieldSpritesDebug", "fieldStrideDelay",
            "kupoSkipUnreadable", "kupoFastUnreadable", "textureLimit", "screens", "styles", "elements", "textStyles", "renderHeights",
            "battle", "loadingIcon", "titleHighlight" };
        internal static readonly string[] AddOnTop = { "$schema", "screens", "elements", "textStyles", "styles" };
        static readonly string[] Screen = { "name", "match", "height", "moveDown", "moveRight", "top", "hide" };
        static readonly string[] Hide = { "path", "sprite" };
        static readonly string[] TextStyleKeys = { "fontStyle", "lineSpacing", "alignment", "shadow", "outline" };
        static readonly string[] Element = new[] {
            "name", "style", "path", "sprite", "within", "text", "fontType", "nth", "battleOnly", "moveDown", "moveRight", "fontSize", "fontScale",
            "fontSizeMax", "fontSizeMin", "spacing", "font", "width", "widen", "gap", "below", "centerOver", "color",
            "onTop", "scale", "height", "addSpritesDown", "radius", "selected", "greyed", "padding", "backdrop", "alpha", "hide", "image", "listPadding", "listAlign" }
            .Concat(TextStyleKeys).ToArray();
        static readonly string[] TextStyle = new[] { "name", "fontType" }.Concat(TextStyleKeys).ToArray();
        static readonly string[] State = { "color", "scale", "fontScale", "font" };
        static readonly string[] Effect = { "color", "distance", "blur" };
        static readonly string[] Backdrop = { "color", "merge", "radius", "padding" };
        static readonly string[] Battle = { "fitBackground", "spritesDown", "spriteCameras", "resolutionScale", "hdPixelsPerUnit", "customScale", "miniScale", "enemyOnTop", "partyRight", "partyDown", "partySlots", "backRowShift", "depthScale", "groundShadow" };
        static readonly string[] GroundShadow = { "on", "azimuth", "elevation", "opacity", "groundTilt", "hideGameShadows", "thickness", "depthRange", "blur", "falloff", "debug" };
        static readonly string[] Loading = { "frameTime", "size", "priority" };
        static readonly string[] Title = { "color", "scale", "fadeTime" };
        static readonly string[] Cleanup = { "skipGcCollect", "unloadEveryMinutes" };

        /// <summary>Checks the main file's top level and its fixed blocks (battle, loadingIcon, titleHighlight, cleanup).</summary>
        internal static void CheckMainTop(JsonElement root)
        {
            Check(root, Top, "MenuLayout.json");
            Block(root, "battle", Battle, "MenuLayout.json battle");
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("battle", out var bt))
                Block(bt, "groundShadow", GroundShadow, "MenuLayout.json battle groundShadow");
            Block(root, "loadingIcon", Loading, "MenuLayout.json loadingIcon");
            Block(root, "titleHighlight", Title, "MenuLayout.json titleHighlight");
            Block(root, "cleanup", Cleanup, "MenuLayout.json cleanup");
        }

        /// <summary>An element rule as written (before styles are merged in), or a named style's body.</summary>
        internal static void CheckElement(JsonElement e, string where)
        {
            Check(e, Element, where);
            Block(e, "selected", State, where + " selected");
            Block(e, "greyed", State, where + " greyed");
            Block(e, "shadow", Effect, where + " shadow");
            Block(e, "outline", Effect, where + " outline");
            Block(e, "backdrop", Backdrop, where + " backdrop");
        }

        internal static void CheckScreen(JsonElement s, string where)
        {
            Check(s, Screen, where);
            if (s.TryGetProperty("hide", out var h) && h.ValueKind == JsonValueKind.Array)
                foreach (var x in h.EnumerateArray()) Check(x, Hide, where + " hide");
        }

        internal static void CheckTextStyle(JsonElement t, string where)
        {
            Check(t, TextStyle, where);
            Block(t, "shadow", Effect, where + " shadow");
            Block(t, "outline", Effect, where + " outline");
        }

        /// <summary>Top level of an add-on: main-file-only keys get their own message, the rest as unknown.</summary>
        internal static void CheckAddOnTop(JsonElement root, string label)
        {
            foreach (var p in root.EnumerateObject())
            {
                if (p.Name.StartsWith("_") || Array.IndexOf(AddOnTop, p.Name) >= 0) continue;
                if (Array.IndexOf(Top, p.Name) >= 0)
                    Plugin.L.LogWarning($"[Keys] {label}: '{p.Name}' only works in MenuLayout.json (add-ons take screens, elements, textStyles, styles); ignored");
                else Unknown(p.Name, AddOnTop, label);
            }
        }

        static void Block(JsonElement parent, string key, string[] known, string where)
        {
            if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var b) && b.ValueKind == JsonValueKind.Object)
                Check(b, known, where);
        }

        static void Check(JsonElement obj, string[] known, string where)
        {
            if (obj.ValueKind != JsonValueKind.Object) return;
            foreach (var p in obj.EnumerateObject())
                if (!p.Name.StartsWith("_") && Array.IndexOf(known, p.Name) < 0) Unknown(p.Name, known, where);
        }

        static void Unknown(string key, string[] known, string where)
        {
            string hint = "";
            var same = known.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (same != null) hint = $" (capitals matter: '{same}')";
            else
            {
                string best = null; int bestD = int.MaxValue;
                foreach (var k in known)
                {
                    int d = Distance(key.ToLowerInvariant(), k.ToLowerInvariant());
                    if (d < bestD) { bestD = d; best = k; }
                }
                if (best != null && bestD <= Math.Max(2, best.Length / 3)) hint = $" (did you mean '{best}'?)";
            }
            Plugin.L.LogWarning($"[Keys] {where}: unknown key '{key}'{hint}; ignored. Start a key with _ to keep it as a note");
        }

        /// <summary>Edit distance with swaps of neighbours counted as one edit.</summary>
        static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                {
                    int c = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + c);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            return d[a.Length, b.Length];
        }
    }
}
