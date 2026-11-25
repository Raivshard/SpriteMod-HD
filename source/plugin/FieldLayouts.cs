using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace FF5MenuSprites
{
    /// <summary>
    /// Map layout overrides (Tiled edits that move, add or remove tiles; "just keep the game directory
    /// layout. that's how everything else has worked"). A tilemap.json in the game's own path under the mod folder,
    /// e.g. Modules\00-Mods\SpriteMod HD\FF5\GameAssets\Serial\Res\Map\Map_20061\Map_20061_6\tilemap.json, same format as
    /// the game's file, replaces the cell data of the tile layers it shares with the game's map (matched by group and
    /// layer name and cell count). Everything else (properties such as the lava colours, events, collision) stays the
    /// game's, so other mods' colour changes (Color Corrected Tiles) are kept. Walkability is collision.json, not this.
    /// Hook: prefix on MapUtility.ParseMapTileGroupData(TileMapData, Int32, MapRange) [verified, interop dump; that it
    /// runs for every map load: unverified, logged]. Which file belongs to the map being parsed: same width/height and
    /// the most cells equal to the game's data (an edited layout still matches most of its original), at least half.
    /// Any error: logged, the map stays as the game has it.
    /// </summary>
    static class FieldLayouts
    {
        const string Tag = "[MapLayout] ";
        static string Root => Path.Combine(Path.GetDirectoryName(Plugin.ConfigPath) ?? "", "GameAssets", "Serial", "Res", "Map");

        sealed class Layout
        {
            public string Name, File;
            public DateTime Stamp;
            public int W, H;
            public readonly Dictionary<string, int[]> Layers = new Dictionary<string, int[]>(StringComparer.Ordinal);
        }
        static readonly Dictionary<string, Layout> cache = new Dictionary<string, Layout>(StringComparer.OrdinalIgnoreCase);

        internal static void Install(HarmonyLib.Harmony h)
        {
            var m = HarmonyLib.AccessTools.Method(typeof(MapUtility), nameof(MapUtility.ParseMapTileGroupData));
            h.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(FieldLayouts).GetMethod(nameof(Prefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
            Plugin.L.LogInfo(Tag + "MapUtility.ParseMapTileGroupData patched; overrides from " + Root);
        }

        static void Prefix(TileMapData __0)
        {
            try { Apply(__0); }
            catch (Exception e) { Plugin.L.LogWarning(Tag + "skipped, map unchanged: " + e.Message); }
        }

        static IEnumerable<Layout> Files()
        {
            if (!Directory.Exists(Root)) yield break;
            foreach (var f in Directory.GetFiles(Root, "tilemap.json", SearchOption.AllDirectories))
            {
                var t = File.GetLastWriteTimeUtc(f);
                if (!cache.TryGetValue(f, out var l) || l.Stamp != t)
                {
                    l = Read(f, t);
                    cache[f] = l;
                }
                if (l != null) yield return l;
            }
        }

        static Layout Read(string f, DateTime t)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
                var r = doc.RootElement;
                var l = new Layout { Name = Path.GetFileName(Path.GetDirectoryName(f)), File = f, Stamp = t,
                                     W = r.GetProperty("width").GetInt32(), H = r.GetProperty("height").GetInt32() };
                foreach (var g in r.GetProperty("layers").EnumerateArray())
                {
                    if (!g.TryGetProperty("layers", out var sub)) continue;
                    string gn = g.GetProperty("name").GetString();
                    foreach (var x in sub.EnumerateArray())
                    {
                        if (!x.TryGetProperty("data", out var d) || d.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                        var a = new int[d.GetArrayLength()]; int i = 0;
                        foreach (var v in d.EnumerateArray()) a[i++] = unchecked((int)v.GetInt64());   // flip flags in the top bits
                        l.Layers[gn + "/" + x.GetProperty("name").GetString()] = a;
                    }
                }
                Plugin.L.LogInfo(Tag + $"read {l.Name}: {l.W}x{l.H}, {l.Layers.Count} tile layer(s)");
                return l;
            }
            catch (Exception e) { Plugin.L.LogWarning(Tag + "not read (ignored): " + f + ": " + e.Message); return null; }
        }

        static void Apply(TileMapData map)
        {
            if (map == null) return;
            int w = map.width, h = map.height;
            // the game's tile layers
            var game = new List<(string key, TileMapLayerData layer, Il2CppStructArray<int> data)>();
            var groups = map.layers;
            if (groups != null)
                foreach (var g in groups)
                {
                    if (g == null || g.layers == null) continue;
                    foreach (var x in g.layers)
                    {
                        var d = x != null ? x.data : null;
                        if (d != null && d.Length > 0) game.Add((g.name + "/" + x.name, x, d));
                    }
                }
            Layout best = null; double bestScore = 0;
            foreach (var l in Files())
            {
                if (l.W != w || l.H != h) continue;
                long eq = 0, tot = 0;
                foreach (var (key, _, d) in game)
                {
                    if (!l.Layers.TryGetValue(key, out var a) || a.Length != d.Length) continue;
                    for (int i = 0; i < a.Length; i++) if (a[i] == d[i]) eq++;
                    tot += a.Length;
                }
                double s = tot > 0 ? (double)eq / tot : 0;
                if (s > bestScore) { bestScore = s; best = l; }
            }
            string names = string.Join(", ", game.ConvertAll(t => t.key));
            if (best == null || bestScore < 0.5)
            {
                Plugin.L.LogInfo(Tag + $"parse {w}x{h} [{names}]: no override" + (best != null ? $" (closest {best.Name}, {bestScore:P0} equal)" : ""));
                return;
            }
            int cells = 0, layers = 0;
            foreach (var (key, layer, d) in game)
            {
                if (!best.Layers.TryGetValue(key, out var a) || a.Length != d.Length) continue;
                int n = 0;
                for (int i = 0; i < a.Length; i++) if (a[i] != d[i]) n++;
                if (n == 0) continue;
                layer.data = new Il2CppStructArray<int>(a);
                cells += n; layers++;
            }
            Plugin.L.LogWarning(Tag + $"{best.Name}: {cells} cell(s) in {layers} layer(s) replaced from {best.File} " +
                                $"({bestScore:P1} equal before) (info, not an error)");
        }
    }
}
