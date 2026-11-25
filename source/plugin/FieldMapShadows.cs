using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FF5MenuSprites
{
    /// <summary>
    /// Map-level shadows (light from the lower left, approved on the offline simulation
    /// tools/map_tiles/map_shadow_sim.py, page karnak_mapshadow7). Per field map a mask
    /// map_shadows/&lt;Map_NNNNN[_n]&gt;.png beside MenuLayout.json: black, alpha = shadow, 32 px per map cell, top row of
    /// the PNG = map row 0 (as in tilemap.json / collision.json). The mask is already zero wherever an object, upper or
    /// ceiling tile covers the floor, so drawing it over the finished map gives the approved look [verified offline:
    /// max difference 1/255 on all 7 Karnak interiors].
    /// How it is drawn: one SpriteRenderer under the map's root object RootObject/&lt;Map name&gt; (hidden and destroyed with
    /// the map), layer of the main-view GroundLayer_opaque (8, drawn by CameraTileMap [verified, F8 dump 2026-11-17
    /// 04:12: camera mask includes 8, the minimap camera does not]), render queue 2451: after the opaque tile meshes
    /// (2450), before characters (2455) [verified, same dump], so characters are not darkened. z = 20: nearer than every
    /// tile (ceiling 46-50, ground 250, bottom 350) and in front of the camera (z -50); the sprite material does not
    /// write depth, so nothing else is affected [inferred].
    /// Placement, every frame before drawing: Last.Map.FieldController.ConvertCellPositionToWorldPosition(cell) [verified,
    /// interop dump] then Last_Map_IMapAccessor_ConvertWorldToVisualPosition (the map scrolls under a fixed camera
    /// [verified, F8 dumps: camera at 0,0; entities shift together]). Whether a cell position is the cell's centre or
    /// corner is read from the main-view tile mesh bounds (tile edges); the first values per map are logged.
    /// The controller instance is taken from a postfix on FieldController.UpdateCamera / MapSetup / MapViewSetup.
    /// MenuLayout "fieldMapShadows": false switches it off. Any error: off, logged once, the map stays as the game draws it.
    /// </summary>
    static class FieldMapShadows
    {
        internal static bool Enabled = true;
        const string Tag = "[MapShadow] ";
        const float Cell = 16f, PxPerUnit = 2f, Z = 20f;
        const int Queue = 2451;
        // floor-layer tiles with see-through pixels (stools, rug edges) are drawn by the game's *_transparent meshes at
        // queue 2456, after the overlay, so they hid the shadow ("chair ... completely blocking a shadow").
        // <map>.decor.png = the shadow on exactly those tiles' pixels, drawn after them [queue 2456 verified, F8 dump]
        const int DecorQueue = 2457;
        // map objects: after the see-through tile meshes (2456) and the decor shadow, else a see-through floor tile behind
        // the object is painted over it (2026-11-21 test: the inn clock's lower half hidden by the carpet at queue 2451).
        // Tiles nearer the camera still cover the object through the depth test (opaque layers write depth).
        const int ObjQueue = 2458;
        static string Folder => Path.Combine(Path.GetDirectoryName(Plugin.ConfigPath) ?? "", "map_shadows");
        static string ObjFolder => Path.Combine(Path.GetDirectoryName(Plugin.ConfigPath) ?? "", "map_objects");

        static Last.Map.FieldController ctrl;
        static Transform mapRoot;          // RootObject/<Map name>
        static string shown;               // map the overlay belongs to
        static GameObject go;
        static SpriteRenderer sr;
        static Texture2D tex, tex2;
        static Sprite sprite, sprite2;
        static int cellsW, cellsH;
        static MeshRenderer groundMesh;    // main-view GroundLayer_opaque: its bounds lie on tile edges
        static bool broken, placedLogged;
        static float nextLook;
        static readonly HashSet<string> logged = new HashSet<string>();

        static void Log(string s) { if (logged.Count < 200 && logged.Add(s)) Plugin.L.LogInfo(Tag + s); }   // details: BepInEx Info level
        static void Warn(string s) { if (logged.Count < 200 && logged.Add(s)) Plugin.L.LogWarning(Tag + s); }

        internal static void Install(HarmonyLib.Harmony h)
        {
            var t = typeof(Last.Map.FieldController);
            var post = new HarmonyLib.HarmonyMethod(typeof(FieldMapShadows).GetMethod(nameof(Capture),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));
            int n = 0;
            foreach (var name in new[] { "UpdateCamera", "MapSetup", "MapViewSetup" })
            {
                try { h.Patch(HarmonyLib.AccessTools.Method(t, name), postfix: post); n++; }
                catch (Exception e) { Plugin.L.LogWarning(Tag + "could not patch FieldController." + name + ": " + e.Message); }
            }
            Plugin.L.LogInfo(Tag + n + " controller patch(es) applied");
        }

        static void Capture(Last.Map.FieldController __instance)
        {
            try { if (__instance != null) ctrl = __instance; } catch { }
        }

        /// <summary>Right before drawing (Canvas.willRenderCanvases, after all game updates).</summary>
        internal static void PreRender()
        {
            if (broken) return;
            try
            {
                string map = CurrentMap();   // objects show even with fieldMapShadows off; Build checks Enabled
                if (map != shown) { Drop(); shown = map; if (map != null) Build(map); placedLogged = false; }
                if (go == null || go.WasCollected) return;
                Place();
            }
            catch (Exception e)
            {
                broken = true;
                Plugin.L.LogWarning(Tag + "stopped, maps show without the extra shadows: " + e);
                try { Drop(); } catch { }
            }
        }

        /// <summary>Name of the field map shown now (child Map_... of the RootObject that holds RootFiled), or null.</summary>
        static string CurrentMap()
        {
            if (mapRoot != null && !mapRoot.WasCollected && mapRoot.gameObject.activeInHierarchy) return mapRoot.name;
            mapRoot = null;
            if (Time.unscaledTime < nextLook) return null;
            nextLook = Time.unscaledTime + 0.25f;
            var view = GameObject.Find("RootObject/RootFiled/MapRenderer");
            var root = view != null && view.transform.parent != null ? view.transform.parent.parent : null;
            for (int i = 0; root != null && i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                if (c.name.StartsWith("Map_", StringComparison.Ordinal) && c.gameObject.activeInHierarchy) { mapRoot = c; return c.name; }
            }
            return null;
        }

        static void Build(string map)
        {
            string f = Path.Combine(Folder, map + ".png");
            string lj = Path.Combine(Folder, map + ".lava.json");
            bool hasMask = Enabled && File.Exists(f), hasLava = Enabled && File.Exists(lj);
            string oj = Path.Combine(ObjFolder, map + ".json");
            bool hasObj = File.Exists(oj);
            if (!hasMask && !hasLava && !hasObj) return;
            go = new GameObject("SpriteModMapShadow");
            go.transform.SetParent(mapRoot, false);
            var ground0 = GameObject.Find("RootObject/RootFiled/MapRenderer/MainViewMapRenderer/MainView/GroundLayer_opaque");
            groundMesh = ground0 != null ? ground0.GetComponent<MeshRenderer>() : null;
            go.layer = ground0 != null ? ground0.layer : 8;
            if (hasLava) BuildLava(map, lj);
            if (hasObj) BuildObjects(map, oj);
            if (!hasMask) return;
            tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(f)))
            {
                Plugin.L.LogWarning(Tag + "not a readable PNG: " + f);
                UnityEngine.Object.Destroy(tex); tex = null; return;
            }
            tex.filterMode = FilterMode.Point;          // pixel-exact with the 32 px tiles
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = "SpriteModMapShadow_" + map;
            cellsW = tex.width / 32; cellsH = tex.height / 32;
            // pivot top-left: the sprite's corner is the map's top-left corner
            sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0f, 1f), PxPerUnit, 0, SpriteMeshType.FullRect);
            sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.material.renderQueue = Queue;
            Log($"{map}: mask {tex.width}x{tex.height} ({cellsW}x{cellsH} cells), layer {go.layer}, queue {Queue}");
            string f2 = Path.Combine(Folder, map + ".decor.png");
            if (File.Exists(f2))
            {
                tex2 = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (ImageConversion.LoadImage(tex2, File.ReadAllBytes(f2)))
                {
                    tex2.filterMode = FilterMode.Point; tex2.wrapMode = TextureWrapMode.Clamp; tex2.name = "SpriteModMapShadowDecor_" + map;
                    sprite2 = Sprite.Create(tex2, new Rect(0, 0, tex2.width, tex2.height), new Vector2(0f, 1f), PxPerUnit, 0, SpriteMeshType.FullRect);
                    var g2 = new GameObject("SpriteModMapShadowDecor");
                    g2.transform.SetParent(go.transform, false);
                    g2.layer = go.layer;
                    var r2 = g2.AddComponent<SpriteRenderer>();
                    r2.sprite = sprite2;
                    r2.material.renderQueue = DecorQueue;
                    Log($"{map}: decor mask {tex2.width}x{tex2.height}, queue {DecorQueue}");
                }
                else { UnityEngine.Object.Destroy(tex2); tex2 = null; Plugin.L.LogWarning(Tag + "not a readable PNG: " + f2); }
            }
        }

        // ---- map objects (tile objects placed in Tiled, e.g. the inn clock with its own animation;
        // "animations on a given tile need to be directly under the user's control") ----
        // map_objects/<map>.json from tools/map_tiles/tiled_objects.py: {"width", "height" (cells), "atlas" (png, frames
        // upright), "objects": [{"x", "y" (Tiled pixels, 32 per cell, object's bottom-left), "w", "h", "z",
        // "frames": [[atlas x, atlas y (top-left, px)], ...], "ms": [duration per frame]}]}. Drawn like the lava layer:
        // SpriteRenderers under the map root, pivot bottom-left, render queue ObjQueue 2458 (after all tile meshes,
        // before characters), depth z from the Tiled layer order (layers at 350 Bottom, 250 Ground, 150 Upper, 50 Ceil
        // [verified, F8]; an object layer between Ground and Upper = 200: over the ground, behind upper tiles, characters
        // drawn after it). Animation in real time with the per-frame durations from Tiled.
        // Objects above Ground (z 200) are sorted with the characters instead (2026-11-21 test: fire and clock covered the
        // player): the game puts each character at z = c + y/8 (y = its world y; lower on screen = nearer) [verified, F8
        // screen_20261121_143105: NPCs at y -54/-22/-6/42 -> z 241/245/247/253; the player 0.5 nearer]. Every frame the
        // object gets the z of a character standing half a cell above its bottom edge, measured from the player, so a
        // character in the row below is drawn in front and one in the row of the object or above it behind.
        // Blend: "mode" (Tiled layer blend mode, SpriteMod/ObjectBlend _Mode; 0 = normal, plain sprite material) and
        // "opacity" (layer x object) in the json.
        sealed class MapObj { public SpriteRenderer R; public Sprite[] Frames; public int[] Ms; public int Total, Shown = -1; public bool Sort; }
        static readonly List<MapObj> objs = new List<MapObj>();
        static Texture2D objTex;
        static Shader objShader; static bool objShaderTried;
        static readonly List<Material> objMats = new List<Material>();
        static Transform player;
        static float groundZ = float.NaN, upperZ = float.NaN;
        static Transform groundT, upperT;
        static int depthLogs;
        static readonly List<Sprite> objSprites = new List<Sprite>();   // shared between objects: destroyed once

        static void BuildObjects(string map, string json)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
                var r = doc.RootElement;
                if (cellsW == 0) { cellsW = r.GetProperty("width").GetInt32(); cellsH = r.GetProperty("height").GetInt32(); }
                string png = Path.Combine(ObjFolder, r.GetProperty("atlas").GetString());
                objTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!File.Exists(png) || !ImageConversion.LoadImage(objTex, File.ReadAllBytes(png)))
                {
                    Plugin.L.LogWarning(Tag + "object atlas missing or not a PNG: " + png);
                    UnityEngine.Object.Destroy(objTex); objTex = null; return;
                }
                objTex.filterMode = FilterMode.Point; objTex.wrapMode = TextureWrapMode.Clamp;
                var cache = new Dictionary<(int, int, int, int), Sprite>();
                int n = 0;
                foreach (var o in r.GetProperty("objects").EnumerateArray())
                {
                    int w = o.GetProperty("w").GetInt32(), h = o.GetProperty("h").GetInt32();
                    var frames = new List<Sprite>(); var ms = new List<int>();
                    foreach (var fr in o.GetProperty("frames").EnumerateArray())
                    {
                        int ax = fr[0].GetInt32(), ay = fr[1].GetInt32();
                        var key = (ax, ay, w, h);
                        if (!cache.TryGetValue(key, out var sp))
                            { cache[key] = sp = Sprite.Create(objTex, new Rect(ax, objTex.height - ay - h, w, h), Vector2.zero, PxPerUnit, 0, SpriteMeshType.FullRect); objSprites.Add(sp); }
                        frames.Add(sp);
                    }
                    foreach (var d in o.GetProperty("ms").EnumerateArray()) ms.Add(Math.Max(16, d.GetInt32()));
                    if (frames.Count == 0) continue;
                    float z = o.GetProperty("z").GetSingle();
                    var g = new GameObject("SpriteModMapObject");
                    g.transform.SetParent(go.transform, false);
                    // Tiled pixels (32 per cell) -> units (16 per cell); y down in Tiled = -y here (the root is the map's top-left)
                    g.transform.localPosition = new Vector3(o.GetProperty("x").GetSingle() / 2f, -o.GetProperty("y").GetSingle() / 2f, z - Z);
                    g.layer = go.layer;
                    var sr2 = g.AddComponent<SpriteRenderer>();
                    sr2.sprite = frames[0];
                    int mode = o.TryGetProperty("mode", out var mp) ? mp.GetInt32() : 0;
                    float op = o.TryGetProperty("opacity", out var opp) ? opp.GetSingle() : 1f;
                    sr2.color = new Color(1f, 1f, 1f, Mathf.Clamp01(op));
                    if (mode != 0)
                    {
                        if (!objShaderTried) { objShaderTried = true; objShader = GpuShadow.LoadShader("assets/shaders/objectblend.shader"); }
                        if (objShader != null)
                        {
                            var bm = new Material(objShader) { name = "SpriteModObjectBlend", renderQueue = ObjQueue };
                            bm.SetFloat("_Mode", mode);
                            objMats.Add(bm);
                            sr2.sharedMaterial = bm;
                        }
                        else Plugin.L.LogWarning(Tag + $"object '{o.GetProperty("name").GetString()}': blend mode {mode} needs SpriteMod/ObjectBlend (shader bundle); drawn normal");
                    }
                    if (sr2.sharedMaterial == null || sr2.sharedMaterial.name != "SpriteModObjectBlend") sr2.material.renderQueue = ObjQueue;
                    var mo = new MapObj { R = sr2, Frames = frames.ToArray(), Ms = ms.Count == frames.Count ? ms.ToArray() : null, Sort = Mathf.Approximately(z, 200f) };
                    if (mode != 0 || mo.Sort) Log($"{map}: object '{o.GetProperty("name").GetString()}' mode {mode} opacity {op}{(mo.Sort ? ", sorted with characters" : "")}");
                    if (mo.Ms != null) foreach (var d in mo.Ms) mo.Total += d;
                    objs.Add(mo);
                    n++;
                }
                Log($"{map}: {n} map object(s) from {Path.GetFileName(json)}, atlas {objTex.width}x{objTex.height}, queue {ObjQueue}");
            }
            catch (Exception e) { Plugin.L.LogWarning(Tag + "map objects not built: " + e.Message); }
        }

        static void AnimateObjects()
        {
            if (objs.Count == 0) return;
            long now = (long)(Time.unscaledTime * 1000.0);
            if (player == null || player.WasCollected)
            {
                var pg = GameObject.Find("RootObject/FieldPlayer(Clone)/VisualParent/SerialOffset/Offset/BirdOffset/VisualInstanceBody");
                player = pg != null ? pg.transform : null;
            }
            if (groundT == null || groundT.WasCollected || upperT == null || upperT.WasCollected)
            {
                // the main view's tile layers (world z; Ground 248, Upper 148 in the inn [verified, F8 2026-11-23])
                var gl = GameObject.Find("RootObject/RootFiled/MapRenderer/MainViewMapRenderer/MainView/GroundLayer_transparent");
                var ul = GameObject.Find("RootObject/RootFiled/MapRenderer/MainViewMapRenderer/MainView/UpperLayer_transparent");
                groundT = gl != null ? gl.transform : null; upperT = ul != null ? ul.transform : null;
            }
            // read every frame: read once at map load it was 254/154, later 248/148 (2026-11-23 F8; embers left behind
            // the Ground layer, blend flickered while walking)
            if (groundT != null && upperT != null)
            {
                float g = groundT.position.z, u = upperT.position.z;
                if (g != groundZ || u != upperZ) { groundZ = g; upperZ = u; if (depthLogs++ < 6) Log($"object depth range: Ground {groundZ}, Upper {upperZ}"); }
            }
            foreach (var o in objs)
            {
                if (o.Sort && player != null && o.R != null && !o.R.WasCollected)
                {
                    var pp = player.position; var tr = o.R.transform; var p = tr.position;
                    // player z = c + y/8 - 0.5 (NPCs: c + y/8); object = a character at its bottom edge + half a cell
                    float oz = pp.z + 0.5f + (p.y + Cell / 2f - pp.y) / 8f;
                    // always between Ground and Upper (2026-11-23 test: within a tile of the fireplace the player's z moved
                    // the embers behind the Ground hearth tiles: frames gone, blend flickering)
                    if (!float.IsNaN(groundZ)) oz = Mathf.Clamp(oz, upperZ + 0.5f, groundZ - 0.5f);
                    if (Mathf.Abs(p.z - oz) > 0.001f) tr.position = new Vector3(p.x, p.y, oz);
                }
                if (o.Ms == null || o.Frames.Length < 2 || o.R == null || o.R.WasCollected) continue;
                long t = now % o.Total;
                int k = 0;
                while (k < o.Ms.Length - 1 && t >= o.Ms[k]) { t -= o.Ms[k]; k++; }
                if (k != o.Shown) { o.R.sprite = o.Frames[k]; o.Shown = k; }
            }
        }

        static void DropObjects()
        {
            foreach (var s in objSprites)
                if (s != null && !s.WasCollected) UnityEngine.Object.Destroy(s);
            objSprites.Clear(); objs.Clear();
            foreach (var m in objMats)
                if (m != null && !m.WasCollected) UnityEngine.Object.Destroy(m);
            objMats.Clear(); player = null; groundZ = upperZ = float.NaN; groundT = upperT = null;
            if (objTex != null && !objTex.WasCollected) UnityEngine.Object.Destroy(objTex);
            objTex = null;
        }

        // ---- lava layer ("work around it. I don't want that key color influencing anything") ----
        // The game's lava water shader only draws on the floor tile's flat key colour, so the floor keeps it (fully
        // covered by the water, never seen). The animated mobile lava (mc39_0, 16 frames, 2 px left per frame,
        // seamless) is drawn as its own layer over every lava floor cell listed in <map>.lava.json, partly see-through
        // (MenuLayout "fieldLavaOpacity"): lava over water at x = water over lava at 1 - x. Depth: z 300, between the
        // floor (350) and the ground layer (250) [verified, F8 dumps]; the sprite material tests depth and does not
        // write it [inferred, Sprites/Default], so the stone pieces in front keep covering it. Queue 2451.
        internal static float LavaOpacity = 0.5f;
        // MenuLayout "fieldLavaBlend" (water on top with a burn / linear blend or "blend if" ranges):
        // shader SpriteMod/LavaBlend (ff5shadow.bundle) takes the water as drawn (screen grab) as the top layer and the lava
        // frame as the bottom layer. Mode 0 normal .. 7 soft light; ranges 0..255 as in Photoshop's blend-if sliders.
        internal static int BlendMode = 4;
        internal static float BlendOpacity = 1f;
        internal static Vector4 WaterRange = new Vector4(0, 0, 255, 255), LavaRange = new Vector4(0, 0, 255, 255);
        // MenuLayout "fieldLavaEdge" ("a slightly brighter edge to the top of the frame, something that
        // mimics the brighter spots of the shader"): the user's mask <map_shadows>\lava_edge.png (32 px, its own colours,
        // alpha = coverage) on every lava cell without a lava cell north of it, blended over the result by the same shader.
        internal static int EdgeMode = 0;
        internal static float EdgeOpacity = 1f;
        internal static Color EdgeTint = new Color(1f, 1f, 1f, 0f);   // alpha 0 = no tint
        internal static Vector4 EdgeWater = new Vector4(0, 0, 255, 255), EdgeLava = new Vector4(0, 0, 255, 255);
        static Texture2D edgeTex;
        static readonly List<bool> lavaEdge = new List<bool>();
        internal static int BlendVersion;
        static int blendApplied = -1;
        static Material lavaMat;
        static bool lavaShaderTried;
        static Shader lavaShader;
        const float LavaZ = 300f;
        static Texture2D lavaTex;
        static Sprite[] lavaFrames;
        static readonly List<SpriteRenderer> lavaCells = new List<SpriteRenderer>();
        static int lavaMs = 133, lavaShown = -1;
        static float lavaAlpha = -1f;

        static void BuildLava(string map, string json)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
                var r = doc.RootElement;
                if (cellsW == 0) { cellsW = r.GetProperty("width").GetInt32(); cellsH = r.GetProperty("height").GetInt32(); }
                int count = r.GetProperty("count").GetInt32();
                lavaMs = Math.Max(16, r.GetProperty("ms").GetInt32());
                string png = Path.Combine(Folder, r.GetProperty("frames").GetString());
                lavaTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!File.Exists(png) || !ImageConversion.LoadImage(lavaTex, File.ReadAllBytes(png)) || lavaTex.width < count * 32)
                {
                    Plugin.L.LogWarning(Tag + "lava frames missing or too small: " + png);
                    UnityEngine.Object.Destroy(lavaTex); lavaTex = null; return;
                }
                lavaTex.filterMode = FilterMode.Point; lavaTex.wrapMode = TextureWrapMode.Clamp;
                lavaFrames = new Sprite[count];
                for (int k = 0; k < count; k++)
                    lavaFrames[k] = Sprite.Create(lavaTex, new Rect(k * 32, 0, 32, 32), new Vector2(0.5f, 0.5f), PxPerUnit, 0, SpriteMeshType.FullRect);
                var root = new GameObject("SpriteModLava");
                root.transform.SetParent(go.transform, false);
                root.transform.localPosition = new Vector3(0f, 0f, LavaZ - Z);
                root.layer = go.layer;
                var cells = new List<(int x, int y)>();
                foreach (var c in r.GetProperty("cells").EnumerateArray()) cells.Add((c[0].GetInt32(), c[1].GetInt32()));
                var set = new HashSet<(int, int)>(cells);
                string ep = Path.Combine(Folder, r.TryGetProperty("edge", out var ee) && ee.ValueKind == System.Text.Json.JsonValueKind.String
                    ? ee.GetString() : "lava_edge.png");
                if (File.Exists(ep))
                {
                    edgeTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (ImageConversion.LoadImage(edgeTex, File.ReadAllBytes(ep)))
                    { edgeTex.filterMode = FilterMode.Point; edgeTex.wrapMode = TextureWrapMode.Clamp; }
                    else { UnityEngine.Object.Destroy(edgeTex); edgeTex = null; Plugin.L.LogWarning(Tag + "not a readable PNG: " + ep); }
                }
                int edges = 0;
                foreach (var (x, y) in cells)
                {
                    bool edge = edgeTex != null && !set.Contains((x, y - 1));
                    if (edge) edges++;
                    lavaEdge.Add(edge);
                    var g = new GameObject("lava");
                    g.transform.SetParent(root.transform, false);
                    g.transform.localPosition = new Vector3(x * Cell + Cell / 2f, -(y * Cell + Cell / 2f), 0f);
                    g.layer = go.layer;
                    var s = g.AddComponent<SpriteRenderer>();
                    s.sprite = lavaFrames[0];
                    if (LavaMaterial() != null) s.sharedMaterial = lavaMat;
                    else s.material.renderQueue = Queue;
                    lavaCells.Add(s);
                }
                lavaShown = -1; lavaAlpha = -1f; blendApplied = -1;
                if (lavaMat != null)
                {
                    lavaMat.SetFloat("_FrameCols", lavaTex.width / 32f);
                    lavaMat.SetFloat("_FrameRows", lavaTex.height / 32f);
                    lavaMat.SetTexture("_EdgeTex", edgeTex != null ? edgeTex : Texture2D.blackTexture);
                }
                Log($"{map}: lava layer, {lavaCells.Count} cells, {count} frames x {lavaMs} ms, z {LavaZ}, queue {Queue}, " +
                    (lavaMat != null ? "blend shader SpriteMod/LavaBlend" : "plain transparency (blend shader not available)") +
                    (edgeTex != null ? $", edge glow {edges} cells ({Path.GetFileName(ep)} {edgeTex.width}x{edgeTex.height})" : ", no edge mask (" + Path.GetFileName(ep) + ")"));
            }
            catch (Exception e) { Plugin.L.LogWarning(Tag + "lava layer not built: " + e.Message); }
        }

        static Material LavaMaterial()
        {
            if (lavaMat != null && !lavaMat.WasCollected) return lavaMat;
            if (!lavaShaderTried) { lavaShaderTried = true; lavaShader = GpuShadow.LoadShader("assets/shaders/lavablend.shader"); }
            if (lavaShader == null) return null;
            lavaMat = new Material(lavaShader) { renderQueue = Queue, name = "SpriteModLavaBlend" };
            lavaMat.hideFlags = HideFlags.DontUnloadUnusedAsset;
            blendApplied = -1;
            return lavaMat;
        }

        static void ApplyBlend()
        {
            if (lavaMat == null || lavaMat.WasCollected || blendApplied == BlendVersion) return;
            lavaMat.SetFloat("_Mode", BlendMode);
            lavaMat.SetFloat("_Opacity", BlendOpacity);
            lavaMat.SetVector("_TopRange", WaterRange / 255f);
            lavaMat.SetVector("_BaseRange", LavaRange / 255f);
            lavaMat.SetFloat("_EdgeMode", EdgeMode);
            lavaMat.SetFloat("_EdgeOpacity", EdgeOpacity);
            lavaMat.SetVector("_EdgeTop", EdgeWater / 255f);
            lavaMat.SetVector("_EdgeBase", EdgeLava / 255f);
            lavaMat.SetColor("_EdgeTint", EdgeTint);
            blendApplied = BlendVersion;
            Log($"lava blend: mode {BlendMode}, opacity {BlendOpacity}, water {WaterRange}, lava {LavaRange}; " +
                $"edge mode {EdgeMode}, opacity {EdgeOpacity}, tint {(EdgeTint.a > 0.5f ? EdgeTint.ToString() : "off")}, water {EdgeWater}, lava {EdgeLava}");
        }

        static void AnimateLava()
        {
            if (lavaFrames == null || lavaCells.Count == 0) return;
            ApplyBlend();
            int k = (int)((long)(FieldLava.AnimClockMs() / lavaMs) % lavaFrames.Length);   // MenuLayout "fieldLavaAnimSpeed"
            float a = lavaMat != null ? 1f : Mathf.Clamp01(LavaOpacity);   // with the blend shader, opacity is in fieldLavaBlend
            if (k == lavaShown && a == lavaAlpha) return;
            // vertex colour green = edge flag for the shader (0 = no edge glow); red/blue unused there
            var colEdge = new Color(1f, 1f, 1f, a);
            var colPlain = new Color(1f, lavaMat != null ? 0f : 1f, 1f, a);
            for (int i = 0; i < lavaCells.Count; i++)
            {
                var s = lavaCells[i];
                if (s == null || s.WasCollected) continue;
                if (k != lavaShown) s.sprite = lavaFrames[k];
                if (a != lavaAlpha) s.color = i < lavaEdge.Count && lavaEdge[i] ? colEdge : colPlain;
            }
            lavaShown = k; lavaAlpha = a;
        }

        static void Place()
        {
            try { AnimateLava(); } catch (Exception e) { Warn("lava animation failed: " + e.Message); }
            try { AnimateObjects(); } catch (Exception e) { Warn("object animation failed: " + e.Message); }
            if (ctrl == null || ctrl.WasCollected) { if (go.activeSelf) go.SetActive(false); Log("no field controller yet; shadow hidden"); return; }
            Vector3 W(float x, float y) => ctrl.ConvertCellPositionToWorldPosition(new Vector3(x, y, 0f));
            Vector3 V(Vector3 w) => ctrl.Last_Map_IMapAccessor_ConvertWorldToVisualPosition(w);
            Vector3 w00 = W(0, 0), w11 = W(1, 1), w0h = W(0, cellsH - 1);
            Vector3 v00 = V(w00), v11 = V(w11), v0h = V(w0h);
            float sx = Mathf.Sign(v11.x - v00.x), sy = Mathf.Sign(v11.y - v00.y);
            // map row 0 at the top of the screen: cell y grows downwards (sy < 0) -> row 0 = cell 0, else row 0 = cell h-1
            Vector3 top = sy < 0 ? v00 : v0h;
            // centre or corner: tile edges of the main-view ground mesh lie on the cell grid
            float half = 0f;
            float edge = groundMesh != null && !groundMesh.WasCollected ? groundMesh.bounds.min.x : float.NaN;
            if (!float.IsNaN(edge))
            {
                float ph = Mathf.Repeat(top.x - edge, Cell);
                half = Mathf.Abs(ph - Cell / 2f) < 2f ? Cell / 2f : 0f;
            }
            else half = Cell / 2f;   // fallback: centre [inferred]
            var pos = new Vector3(top.x - half * (sx < 0 ? -1 : 1), top.y + half, Z);
            if (!go.activeSelf) go.SetActive(true);
            go.transform.position = pos;
            if (!placedLogged)
            {
                placedLogged = true;
                Plugin.L.LogInfo(Tag + $"{shown}: cell(0,0) world {w00} visual {v00}; cell(1,1) world {w11} visual {v11}; cell(0,{cellsH - 1}) visual {v0h}; " +
                    $"mesh edge x {edge}; half {half}; overlay top-left {pos}");
            }
        }

        static void Drop()
        {
            DropObjects();
            if (go != null && !go.WasCollected) UnityEngine.Object.Destroy(go);
            if (sprite != null && !sprite.WasCollected) UnityEngine.Object.Destroy(sprite);
            if (tex != null && !tex.WasCollected) UnityEngine.Object.Destroy(tex);
            if (sprite2 != null && !sprite2.WasCollected) UnityEngine.Object.Destroy(sprite2);
            if (lavaFrames != null) foreach (var s in lavaFrames) if (s != null && !s.WasCollected) UnityEngine.Object.Destroy(s);
            if (lavaTex != null && !lavaTex.WasCollected) UnityEngine.Object.Destroy(lavaTex);
            if (edgeTex != null && !edgeTex.WasCollected) UnityEngine.Object.Destroy(edgeTex);
            lavaFrames = null; lavaTex = null; edgeTex = null; lavaCells.Clear(); lavaEdge.Clear(); cellsW = 0; cellsH = 0;
            if (tex2 != null && !tex2.WasCollected) UnityEngine.Object.Destroy(tex2);
            go = null; sr = null; sprite = null; tex = null; sprite2 = null; tex2 = null; groundMesh = null;
        }
    }
}
