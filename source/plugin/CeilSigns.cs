using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace FF5MenuSprites
{
    /// <summary>
    /// MenuLayout "fieldCeilingSigns": ceiling-layer tiles that should be drawn like wall (ground-layer) tiles, per tile sheet.
    /// Why [verified 2026-11-16]: in Karnak (Map_20060 tilemap.json; Tiled numbers the sheet's rows from the bottom) two sign
    /// boards are CeilLayer tiles stacked on a GroundLayer board in the same cell (cells 19,23 and 30,24); every other sign is
    /// a Bottom/Ground tile, and those draw behind heads correctly (user's screenshots 17:18 and 17:19). Ceiling tiles are drawn
    /// nearest (the opaque mesh writes depth) and painted onto heads by the head shader (CameraCeilTransparentRT, layer 25), so
    /// they cover taller heads. User: "it very clearly should not be a ceiling tile".
    /// How the game draws the ceiling [verified, [CeilDiag] 2026-11-16]: each ceiling mesh is a fixed grid of 345 tile slots
    /// (2 triangles each) that follows the camera; every slot has its own depth per row (z 46/48/50); as the view scrolls the
    /// game gives slots other tile pictures (uv); empty slots show uv (0,0), a transparent texel.
    /// What this does, every frame while the sheet has listed tiles: a slot that shows a listed picture is blanked by setting
    /// its uv to Mark (inside the same transparent corner texel as the game's empty uv 0,0, but a value the game never writes),
    /// and the picture is drawn from our own mesh at that slot's live position, with the same material, at the GroundLayer's
    /// depth + SignDepth (over the ground board in the same cell), on layer 23 (CameraTileMap draws it; the ceiling
    /// picture camera and the minimap camera do not [verified, F8 masks]). A slot that no longer shows Mark was given another
    /// picture by the game and is let go. Attempt 2 collapsed slot positions instead: signs showed in the wrong places as the
    /// grid scrolled (user's screenshots 17:58-18:00).
    /// </summary>
    static class CeilSigns
    {
        /// <summary>sheet name -> top-left pixel (x, y from the top) of each listed 16 px tile, as in the exported sheet PNG.</summary>
        internal static Dictionary<string, HashSet<(int, int)>> List = new Dictionary<string, HashSet<(int, int)>>(StringComparer.OrdinalIgnoreCase);
        const string View = "RootObject/RootFiled/MapRenderer/MainViewMapRenderer/MainView/";
        const int SignLayer = 23;
        // depth relative to the ground row the slot is in [verified, F8 18:39: rows step 2 units; a character is 1-1.5 units nearer than
        // its own row's ground (y -10 -> 245, y 10 -> 249, player 246.5)]: -2.25 = in front of characters in the sign's own cell and rows
        // behind (the game's ceiling look there), behind characters one row in front (taller heads). -0.5 (attempt 3) put the sign under
        // characters standing in its own cell (user 18:39: "the sign now stays under the character's feet").
        const float SignDepth = -2.25f;
        const float Tile = 16f, SheetPx = 512f;   // exported sheets are 512 px, 16 px tiles [verified, map_chip_147.png]
        // blank-slot uv: inside texel (0,0) of a 512 sheet (fully transparent [verified, map_chip_147.png bottom-left 16x16 alpha 0])
        static readonly Vector2 Mark = new Vector2(0.000732421875f, 0.000732421875f);

        /// <summary>One ceiling mesh (opaque or transparent) and the sign mesh made from it.</summary>
        sealed class Src
        {
            public string Name;
            public GameObject Go, SignGo;
            public Mesh SignMesh;
            public readonly Dictionary<long, Vector2[]> Mine = new Dictionary<long, Vector2[]>();   // slot -> its sign picture's uv (4 corners)
            public IntPtr SavedMesh; public int SavedCount = -1;
            public float NextFind;
            public readonly List<Il2CppStructArray<int>> SubIdx = new List<Il2CppStructArray<int>>();
            public readonly List<int> SubN = new List<int>();
            public readonly List<int[]> Uniq = new List<int[]>();    // per slot (in SubIdx order): its 4 vertex indices
            public readonly List<int[]> Tri = new List<int[]>();     // per slot: its triangles as 0-3
            public readonly List<long> Keys = new List<long>();
        }

        static readonly Src[] srcs = { new Src { Name = "CeilLayer_opaque" }, new Src { Name = "CeilLayer_transparent" } };
        static GameObject groundGo;
        static bool broken, unreadableLogged;
        static readonly HashSet<string> logged = new HashSet<string>();
        // diagnostics (user: "Yes, logging build"; with fieldSpritesDebug only): once a second on field maps, max 90 lines, identical lines skipped
        static float diagNext; static int diagLines; static bool diagNow;
        static readonly Dictionary<string, string> diagLast = new Dictionary<string, string>();
        static void Diag(string msg)
        {
            if (!diagNow || diagLines >= 90) return;
            string key = msg.Length > 16 ? msg.Substring(0, 16) : msg;
            if (diagLast.TryGetValue(key, out var last) && last == msg) return;
            diagLast[key] = msg; diagLines++;
            Plugin.L.LogWarning($"[CeilDiag] f {Time.frameCount}: " + msg);
        }

        internal static void PreRender()
        {
            if (broken) return;
            try
            {
                diagNow = FieldSprites.Debug && Time.unscaledTime >= diagNext && diagLines < 90;   // [CeilDiag] only with fieldSpritesDebug
                if (diagNow) diagNext = Time.unscaledTime + 1f;
                bool on = List.Count > 0 && FieldSprites.Enabled;
                if ((on || diagNow) && (groundGo == null || groundGo.WasCollected)) groundGo = GameObject.Find(View + "GroundLayer_opaque");
                if (groundGo == null) diagNow = false;        // log only on a field map
                foreach (var s in srcs) Run(s, on && groundGo != null);
            }
            catch (Exception e)
            {
                broken = true;
                foreach (var s in srcs) { try { Release(s); } catch (Exception) { } if (s.SignGo != null && !s.SignGo.WasCollected) UnityEngine.Object.Destroy(s.SignGo); }
                Plugin.L.LogWarning("[Field] ceiling signs off (the game's ceiling is restored): " + e.Message);
            }
        }

        static void Run(Src s, bool on)
        {
            if (!on) { Release(s); Hide(s); return; }
            if ((s.Go == null || s.Go.WasCollected) && Time.unscaledTime >= s.NextFind)
            {
                s.NextFind = Time.unscaledTime + 1f;
                var root = GameObject.Find("RootObject/RootFiled/MapRenderer/MainViewMapRenderer");
                var t = root != null ? FindUnder(root.transform, s.Name, 0) : null;
                s.Go = t != null ? t.gameObject : null;
            }
            if (s.Go == null) { Hide(s); return; }
            var mf = s.Go.GetComponent<MeshFilter>();
            var mr = s.Go.GetComponent<MeshRenderer>();
            var mesh = mf != null ? mf.sharedMesh : null;
            var mat = mr != null ? mr.sharedMaterial : null;
            var tex = mat != null ? mat.mainTexture : null;
            // HD tile swap renames the sheet '<name>_HD' (FieldTiles) [verified, F8 19:00: map_chip_147_HD 2048x2048]; sizes scale via SheetPx
            string sheet = tex != null && tex.name.EndsWith("_HD", StringComparison.Ordinal) ? tex.name.Substring(0, tex.name.Length - 3) : tex?.name;
            if (mesh == null || tex == null || !List.TryGetValue(sheet, out var tiles) || tiles.Count == 0) { Hide(s); return; }
            if (!mesh.isReadable)
            {
                if (!unreadableLogged) { unreadableLogged = true; Plugin.L.LogWarning($"[Field] {s.Name} mesh not readable; fieldCeilingSigns has no effect there (info)"); }
                Hide(s); return;
            }
            var v = mesh.vertices;
            var uv = mesh.uv;
            if (v == null || uv == null || uv.Length != v.Length) { Hide(s); return; }
            if (s.SavedMesh != mesh.Pointer || s.SavedCount != v.Length) Index(s, mesh, v.Length);

            float sx = tex.width / SheetPx, sy = tex.height / SheetPx;
            bool uvChanged = false;
            var signs = new List<int>();         // slot numbers drawn by us this frame
            int taken = 0, released = 0;
            for (int n = 0; n < s.Keys.Count; n++)
            {
                var u4 = s.Uniq[n];
                long key = s.Keys[n];
                bool marked = uv[u4[0]] == Mark && uv[u4[1]] == Mark && uv[u4[2]] == Mark && uv[u4[3]] == Mark;
                if (marked)
                {
                    if (s.Mine.ContainsKey(key)) signs.Add(n);
                    continue;
                }
                if (s.Mine.Remove(key)) released++;          // the game gave this slot another picture
                float u0 = float.MaxValue, v1 = float.MinValue;
                foreach (var q in u4) { u0 = Mathf.Min(u0, uv[q].x); v1 = Mathf.Max(v1, uv[q].y); }
                int px = Mathf.RoundToInt(u0 * tex.width / sx / Tile) * (int)Tile;
                int py = Mathf.RoundToInt((tex.height - v1 * tex.height) / sy / Tile) * (int)Tile;
                if (!tiles.Contains((px, py))) continue;
                var orig = new Vector2[4];
                for (int k = 0; k < 4; k++) { orig[k] = uv[u4[k]]; uv[u4[k]] = Mark; }
                s.Mine[key] = orig;
                signs.Add(n);
                uvChanged = true; taken++;
            }
            if (uvChanged) mesh.uv = uv;
            Diag($"{s.Name}: {s.Keys.Count} slots, {signs.Count} showing listed signs (taken now {taken}, let go {released})");
            if (signs.Count == 0) { Hide(s); return; }

            // our mesh: the listed pictures at the slots' live positions (the grid may move every frame)
            var col = mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Color) ? mesh.colors32 : null;
            var uv2 = mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord1) ? mesh.uv2 : null;
            int nvc = signs.Count * 4;
            var nv = new Vector3[nvc]; var nuv = new Vector2[nvc]; var ncol = col != null ? new Color32[nvc] : null; var nuv2 = uv2 != null ? new Vector2[nvc] : null;
            var ntri = new List<int>();
            for (int j = 0; j < signs.Count; j++)
            {
                int n = signs[j];
                var u4 = s.Uniq[n]; var orig = s.Mine[s.Keys[n]];
                for (int k = 0; k < 4; k++)
                {
                    nv[j * 4 + k] = v[u4[k]]; nuv[j * 4 + k] = orig[k];
                    if (ncol != null) ncol[j * 4 + k] = u4[k] < col.Length ? col[u4[k]] : new Color32(255, 255, 255, 255);
                    if (nuv2 != null) nuv2[j * 4 + k] = u4[k] < uv2.Length ? uv2[u4[k]] : Vector2.zero;
                }
                foreach (var t in s.Tri[n]) ntri.Add(j * 4 + t);
            }

            if (s.SignGo == null || s.SignGo.WasCollected || s.SignMesh == null || s.SignMesh.WasCollected)
            {
                s.SignGo = new GameObject("SpriteModCeilSigns_" + s.Name);
                s.SignGo.AddComponent<MeshFilter>();
                s.SignGo.AddComponent<MeshRenderer>();
                s.SignMesh = new Mesh { name = "SpriteModCeilSigns_" + s.Name };
                s.SignMesh.MarkDynamic();
                s.SignGo.GetComponent<MeshFilter>().sharedMesh = s.SignMesh;
            }
            s.SignGo.layer = SignLayer;
            var smr = s.SignGo.GetComponent<MeshRenderer>();
            if (smr.sharedMaterial == null || smr.sharedMaterial.Pointer != mat.Pointer) smr.sharedMaterial = mat;
            smr.enabled = true;
            if (!s.SignGo.activeSelf) s.SignGo.SetActive(true);
            Place(s);
            s.SignMesh.Clear();
            s.SignMesh.vertices = new Il2CppStructArray<Vector3>(nv);
            s.SignMesh.uv = new Il2CppStructArray<Vector2>(nuv);
            if (ncol != null) s.SignMesh.colors32 = new Il2CppStructArray<Color32>(ncol);
            if (nuv2 != null) s.SignMesh.uv2 = new Il2CppStructArray<Vector2>(nuv2);
            s.SignMesh.SetIndices(new Il2CppStructArray<int>(ntri.ToArray()), MeshTopology.Triangles, 0);
            s.SignMesh.RecalculateBounds();

            if (logged.Add(s.Name + "|" + tex.name))
                Plugin.L.LogWarning($"[Field] ceiling signs: listed tiles of '{tex.name}' taken out of {s.Name}, drawn at ground-layer depth " +
                                    $"{groundGo.transform.position.z + SignDepth:0.##} (colours {(ncol != null ? "copied" : "none")}, uv2 {(nuv2 != null ? "copied" : "none")}) (info, not an error)");
        }

        /// <summary>Slots of the mesh: 4 indices (quads) or 2 triangles (6 indices, 4 vertices) each.</summary>
        static void Index(Src s, Mesh mesh, int vertexCount)
        {
            s.Mine.Clear(); s.SubIdx.Clear(); s.SubN.Clear(); s.Uniq.Clear(); s.Tri.Clear(); s.Keys.Clear();
            s.SavedMesh = mesh.Pointer; s.SavedCount = vertexCount;
            var lay = new System.Text.StringBuilder();
            int odd = 0;
            var u = new List<int>(6);
            for (int sm = 0; sm < mesh.subMeshCount; sm++)
            {
                var topo = mesh.GetTopology(sm);
                int n = topo == MeshTopology.Quads ? 4 : topo == MeshTopology.Triangles ? 6 : 0;
                var ix = n > 0 ? mesh.GetIndices(sm) : null;
                lay.Append($" sub {sm} {topo} {(ix != null ? ix.Length : 0)} indices;");
                if (ix == null) continue;
                for (int i = 0; i + n - 1 < ix.Length; i += n)
                {
                    u.Clear();
                    for (int k = 0; k < n; k++) if (!u.Contains(ix[i + k])) u.Add(ix[i + k]);
                    if (u.Count != 4) { odd++; continue; }
                    var tri = n == 4 ? new[] { 0, 1, 2, 0, 2, 3 } : new int[6];
                    if (n == 6) for (int k = 0; k < 6; k++) tri[k] = u.IndexOf(ix[i + k]);
                    s.Uniq.Add(u.ToArray()); s.Tri.Add(tri); s.Keys.Add(((long)sm << 32) | (uint)i);
                }
            }
            Diag($"{s.Name}: mesh {vertexCount} vertices,{lay} {s.Keys.Count} slots, {odd} index groups not one tile");
        }

        /// <summary>Gives the game back every slot we blanked that still shows Mark (list changed or feature off).</summary>
        static void Release(Src s)
        {
            if (s.Mine.Count == 0 || s.Go == null || s.Go.WasCollected) { s.Mine.Clear(); return; }
            var mesh = s.Go.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null || mesh.Pointer != s.SavedMesh) { s.Mine.Clear(); return; }
            var uv = mesh.uv;
            bool ch = false;
            for (int n = 0; n < s.Keys.Count; n++)
            {
                if (!s.Mine.TryGetValue(s.Keys[n], out var orig)) continue;
                var u4 = s.Uniq[n];
                if (uv[u4[0]] != Mark) continue;
                for (int k = 0; k < 4; k++) uv[u4[k]] = orig[k];
                ch = true;
            }
            if (ch) mesh.uv = uv;
            s.Mine.Clear();
        }

        /// <summary>Same place as the ceiling mesh, at the ground layer's depth + SignDepth (per-row depth comes with the slot's own vertices).</summary>
        static void Place(Src s)
        {
            var t = s.SignGo.transform;
            var ct = s.Go.transform;
            if (t.parent == null || t.parent.Pointer != ct.parent.Pointer) t.SetParent(ct.parent, false);
            t.rotation = ct.rotation; t.localScale = ct.localScale;
            var p = ct.position;
            t.position = new Vector3(p.x, p.y, groundGo.transform.position.z + SignDepth);
        }

        static Transform FindUnder(Transform t, string name, int depth)
        {
            if (t.name == name) return t;
            if (depth > 6) return null;
            for (int i = 0; i < t.childCount; i++) { var r = FindUnder(t.GetChild(i), name, depth + 1); if (r != null) return r; }
            return null;
        }

        static void Hide(Src s)
        {
            if (s.SignGo != null && !s.SignGo.WasCollected && s.SignGo.activeSelf) s.SignGo.SetActive(false);
        }
    }
}
