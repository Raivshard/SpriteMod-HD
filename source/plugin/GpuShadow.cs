using System;
using System.IO;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace FF5MenuSprites
{
    /// <summary>
    /// GPU ground shadows (2026-11-13, replaces groundShadow method A). Custom shaders come from
    /// SpriteMod HD/FF5/shaders/ff5shadow.bundle, built with Unity 2019.4.26f1 (the game's version [verified,
    /// globalgamemanagers header]) by tools/gpu_shadow/build_bundle.ps1.
    /// Step 1 (smoke test): load the bundle once, log the graphics API and the shaders, blit SpriteMod/SmokeTest into a
    /// 64x64 render texture and read the centre pixel; expected (0.5, 0.5, 0.25, 1).
    /// </summary>
    static class GpuShadow
    {
        static bool tried;
        static AssetBundle bundle;

        internal static string BundlePath => Path.Combine(Path.GetDirectoryName(Plugin.ConfigPath) ?? "", "shaders", "ff5shadow.bundle");

        /// <summary>Loads the bundle once (null if missing or unreadable; logged).</summary>
        internal static AssetBundle Bundle()
        {
            if (tried) return bundle;
            tried = true;
            try
            {
                string p = BundlePath;
                Plugin.L.LogWarning($"[GpuShadow] graphics API {SystemInfo.graphicsDeviceType}, shader level {SystemInfo.graphicsShaderLevel}, device '{SystemInfo.graphicsDeviceName}' (info, not an error)");
                if (!File.Exists(p)) { Plugin.L.LogWarning("[GpuShadow] no shader bundle at " + p + " (GPU shadows off)"); return null; }
                bundle = AssetBundle.LoadFromFile(p);
                if (bundle == null) { Plugin.L.LogWarning("[GpuShadow] shader bundle did not load: " + p); return null; }
                var names = bundle.GetAllAssetNames();
                var list = new System.Collections.Generic.List<string>();
                if (names != null) foreach (var n in names) list.Add(n);
                Plugin.L.LogWarning($"[GpuShadow] shader bundle loaded: {list.Count} asset(s): {string.Join(", ", list)} (info, not an error)");
            }
            catch (Exception e) { bundle = null; Plugin.L.LogWarning("[GpuShadow] shader bundle failed: " + e); }
            return bundle;
        }

        internal static Shader LoadShader(string assetPath)
        {
            var b = Bundle();
            if (b == null) return null;
            var o = b.LoadAsset(assetPath, Il2CppType.Of<Shader>());
            var s = o != null ? o.TryCast<Shader>() : null;
            if (s == null) Plugin.L.LogWarning("[GpuShadow] shader not in the bundle: " + assetPath);
            return s;
        }

        // ---- step 2: shadow buffer (outlines + ground lines) ----
        /// <summary>One body for the shadow buffer: its renderer and texture, up to 8 ground points (world x, y, left to right, already lowered by the lift), lift in world units.</summary>
        internal struct Caster { public Renderer R; public Texture Tex; public Vector2[] G; public int N; public float Lift; }
        internal static readonly System.Collections.Generic.List<Caster> Casters = new System.Collections.Generic.List<Caster>();

        static Material silMat, dbgMat, depthMat, castMat;
        static bool shadersTried, shadersOk, loggedFrame;
        static RenderTexture buf, seedA, seedB, depthRt, castRt;
        static readonly float[] bodyR = new float[16], bodyDc = new float[16];
        static readonly string[] bodyName = new string[16];
        static int idDc0, idDc1, idDc2, idDc3;
        static float fitLogAt = -1f;
        static string fitSig = "";
        static readonly Vector4[] bodyB = new Vector4[16];
        static RenderTexture fitRt;
        static int idFit, idPx, idDepthTex, idGsDepth, idMap, idBlurDir;
        static RenderTexture blurRt;
        static bool loggedMap;
        static Vector4 lastMap;
        static readonly int[] idB = new int[16];
        // pre: the GPU passes (outlines, depth, fit, cast) with CameraBattle's view, then the blend, on CameraBattlebackGround
        //   after it has rendered (depth -5, just before CameraBattle at 0 [verified, F8 dump]): the shadow lands on the
        //   background picture itself (the ground), under everything CameraBattle draws (bodies, weapons, effects).
        // cbDbg: the debug view, on CameraBattle after everything.
        // Why: blended from CameraBattle's own buffers (CameraTarget at BeforeForwardOpaque / BeforeForwardAlpha, CurrentActive
        // at BeforeForwardAlpha) the shadow was computed (debug red) but never seen [verified, tests 2026-11-13 22:04, 22:41,
        // 22:58]; only after everything showed (debug view).
        static UnityEngine.Rendering.CommandBuffer cbDbg, pre;
        static Camera cbCam, preCam;
        const UnityEngine.Rendering.CameraEvent PreEvent = UnityEngine.Rendering.CameraEvent.AfterEverything;
        static bool loggedPre;
        const UnityEngine.Rendering.CameraEvent DbgEvent = UnityEngine.Rendering.CameraEvent.AfterEverything;
        static readonly System.Collections.Generic.Dictionary<IntPtr, Material> mats = new System.Collections.Generic.Dictionary<IntPtr, Material>();
        static int idG0, idG1, idG2, idG3, idGN, idDbg, idStep, idWorld, idR0, idR1, idR2, idR3, idSil, idDepth, idLight, idMarch, idBox, idShade;

        /// <summary>
        /// Keeps a runtime-made asset alive across battles. The game unloads unused assets when a battle ends, and objects
        /// referenced only from plugin code count as unused: the second battle's SetTexture hit a destroyed material
        /// (NullReferenceException at Material.SetTexture [verified, log 2026-11-14 03:0x]). DontDestroyOnLoad does not
        /// protect assets; HideFlags.DontUnloadUnusedAsset does (as KupoTextureCache does for its copies).
        /// </summary>
        static void Keep(UnityEngine.Object o) { if (o != null) o.hideFlags |= HideFlags.DontUnloadUnusedAsset; }

        static bool Dead(UnityEngine.Object o) => o == null || o.WasCollected;

        static bool Shaders()
        {
            // rebuilt if anything was destroyed anyway
            if (shadersTried && shadersOk && (Dead(silMat) || Dead(dbgMat) || Dead(depthMat) || Dead(castMat)))
            {
                Plugin.L.LogWarning("[GpuShadow] shader materials were destroyed; rebuilding them (info, not an error)");
                shadersTried = false;
                mats.Clear();
            }
            if (shadersTried) return shadersOk;
            shadersTried = true;
            var a = LoadShader("assets/shaders/shadowsil.shader");
            var b = LoadShader("assets/shaders/shadowdebug.shader");
            var d = LoadShader("assets/shaders/shadowdepth.shader");
            var e = LoadShader("assets/shaders/shadowcast.shader");
            string St(Shader x) => x == null ? "missing" : x.isSupported ? "ok" : "unsupported";
            shadersOk = a != null && b != null && d != null && e != null && a.isSupported && b.isSupported && d.isSupported && e.isSupported;
            Plugin.L.LogWarning($"[GpuShadow] shaders ShadowSil {St(a)}, ShadowDebug {St(b)}, ShadowDepth {St(d)}, ShadowCast {St(e)}{(shadersOk ? "" : ": GPU shadows off")} (info, not an error)");
            if (!shadersOk) return false;
            Keep(a); Keep(b); Keep(d); Keep(e);
            silMat = new Material(a); dbgMat = new Material(b); depthMat = new Material(d); castMat = new Material(e);
            Keep(silMat); Keep(dbgMat); Keep(depthMat); Keep(castMat);
            idFit = Shader.PropertyToID("_GsFit"); idPx = Shader.PropertyToID("_GsPx");
            idDepthTex = Shader.PropertyToID("_DepthTex"); idGsDepth = Shader.PropertyToID("_GsDepth"); idMap = Shader.PropertyToID("_GsMap"); idBlurDir = Shader.PropertyToID("_GsBlurDir");
            idDc0 = Shader.PropertyToID("_GsDc0"); idDc1 = Shader.PropertyToID("_GsDc1"); idDc2 = Shader.PropertyToID("_GsDc2"); idDc3 = Shader.PropertyToID("_GsDc3");
            for (int i = 0; i < 16; i++) idB[i] = Shader.PropertyToID("_GsB" + i);
            idLight = Shader.PropertyToID("_GsLight"); idMarch = Shader.PropertyToID("_GsMarch"); idBox = Shader.PropertyToID("_GsBox"); idShade = Shader.PropertyToID("_GsShade");
            idStep = Shader.PropertyToID("_GsStep"); idWorld = Shader.PropertyToID("_GsWorld"); idSil = Shader.PropertyToID("_Sil"); idDepth = Shader.PropertyToID("_Depth");
            idR0 = Shader.PropertyToID("_GsR0"); idR1 = Shader.PropertyToID("_GsR1"); idR2 = Shader.PropertyToID("_GsR2"); idR3 = Shader.PropertyToID("_GsR3");
            idG0 = Shader.PropertyToID("_GsG0"); idG1 = Shader.PropertyToID("_GsG1");
            idG2 = Shader.PropertyToID("_GsG2"); idG3 = Shader.PropertyToID("_GsG3"); idGN = Shader.PropertyToID("_GsGN"); idDbg = Shader.PropertyToID("_GsDbg");
            return true;
        }

        /// <summary>
        /// Called once per frame after every caster is listed. Rebuilds one command buffer on CameraBattle
        /// (CameraEvent.AfterEverything): clear the shadow buffer, draw each caster's renderer with ShadowSil, and with
        /// debug on, blend the buffer over the battle picture. CPU work: a few commands per body, no mesh or pixel work.
        /// </summary>
        internal static void Frame(Camera battle, bool on, bool debug)
        {
            if (!on || battle == null || !Shaders()) { Drop(); Casters.Clear(); return; }
            int w = battle.pixelWidth, h = battle.pixelHeight;
            var bt = battle.targetTexture;
            if (bt != null) { w = bt.width; h = bt.height; }
            if (w < 16 || h < 16) { Casters.Clear(); return; }
            if (buf == null || buf.WasCollected || buf.width != w || buf.height != h)
            {
                ReleaseBuf();
                buf = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf);
                buf.filterMode = FilterMode.Point; buf.wrapMode = TextureWrapMode.Clamp;
                buf.Create();
                Keep(buf);
                // step 3: jump-flood seeds (float uv) and the depth map, half resolution
                RenderTexture Half(RenderTextureFormat f, FilterMode fm)
                {
                    var r = new RenderTexture(Math.Max(8, w / 2), Math.Max(8, h / 2), 0, f);
                    r.filterMode = fm; r.wrapMode = TextureWrapMode.Clamp; r.Create();
                    Keep(r);
                    return r;
                }
                seedA = Half(RenderTextureFormat.ARGBFloat, FilterMode.Point);
                seedB = Half(RenderTextureFormat.ARGBFloat, FilterMode.Point);
                depthRt = Half(RenderTextureFormat.ARGBHalf, FilterMode.Bilinear);
                castRt = Half(RenderTextureFormat.ARGBHalf, FilterMode.Bilinear);
                blurRt = Half(RenderTextureFormat.ARGBHalf, FilterMode.Bilinear);
                fitRt = new RenderTexture(16, 1, 0, RenderTextureFormat.ARGBFloat);
                fitRt.filterMode = FilterMode.Point; fitRt.wrapMode = TextureWrapMode.Clamp; fitRt.Create();
                Keep(fitRt);
                loggedFrame = false;
            }
            if (cbDbg == null) { cbDbg = new UnityEngine.Rendering.CommandBuffer(); cbDbg.name = "SpriteMod GPU shadow debug"; }
            if (pre == null) { pre = new UnityEngine.Rendering.CommandBuffer(); pre.name = "SpriteMod GPU shadow passes"; }
            if (preCam == null || preCam.WasCollected)
            {
                preCam = null;
                foreach (var c in Camera.allCameras) if (c.name == "CameraBattlebackGround") { preCam = c; break; }
                if (preCam != null) preCam.AddCommandBuffer(PreEvent, pre);
                if (!loggedPre) { loggedPre = true; Plugin.L.LogWarning(preCam != null ? $"[GpuShadow] passes and blend run on '{preCam.name}' (depth {preCam.depth}) after it renders (info, not an error)" : "[GpuShadow] CameraBattlebackGround not found: no shadow shown (debug view only)"); }
            }
            if (cbCam == null || cbCam.WasCollected || cbCam.Pointer != battle.Pointer)
            {
                Detach();
                battle.AddCommandBuffer(DbgEvent, cbDbg);
                cbCam = battle;
            }
            cbDbg.Clear();
            pre.Clear();
            pre.SetViewProjectionMatrices(battle.worldToCameraMatrix, battle.projectionMatrix);
            float upp = battle.orthographic ? 2f * battle.orthographicSize / h : 0.1f;   // world units per screen px
            pre.SetRenderTarget(Rt(buf));
            pre.ClearRenderTarget(false, true, new Color(0f, 0f, 0f, 0f));
            int id = 0;
            float maxR = 0f, maxH = 0f, maxD = 0f;
            var sigB = new System.Text.StringBuilder();
            int withMap = 0;
            Array.Clear(bodyR, 0, bodyR.Length);
            Array.Clear(bodyDc, 0, bodyDc.Length);
            for (int i = 0; i < 16; i++) bodyB[i] = new Vector4(2f, 2f, -1f, -1f);
            Vector2 boxLo = new Vector2(float.MaxValue, float.MaxValue), boxHi = new Vector2(float.MinValue, float.MinValue);
            foreach (var c in Casters)
            {
                if (c.R == null || c.R.WasCollected || c.Tex == null || c.N < 1) continue;
                id++;
                // step 3: full depth R of this body = thickness x its width on screen (world units)
                float R = Mathf.Max(0.01f, BattleCharScale.GsThick * c.R.bounds.size.x);
                var bb = c.R.bounds;
                if (id <= 16) bodyR[id - 1] = R;
                maxR = Mathf.Max(maxR, R);
                // step 4: tallest body above its (lowered) ground line, and all bodies' box on screen
                float gLow = float.MaxValue;
                for (int i = 0; i < c.N; i++) gLow = Mathf.Min(gLow, c.G[i].y);
                maxH = Mathf.Max(maxH, bb.max.y - gLow);
                var v0 = battle.WorldToViewportPoint(bb.min); var v1 = battle.WorldToViewportPoint(bb.max);
                boxLo = Vector2.Min(boxLo, Vector2.Min(v0, v1)); boxHi = Vector2.Max(boxHi, Vector2.Max(v0, v1));
                if (id <= 16) bodyB[id - 1] = new Vector4(Mathf.Min(v0.x, v1.x), Mathf.Min(v0.y, v1.y), Mathf.Max(v0.x, v1.x), Mathf.Max(v0.y, v1.y));
                // One material per body: this game's interop has no public CommandBuffer.SetGlobalTexture
                // [verified, interop dump 2026-11-13], and material values are read when the camera renders.
                if (!mats.TryGetValue(c.R.Pointer, out var mat) || mat == null || mat.WasCollected)
                {
                    mat = new Material(silMat);
                    Keep(mat);
                    mats[c.R.Pointer] = mat;
                }
                if (mat.mainTexture == null || mat.mainTexture.Pointer != c.Tex.Pointer) mat.mainTexture = c.Tex;
                var g = c.G;
                Vector2 P(int i) => g[Math.Min(i, c.N - 1)];
                mat.SetVector(idG0, new Vector4(P(0).x, P(0).y, P(1).x, P(1).y));
                mat.SetVector(idG1, new Vector4(P(2).x, P(2).y, P(3).x, P(3).y));
                mat.SetVector(idG2, new Vector4(P(4).x, P(4).y, P(5).x, P(5).y));
                mat.SetVector(idG3, new Vector4(P(6).x, P(6).y, P(7).x, P(7).y));
                mat.SetVector(idGN, new Vector4(c.N, c.Lift, id, 0f));
                // depth map beside the sprite's file (2026-11-14): front-to-back range D = depthRange x body width
                // The outline pass stores the map's depth unscaled (0.5 - v); the fit pass scales it per body so both soles
                // touch the ground (user, 2026-11-14), falling back to depthRange x width.
                var map = DepthMaps.For(c.Tex);
                float D = Mathf.Max(0f, BattleCharScale.GsDepthRange) * bb.size.x;
                if (map != null) withMap++;
                maxD = Mathf.Max(maxD, Mathf.Max(D, bb.size.x));
                if (id <= 16) { bodyDc[id - 1] = D; bodyName[id - 1] = c.Tex.name; }
                sigB.Append(c.Tex.GetInstanceID()).Append(',');
                mat.SetTexture(idDepthTex, map != null ? (Texture)map : Texture2D.blackTexture);
                mat.SetVector(idGsDepth, new Vector4(map != null ? 1f : 0f, 1f, 0f, 0f));
                Draw(pre, c.R, mat);
            }
            // step 3: depth map by jump flooding at half resolution (seed, jumps down to 1 px plus one extra 1-px pass, resolve)
            float worldH = upp * h, worldW = upp * w;
            int hw = seedA.width, hh = seedA.height;
            float maxPx = maxR / (upp * 2f);                                             // in half-res px
            int step = 1; while (step < maxPx && step < 512) step <<= 1;
            depthMat.SetTexture(idSil, buf);
            depthMat.SetVector(idWorld, new Vector4(worldW, worldH, 0f, 0f));
            depthMat.SetVector(idR0, new Vector4(bodyR[0], bodyR[1], bodyR[2], bodyR[3]));
            depthMat.SetVector(idR1, new Vector4(bodyR[4], bodyR[5], bodyR[6], bodyR[7]));
            depthMat.SetVector(idR2, new Vector4(bodyR[8], bodyR[9], bodyR[10], bodyR[11]));
            depthMat.SetVector(idR3, new Vector4(bodyR[12], bodyR[13], bodyR[14], bodyR[15]));
            pre.Blit(Rt(buf), Rt(seedA), depthMat, 0);
            var src = seedA; var dst = seedB;
            int passes = 0;
            for (int k = step; ; k >>= 1)
            {
                int kk = Math.Max(1, k);
                pre.SetGlobalVector(idStep, new Vector4((float)kk / hw, (float)kk / hh, 0f, 0f));
                pre.Blit(Rt(src), Rt(dst), depthMat, 1);
                var t = src; src = dst; dst = t;
                passes++;
                if (k <= 0) break;   // k = 0 ran as the extra 1-px pass
            }
            pre.Blit(Rt(src), Rt(depthRt), depthMat, 2);
            // step 4: march from every ground pixel toward the light (half res), then black x opacity onto the camera
            float az = BattleCharScale.GsAzimuth * Mathf.Deg2Rad, tilt = Mathf.Clamp(BattleCharScale.GsTilt, 0.05f, 1f);
            float tanE = Mathf.Tan(Mathf.Clamp(BattleCharScale.GsElevation, 5f, 89f) * Mathf.Deg2Rad);
            var light = new Vector4(-Mathf.Cos(az), -Mathf.Sin(az) * tilt + tanE, tanE, Mathf.Sin(az) * tilt);
            float move = Mathf.Max(1e-4f, new Vector2(light.x, light.y).magnitude);   // screen world units per unit L
            float Lmax = (maxH + maxD * tilt + 2f * maxR * tilt + 2f) / tanE;   // depth moves the ground point by up to D t
            float stepL = 2f * upp / move;                                               // one half-res px per step
            int nSteps = Mathf.CeilToInt(Lmax / stepL);
            if (nSteps > 256) { nSteps = 256; stepL = Lmax / 256f; }
            float tolMin = 0.5f * stepL * Mathf.Abs(light.w) + 2f * upp;
            castMat.SetTexture(idSil, buf);
            castMat.SetTexture(idDepth, depthRt);
            castMat.SetVector(idWorld, new Vector4(worldW, worldH, tilt, 0f));
            castMat.SetVector(idLight, light);
            castMat.SetVector(idMarch, new Vector4(stepL, nSteps, tolMin, 0f));
            castMat.SetVector(idBox, id > 0 ? new Vector4(boxLo.x, boxLo.y, boxHi.x, boxHi.y) : new Vector4(2f, 2f, -1f, -1f));
            castMat.SetVector(idShade, new Vector4(Mathf.Clamp01(BattleCharScale.GsOpacity), Mathf.Clamp01(BattleCharScale.GsFalloff), 0f, 0f));
            // standing bodies' ground: each body slid down until its lowest point in 3D touches it (pass 2, 16 x 1)
            for (int i = 0; i < 16; i++) castMat.SetVector(idB[i], bodyB[i]);
            castMat.SetVector(idPx, new Vector4(w, h, 0f, 0f));
            castMat.SetVector(idDc0, new Vector4(bodyDc[0], bodyDc[1], bodyDc[2], bodyDc[3]));
            castMat.SetVector(idDc1, new Vector4(bodyDc[4], bodyDc[5], bodyDc[6], bodyDc[7]));
            castMat.SetVector(idDc2, new Vector4(bodyDc[8], bodyDc[9], bodyDc[10], bodyDc[11]));
            castMat.SetVector(idDc3, new Vector4(bodyDc[12], bodyDc[13], bodyDc[14], bodyDc[15]));
            LogFit(id);
            castMat.SetTexture(idFit, fitRt);
            // The fit (16 threads, each scanning its body's feet) is redone every 4th frame and whenever the bodies or their
            // textures change; its ground is stored relative to each body's box, so moving bodies stay right in between.
            string sig = sigB.ToString();
            if (sig != fitSig || Time.frameCount % 4 == 0) { fitSig = sig; pre.Blit(Rt(buf), Rt(fitRt), castMat, 2); }
            pre.Blit(Rt(buf), Rt(castRt), castMat, 0);
            // step 5: soft edge, blur px at 1440p -> half-res px; Gaussian across then down (cast -> blur -> cast)
            float blurTaps = Mathf.Clamp(BattleCharScale.GsBlur * h / 1440f * 0.5f, 0f, 32f);
            if (blurTaps >= 0.5f)
            {
                pre.SetGlobalVector(idBlurDir, new Vector4(1f / hw, 0f, blurTaps, 0f));
                pre.Blit(Rt(castRt), Rt(blurRt), castMat, 3);
                pre.SetGlobalVector(idBlurDir, new Vector4(0f, 1f / hh, blurTaps, 0f));
                pre.Blit(Rt(blurRt), Rt(castRt), castMat, 3);
            }
            // the blend: onto the background camera's picture (the ground), after it has rendered
            // its picture can be larger than the screen (fitBackground): map its uv to screen uv through its pixelRect
            // (the Battle render texture fills the screen [inferred, cursor x matched 2026-11-12])
            var map01 = new Vector4(1f, 1f, 0f, 0f);
            if (preCam != null && Screen.width > 0 && Screen.height > 0)
            {
                var pr = preCam.pixelRect;
                // The screen shows the MIDDLE of that picture: pixelRect says 0,0 3414x1707 on a 2560x1440 screen, and the
                // shadow drawn with offset 0 landed ~427 px left / ~133 px low of where it was computed [measured, debug
                // screenshot 2026-11-14 01:3x: black vs red] = half the overhang each way.
                map01 = new Vector4(pr.width / Screen.width, pr.height / Screen.height,
                    (pr.x - (pr.width - Screen.width) * 0.5f) / Screen.width, (pr.y - (pr.height - Screen.height) * 0.5f) / Screen.height);
                if (!loggedMap || map01 != lastMap)
                {
                    loggedMap = true; lastMap = map01;
                    Plugin.L.LogWarning($"[GpuShadow] background camera pixelRect {pr.x:0},{pr.y:0} {pr.width:0}x{pr.height:0}, screen {Screen.width}x{Screen.height} -> shadow scale {map01.x:0.###},{map01.y:0.###} offset {map01.z:0.###},{map01.w:0.###} (info, not an error)");
                }
            }
            castMat.SetVector(idMap, map01);
            pre.Blit(Rt(castRt), Rt(UnityEngine.Rendering.BuiltinRenderTextureType.CameraTarget), castMat, 1);
            if (preCam == null) Graphics.ExecuteCommandBuffer(pre);
            if (debug)
            {
                dbgMat.SetVector(idDbg, new Vector4(upp, 0f, 0f, 0f));
                dbgMat.SetTexture(idDepth, depthRt);
                dbgMat.SetTexture("_Cast", castRt);
                cbDbg.Blit(Rt(buf), Rt(UnityEngine.Rendering.BuiltinRenderTextureType.CameraTarget), dbgMat);
            }
            if (!loggedFrame && id > 0)
            {
                loggedFrame = true;
                Plugin.L.LogWarning($"[GpuShadow] shadow buffer {w}x{h} ARGBHalf for camera '{battle.name}' ({battle.actualRenderingPath}, clear {battle.clearFlags}), {id} body(ies), {withMap} with a depth map (depthRange {BattleCharScale.GsDepthRange:0.##} -> up to {maxD:0.#} units); " +
                    $"depth map {hw}x{hh}, thickness {BattleCharScale.GsThick:0.##} -> depth up to {maxR:0.#} units ({maxPx:0} half-res px), {passes} jump pass(es) from {step} px; " +
                    $"shadow: azimuth {BattleCharScale.GsAzimuth:0} elevation {BattleCharScale.GsElevation:0} tilt {tilt:0.##}, tallest {maxH:0.#} units -> {nSteps} steps of {stepL:0.##} units, " +
                    $"box {boxLo.x:0.##},{boxLo.y:0.##}-{boxHi.x:0.##},{boxHi.y:0.##}; debug view {(debug ? "on" : "off")} (info, not an error)");
            }
            Casters.Clear();
        }

        // RenderTargetIdentifier's constructors are stripped from the game (op_Implicit(Texture) threw
        // MissingMethodException: ..ctor(Texture) [verified, log 2026-11-13]); its fields are filled here the way Unity
        // 2019.4's constructors do [inferred from Unity's C# reference source].
        static UnityEngine.Rendering.RenderTargetIdentifier Rt(Texture t) => new UnityEngine.Rendering.RenderTargetIdentifier
        {
            m_Type = t == null ? UnityEngine.Rendering.BuiltinRenderTextureType.None
                : t.TryCast<RenderTexture>() != null ? UnityEngine.Rendering.BuiltinRenderTextureType.RenderTexture
                : UnityEngine.Rendering.BuiltinRenderTextureType.BindableTexture,
            m_NameID = -1, m_InstanceID = t != null ? t.GetInstanceID() : 0, m_BufferPointer = IntPtr.Zero,
            m_MipLevel = 0, m_CubeFace = CubemapFace.Unknown, m_DepthSlice = 0
        };
        static UnityEngine.Rendering.RenderTargetIdentifier Rt(UnityEngine.Rendering.BuiltinRenderTextureType type) => new UnityEngine.Rendering.RenderTargetIdentifier
        {
            m_Type = type, m_NameID = -1, m_InstanceID = 0, m_BufferPointer = IntPtr.Zero,
            m_MipLevel = 0, m_CubeFace = CubemapFace.Unknown, m_DepthSlice = 0
        };

        // CommandBuffer.DrawRenderer's managed wrapper references String.Format(String, Il2CppSystem.Object), missing in
        // the game (MissingMethodException [verified, log 2026-11-13]); the native Internal_DrawRenderer(Renderer,
        // Material, Int32, Int32) it forwards to is in the interop [verified, interop dump] and is called directly.
        static Action<UnityEngine.Rendering.CommandBuffer, Renderer, Material, int, int> drawRenderer;
        static void Draw(UnityEngine.Rendering.CommandBuffer b, Renderer r, Material m)
        {
            if (drawRenderer == null)
            {
                var mi = typeof(UnityEngine.Rendering.CommandBuffer).GetMethod("Internal_DrawRenderer",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                    null, new[] { typeof(Renderer), typeof(Material), typeof(int), typeof(int) }, null);
                if (mi == null) throw new MissingMethodException("CommandBuffer.Internal_DrawRenderer(Renderer, Material, Int32, Int32) not in the interop");
                drawRenderer = (Action<UnityEngine.Rendering.CommandBuffer, Renderer, Material, int, int>)
                    Delegate.CreateDelegate(typeof(Action<UnityEngine.Rendering.CommandBuffer, Renderer, Material, int, int>), mi);
            }
            drawRenderer(b, r, m, 0, 0);
        }

        /// <summary>Once per battle, 2 s in: reads the 16 x 1 fit result (ground, depth scale, solved?, sole height gap) and logs it (a 16-pixel read).</summary>
        static void LogFit(int n)
        {
            if (fitLogAt < 0f) { fitLogAt = Time.unscaledTime + 2f; return; }
            if (fitLogAt == float.MaxValue || Time.unscaledTime < fitLogAt || fitRt == null) return;
            fitLogAt = float.MaxValue;
            var prev = RenderTexture.active;
            Texture2D t = null;
            try
            {
                RenderTexture.active = fitRt;
                t = new Texture2D(16, 1, TextureFormat.RGBAFloat, false, true);
                t.ReadPixels(new Rect(0, 0, 16, 1), 0, 0);
                t.Apply(false);
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < Math.Min(n, 16); i++)
                {
                    var c = t.GetPixel(i, 0);
                    sb.Append($" [{i + 1} '{bodyName[i]}': {(c.b > 0.5f ? "soles solved" : "fallback")} depth scale {c.g:0.#} units (depthRange gives {bodyDc[i]:0.#}), rear sole {c.a:0.#} units higher]");
                }
                Plugin.L.LogWarning("[GpuShadow] ground fit:" + sb + " (info, not an error)");
            }
            catch (Exception e) { Plugin.L.LogWarning("[GpuShadow] ground fit log failed: " + e.Message); }
            finally { RenderTexture.active = prev; if (t != null) UnityEngine.Object.Destroy(t); }
        }

        static void Detach()
        {
            try { if (cbCam != null && !cbCam.WasCollected && cbDbg != null) cbCam.RemoveCommandBuffer(DbgEvent, cbDbg); } catch { }
            cbCam = null;
            try { if (preCam != null && !preCam.WasCollected && pre != null) preCam.RemoveCommandBuffer(PreEvent, pre); } catch { }
            preCam = null;
        }

        static void ReleaseBuf()
        {
            foreach (var r in new[] { buf, seedA, seedB, depthRt, castRt, fitRt, blurRt })
                try { if (r != null && !r.WasCollected) { r.Release(); UnityEngine.Object.Destroy(r); } } catch { }
            buf = seedA = seedB = depthRt = castRt = fitRt = blurRt = null;
        }

        /// <summary>Removes the command buffer from the camera and frees the buffer (battle over, or groundShadow off).</summary>
        internal static void Drop()
        {
            if (cbCam == null && preCam == null && buf == null && mats.Count == 0) return;
            Detach();
            if (cbDbg != null) try { cbDbg.Clear(); } catch { }
            if (pre != null) try { pre.Clear(); } catch { }
            loggedPre = false;
            ReleaseBuf();
            foreach (var m in mats.Values) try { if (m != null && !m.WasCollected) UnityEngine.Object.Destroy(m); } catch { }
            mats.Clear();
            loggedFrame = false;
            fitLogAt = -1f;
            fitSig = "";
        }

        /// <summary>
        /// Depth maps (2026-11-14): "<sprite file without .png>.depth.png" beside the PNG KupoUI loaded the texture from
        /// (KupoTextureCache.TexFile), made in ComfyUI with Depth Anything V2 (background black, bright = near). Loaded
        /// once per file; a missing file is remembered (checked again after 10 s, so a new map shows up live).
        /// </summary>
        internal static class DepthMaps
        {
            class Entry { public Texture2D Tex; public float Checked; public long Stamp; }
            static readonly System.Collections.Generic.Dictionary<string, Entry> byFile = new System.Collections.Generic.Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            static readonly System.Collections.Generic.HashSet<string> loggedNoFile = new System.Collections.Generic.HashSet<string>();

            internal static Texture2D For(Texture tex)
            {
                if (tex == null) return null;
                if (!KupoTextureCache.TexFile.TryGetValue(tex.GetInstanceID(), out var file))
                {
                    if (loggedNoFile.Add(tex.name)) Plugin.L.LogWarning($"[GpuShadow] '{tex.name}': no KupoUI file known for this texture, so no depth map (rounded edge used) (info, not an error)");
                    return null;
                }
                string dp = Path.Combine(Path.GetDirectoryName(file) ?? "", Path.GetFileNameWithoutExtension(file) + ".depth.png");
                if (!byFile.TryGetValue(dp, out var e)) { e = new Entry { Checked = -100f }; byFile[dp] = e; }
                if (e.Tex != null && !e.Tex.WasCollected) return e.Tex;
                if (Time.unscaledTime - e.Checked < 10f) return null;
                e.Checked = Time.unscaledTime;
                try
                {
                    if (!File.Exists(dp))
                    {
                        if (e.Stamp == 0) { e.Stamp = -1; Plugin.L.LogWarning($"[GpuShadow] '{tex.name}' from {file}: no depth map ({Path.GetFileName(dp)}), rounded edge used (info, not an error)"); }
                        return null;
                    }
                    var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                    if (!ImageConversion.LoadImage(t, File.ReadAllBytes(dp))) { UnityEngine.Object.Destroy(t); Plugin.L.LogWarning("[GpuShadow] not a readable PNG: " + dp); return null; }
                    t.filterMode = FilterMode.Bilinear; t.wrapMode = TextureWrapMode.Clamp; t.name = Path.GetFileName(dp);
                    Keep(t);
                    e.Tex = t; e.Stamp = File.GetLastWriteTimeUtc(dp).Ticks;
                    bool same = t.width == tex.width && t.height == tex.height;
                    Plugin.L.LogWarning($"[GpuShadow] '{tex.name}': depth map {dp} {t.width}x{t.height}{(same ? "" : $" (texture is {tex.width}x{tex.height}: SIZE DIFFERS, map may not line up)")} (info, not an error)");
                    return t;
                }
                catch (Exception ex) { Plugin.L.LogWarning("[GpuShadow] depth map failed: " + dp + ": " + ex.Message); return null; }
            }
        }

        static bool smokeDone;

        /// <summary>Step 1: runs once, a few frames after start (the device is up), and logs the result.</summary>
        internal static void Smoke()
        {
            if (smokeDone || Time.frameCount < 30) return;
            smokeDone = true;
            RenderTexture rt = null; Texture2D read = null; Material mat = null;
            var prev = RenderTexture.active;
            try
            {
                var sh = LoadShader("assets/shaders/smoketest.shader");
                if (sh == null) return;
                Plugin.L.LogWarning($"[GpuShadow] smoke test: shader '{sh.name}' supported={sh.isSupported} (info, not an error)");
                if (!sh.isSupported) return;
                mat = new Material(sh);
                rt = RenderTexture.GetTemporary(64, 64, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Graphics.Blit(Texture2D.whiteTexture, rt, mat);
                RenderTexture.active = rt;
                read = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
                read.ReadPixels(new Rect(32, 32, 1, 1), 0, 0);
                read.Apply(false);
                var c = read.GetPixel(0, 0);
                bool ok = Mathf.Abs(c.r - 0.5f) < 0.03f && Mathf.Abs(c.g - 0.5f) < 0.03f && Mathf.Abs(c.b - 0.25f) < 0.03f && c.a > 0.97f;
                Plugin.L.LogWarning($"[GpuShadow] smoke test pixel ({c.r:0.###}, {c.g:0.###}, {c.b:0.###}, {c.a:0.###}), expected (0.5, 0.5, 0.25, 1): {(ok ? "PASS, custom shaders run in the game" : "FAIL")} (info, not an error)");
            }
            catch (Exception e) { Plugin.L.LogWarning("[GpuShadow] smoke test failed: " + e); }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (read != null) UnityEngine.Object.Destroy(read);
                if (mat != null) UnityEngine.Object.Destroy(mat);
            }
        }
    }
}
