using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FF5MenuSprites
{
    /// <summary>
    /// Shared named styles (MenuLayout top-level "styles": { "name": { ...element keys... } }).
    /// An element rule with "style": "name" (or ["a", "b"]) gets the style's keys first, later styles over earlier,
    /// and the rule's own keys over all of them. Blocks (selected, greyed, shadow, outline, backdrop) merge key by key,
    /// so a rule can change only selected.color. A key repeated in one object: the last one counts (same as before).
    /// The merged entry is handed to the normal element parser, so every element key works in a style.
    /// </summary>
    static class SharedStyles
    {
        static readonly HashSet<string> Blocks = new HashSet<string> { "selected", "greyed", "shadow", "outline", "backdrop" };

        internal static JsonElement Merge(JsonElement e, Dictionary<string, JsonElement> styles, List<JsonDocument> docs)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("style", out var s)) return e;
            var names = new List<string>();
            if (s.ValueKind == JsonValueKind.String) names.Add(s.GetString());
            else if (s.ValueKind == JsonValueKind.Array)
            {
                foreach (var n in s.EnumerateArray())
                    if (n.ValueKind == JsonValueKind.String) names.Add(n.GetString());
                    else throw new FormatException("style must be a name or a list of names");
            }
            else throw new FormatException("style must be a name or a list of names");

            var layers = new List<JsonElement>();
            foreach (var n in names)
            {
                if (string.IsNullOrEmpty(n)) continue;
                if (styles.TryGetValue(n, out var st)) layers.Add(st);
                else Plugin.L.LogWarning($"[Styles] rule '{(e.TryGetProperty("name", out var rn) ? rn.GetString() : "?")}': no style named '{n}' (rule applied without it)");
            }
            if (layers.Count == 0) return e;
            layers.Add(e);

            var order = new List<string>();
            var vals = new Dictionary<string, JsonElement>();
            var blocks = new Dictionary<string, List<JsonElement>>();
            foreach (var layer in layers)
                foreach (var p in layer.EnumerateObject())
                {
                    if (p.Name == "style") continue;
                    if (!order.Contains(p.Name)) order.Add(p.Name);
                    if (Blocks.Contains(p.Name) && p.Value.ValueKind == JsonValueKind.Object)
                    {
                        vals.Remove(p.Name);
                        if (!blocks.TryGetValue(p.Name, out var bl)) blocks[p.Name] = bl = new List<JsonElement>();
                        bl.Add(p.Value);
                    }
                    else { vals[p.Name] = p.Value; blocks.Remove(p.Name); }
                }

            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                foreach (var k in order)
                {
                    if (vals.TryGetValue(k, out var v)) { w.WritePropertyName(k); v.WriteTo(w); }
                    else if (blocks.TryGetValue(k, out var bl))
                    {
                        var io = new List<string>();
                        var iv = new Dictionary<string, JsonElement>();
                        foreach (var b in bl)
                            foreach (var p in b.EnumerateObject()) { if (!io.Contains(p.Name)) io.Add(p.Name); iv[p.Name] = p.Value; }
                        w.WriteStartObject(k);
                        foreach (var ik in io) { w.WritePropertyName(ik); iv[ik].WriteTo(w); }
                        w.WriteEndObject();
                    }
                }
                w.WriteEndObject();
            }
            var doc = JsonDocument.Parse(ms.ToArray());
            docs.Add(doc);
            return doc.RootElement;
        }
    }
}
