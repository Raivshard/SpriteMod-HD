using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FF5MenuSprites
{
    /// <summary>
    /// HD map tile sheets. Each field map layer is a mesh (Last.Map MapLayerMesh) whose material draws one tile sheet,
    /// e.g. 'map_chip_147' 512x512 on all eight renderers RootObject/RootFiled/MapRenderer/MainViewMapRenderer/MainView/
    /// {Bottom,Ground,Upper,Ceil}Layer_{opaque,transparent} [verified, F8 dumps 2026-11-14/15]; the tile UVs come from
    /// MapTileGroupData.TexUVTable [verified, interop dump]. A larger sheet loaded by the game does not work [verified
    /// externally]. So the game keeps loading its own sheet (its UVs stay right) and the plugin then
    /// sets the material's texture to map_tiles/&lt;sheet name&gt;.hd.png beside MenuLayout.json. UVs are 0..1, so any size
    /// with the same layout fits (2x = 1024 px, 32 px tiles) [inferred; the test checks it].
    /// Point filter (no bleeding from neighbouring tiles). Files are re-read when the folder changes; the game's own
    /// sheet is put back first. No file = the game's sheet.
    /// </summary>
    static class FieldTiles
    {
        // Main view and the minimap both draw the map with the same sheet: .../MainViewMapRenderer/MainView/... and
        // .../MinimapViewMapRenderer/MainView/... [verified, F8 dump 2026-11-15 18:28]
        const string ViewPath = "RootObject/RootFiled/MapRenderer";
        static int resets, reloads, ticksNoView;
        static string Folder => Path.Combine(Path.GetDirectoryName(Plugin.ConfigPath) ?? "", "map_tiles");
        /// <summary>Sheet name -> HD texture (null = no usable file).</summary>
        static readonly Dictionary<string, Texture2D> hd = new Dictionary<string, Texture2D>();
        /// <summary>Material -> the game's own texture, for putting it back.</summary>
        static readonly Dictionary<IntPtr, KeyValuePair<Material, Texture>> orig = new Dictionary<IntPtr, KeyValuePair<Material, Texture>>();
        static readonly HashSet<IntPtr> ours = new HashSet<IntPtr>();
        static readonly HashSet<string> logged = new HashSet<string>();
        static float next;
        static long stamp = -1;
        static int files;
        static bool broken;

        static void Log(string s)
        {
            if (logged.Count < 300 && logged.Add(s)) Plugin.L.LogInfo("[MapTiles] " + s);   // details: BepInEx Info level
        }

        /// <summary>Re-read the folder when its files change: game sheets back on, HD textures dropped.</summary>
        static void Restamp()
        {
            string dir = Folder;
            long st = 0; int n = 0;
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, "*.png", SearchOption.AllDirectories)) { st = st * 31 + File.GetLastWriteTimeUtc(f).Ticks + new FileInfo(f).Length; n++; }
            if (st == stamp) return;
            bool first = stamp == -1;
            stamp = st; files = n;
            foreach (var kv in orig.Values)
                if (kv.Key != null && !kv.Key.WasCollected && kv.Value != null && !kv.Value.WasCollected) kv.Key.mainTexture = kv.Value;
            orig.Clear();
            foreach (var t in hd.Values) if (t != null && !t.WasCollected) UnityEngine.Object.Destroy(t);
            hd.Clear(); ours.Clear(); logged.Clear();
            LoadAnims(dir);
            if (!first || n > 0) Plugin.L.LogInfo($"[MapTiles] {n} sheet file(s) in {dir}");
        }

        /// <summary>
        /// The map package shown now, e.g. "map_20060", from the scene root RootObject/Map_20060 [verified, F8 dump
        /// 2026-11-15 18:28]; null if none. 23 sheet names have different art in different packages [verified, export],
        /// so map_tiles/&lt;package&gt;/&lt;sheet&gt;.hd.png wins over map_tiles/&lt;sheet&gt;.hd.png.
        /// </summary>
        /// Attempt 1 used GameObject.Find("RootObject") and found no package although the dump shows
        /// RootObject/Map_20060 (user: fail, 2026-11-15 18:59): more than one object is likely named RootObject
        /// [inferred]. Now the RootObject that holds the map renderer (RootObject/RootFiled/MapRenderer) is used.
        static string CurrentPackage(GameObject mapRenderer)
        {
            var t = mapRenderer.transform.parent != null ? mapRenderer.transform.parent.parent : null;
            if (t == null) return null;
            for (int i = 0; i < t.childCount; i++)
            {
                var n = t.GetChild(i).name;
                if (n.Length == 9 && n.StartsWith("Map_", StringComparison.Ordinal) && int.TryParse(n.Substring(4), out _))
                    return "map_" + n.Substring(4);
            }
            if (!childrenLogged)
            {
                childrenLogged = true;
                var names = new List<string>();
                for (int i = 0; i < t.childCount && i < 40; i++) names.Add(t.GetChild(i).name);
                Plugin.L.LogWarning($"[MapTiles] no Map_NNNNN under '{t.name}'; children: {string.Join(", ", names)}");
            }
            return null;
        }
        static string lastPkg = "(not checked yet)";
        static bool childrenLogged;

        static Texture2D Hd(string pkg, string name, int gameW, int gameH)
        {
            string key = (pkg ?? "") + "/" + name;
            if (hd.TryGetValue(key, out var have))
            {
                if (have == null || !have.WasCollected) return have;
                // the HD texture was destroyed under us: reload it (logged, max 20)
                if (++reloads <= 20) Plugin.L.LogWarning($"[MapTiles] HD texture for {name} was destroyed; reloading ({reloads})");
                hd.Remove(key);
            }
            Texture2D tex = null;
            // "<sheet>.hd.png", not "<sheet>.png": KupoUI loads any PNG under Modules whose name matches a game texture
            // [verified, log 2026-11-15: it loaded map_tiles\map_chip_147.png as the game's map_chip_147, 1024x1024],
            // which would mix its in-place replacement with this swap.
            string f = pkg != null ? Path.Combine(Folder, pkg, name + ".hd.png") : null;
            if (f == null || !File.Exists(f)) f = Path.Combine(Folder, name + ".hd.png");
            if (File.Exists(f))
            {
                try
                {
                    tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(f))) { Plugin.L.LogWarning("[MapTiles] not a readable PNG: " + f); UnityEngine.Object.Destroy(tex); tex = null; }
                    else if (tex.width * gameH != tex.height * gameW)
                    {
                        Plugin.L.LogWarning($"[MapTiles] {name}.png is {tex.width}x{tex.height}, the game's sheet {gameW}x{gameH}: not the same shape, game sheet kept");
                        UnityEngine.Object.Destroy(tex); tex = null;
                    }
                    else
                    {
                        if (tex.width % gameW != 0)
                            Plugin.L.LogWarning($"[MapTiles] {name}.png: {tex.width} px is not a whole multiple of {gameW}; tile edges fall between pixels");
                        tex.filterMode = FilterMode.Point;
                        tex.wrapMode = TextureWrapMode.Clamp;
                        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                        tex.name = name + "_HD";
                        ours.Add(tex.Pointer);
                    }
                }
                catch (Exception e) { Plugin.L.LogWarning("[MapTiles] could not load " + f + ": " + e.Message); tex = null; }
            }
            if (tex != null) Log($"{key}: using {Path.GetRelativePath(Folder, f)}");
            hd[key] = tex;
            return tex;
        }

        /// <summary>"MainViewMapRenderer/MainView/GroundLayer_opaque" (the last three levels).</summary>
        static string PathOf(Transform t)
        {
            var p = t.name;
            for (int i = 0; i < 2 && t.parent != null; i++) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }

        internal static void Tick()
        {
            if (broken || Time.unscaledTime < next) return;
            next = Time.unscaledTime + 0.5f;
            try
            {
                Restamp();
                if (files == 0) return;
                var view = GameObject.Find(ViewPath);
                if (view == null)
                {
                    if (++ticksNoView == 1) Plugin.L.LogInfo("[MapTiles] map renderer not found (" + ViewPath + "); waiting");
                    return;
                }
                if (ticksNoView > 0) { Plugin.L.LogInfo($"[MapTiles] map renderer found again after {ticksNoView} checks"); ticksNoView = 0; }
                viewGo = view;
                Swap(view);
            }
            catch (Exception e)
            {
                broken = true;
                Plugin.L.LogWarning("[MapTiles] stopped (game sheets stay): " + e.Message);
            }
        }

        static GameObject viewGo;

        // ---- tile animations the map data does not have (lava) ----
        // map_tiles/anim/anim.json: [{"sheet", "tid", "frames" (png: count frames of one tile side by side, stored
        // orientation), "count", "ms"}] from tools/map_tiles/tile_anim.py. Every frame step the current frame is copied
        // into the tile's place in the HD sheet on the GPU (Graphics.CopyTexture, region form [verified, interop dump]);
        // every cell using that tile shows it, the water shader included.
        // "clock": "lava" = the lava clock (MenuLayout fieldLavaAnimSpeed), anything else = real time (inn clock, user
        // 2026-11-20: its own sequence at the game's 250 ms; entries for the 4 tile ids the game switches between play in step)
        sealed class Anim { public string Sheet; public int Tid, Count, Ms, Last = -1; public bool Lava; public Texture2D Src; }
        static readonly List<Anim> anims = new List<Anim>();
        static bool animSeen;
        static readonly Dictionary<string, Texture2D> hdBySheet = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        static void LoadAnims(string dir)
        {
            foreach (var a in anims) if (a.Src != null && !a.Src.WasCollected) UnityEngine.Object.Destroy(a.Src);
            anims.Clear(); hdBySheet.Clear();
            string f = Path.Combine(dir, "anim", "anim.json");
            if (!File.Exists(f)) return;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    var a = new Anim { Sheet = e.GetProperty("sheet").GetString(), Tid = e.GetProperty("tid").GetInt32(),
                                       Count = e.GetProperty("count").GetInt32(), Ms = Math.Max(16, e.GetProperty("ms").GetInt32()),
                                       // entries written before "clock" existed were all lava
                                       Lava = !e.TryGetProperty("clock", out var ck) || ck.GetString() == "lava" };
                    string png = Path.Combine(dir, "anim", e.GetProperty("frames").GetString());
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!File.Exists(png) || !ImageConversion.LoadImage(tex, File.ReadAllBytes(png)) || tex.width < a.Count * 32 || tex.height != 32)
                    {
                        Plugin.L.LogWarning("[MapTiles] animation frames missing or wrong size: " + png);
                        UnityEngine.Object.Destroy(tex); continue;
                    }
                    tex.filterMode = FilterMode.Point; tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    a.Src = tex;
                    anims.Add(a);
                }
                Plugin.L.LogInfo($"[MapTiles] {anims.Count} tile animation(s) from {f}");
            }
            catch (Exception e) { anims.Clear(); Plugin.L.LogWarning("[MapTiles] anim.json not read (no tile animations): " + e.Message); }
        }

        static void Animate()
        {
            if (anims.Count == 0) return;
            if (!animSeen) { animSeen = true; Log($"animations running: {anims.Count}; HD sheets known: {string.Join(", ", hdBySheet.Keys)}"); }
            double lava = FieldLava.AnimClockMs();   // MenuLayout "fieldLavaAnimSpeed"
            double real = Time.unscaledTime * 1000.0;
            foreach (var a in anims)
            {
                if (!hdBySheet.TryGetValue(a.Sheet, out var dst) || dst == null || dst.WasCollected) continue;
                int k = (int)((long)((a.Lava ? lava : real) / a.Ms) % a.Count);
                if (k == a.Last) continue;
                int s = dst.width / 32;
                if (s != 32 || dst.format != a.Src.format)
                {
                    Plugin.L.LogWarning($"[MapTiles] animation {a.Sheet}:{a.Tid}: HD sheet {dst.width}px {dst.format} vs frames 32px {a.Src.format}; not animated");
                    continue;
                }
                int r = a.Tid / 32, c = a.Tid % 32;
                // PNG row r from the top = texture y from the bottom (Unity origin bottom-left)
                Graphics.CopyTexture(a.Src, 0, 0, k * 32, 0, 32, 32, dst, 0, 0, c * 32, dst.height - (r + 1) * 32);
                if (a.Last < 0) Log($"animation {a.Sheet}:{a.Tid}: first frame copied into '{dst.name}' {dst.width}x{dst.height} {dst.format} at ({c * 32},{dst.height - (r + 1) * 32})");
                a.Last = k;
            }
        }

        /// <summary>Every frame right before drawing (Canvas.willRenderCanvases): swap sheets the game just set up, so a new
        /// map never shows the game's sheet for the up to 0.5 s until the next Tick ("the tileset shows the
        /// old one for a split-second before switching"). Files are still checked in Tick only.</summary>
        internal static void PreRender()
        {
            if (broken || files == 0) return;
            try
            {
                if (viewGo == null || viewGo.WasCollected) return;
                Swap(viewGo);
                Animate();
            }
            catch (Exception e)
            {
                broken = true;
                Plugin.L.LogWarning("[MapTiles] stopped (game sheets stay): " + e.Message);
            }
        }

        static void Swap(GameObject view)
        {
            {
                string pkg = CurrentPackage(view);
                if (pkg != lastPkg) { lastPkg = pkg; Plugin.L.LogInfo($"[MapTiles] map package now {pkg ?? "(none)"}"); }
                foreach (var r in view.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var m = r.sharedMaterial;
                    if (m == null) continue;
                    var t = m.mainTexture;
                    if (t == null || ours.Contains(t.Pointer)) continue;
                    var h = Hd(pkg, t.name, t.width, t.height);
                    if (h == null) continue;
                    // a material we had already swapped shows the game's sheet again: the game (or another mod) set it
                    // back. Logged (max 30) to find out what undoes the swap; it is swapped again here.
                    if (orig.ContainsKey(m.Pointer) && ++resets <= 30)
                        Plugin.L.LogInfo($"[MapTiles] '{PathOf(r.transform)}': sheet set back to {t.name} {t.width}x{t.height} (reset {resets}); swapping again");
                    orig[m.Pointer] = new KeyValuePair<Material, Texture>(m, t);
                    m.mainTexture = h;
                    hdBySheet[t.name] = h;
                    Log($"'{PathOf(r.transform)}': {t.name} {t.width}x{t.height} -> {h.width}x{h.height} (x{(float)h.width / t.width:0.##})");
                }
            }
        }
    }
}
