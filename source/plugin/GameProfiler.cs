using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace FF5MenuSprites
{
    /// <summary>
    /// MenuLayout "profileGame": true (read at game start; restart to switch) times the game's own map loading, asset
    /// loading, instantiation, PNG decoding and clean-up calls with Harmony prefix/postfix pairs (prefix first, postfix
    /// last, so other mods' patches on the same method, e.g. Magicite on CheckCompleteAsset, are inside the time).
    /// Times are inclusive (a call inside another hooked call counts in both). Logs [GameProf] lines at Warning level:
    /// every frame over 100 ms with the hooked time in it, and one summary per area change (RequestLoadMap until 30
    /// frames in a row under 34 ms). Main thread only.
    /// </summary>
    static class GameProfiler
    {
        internal static bool Enabled;
        class Stat { public double Ms, Max; public int Calls; }
        static readonly Dictionary<MethodBase, string> names = new Dictionary<MethodBase, string>();
        static readonly Dictionary<string, Stat> frame = new Dictionary<string, Stat>(), transition = new Dictionary<string, Stat>();
        static int mainThread;
        static long frameStart;
        static bool inTransition;
        static long transStart;
        static int transFrames, transSlow, calmFrames;
        static double transWorst;
        static string transMap = "";

        // (assembly-qualified type, method, parameter type names or null = every non-generic overload, label)
        static readonly (Type type, string method, string[] args, string label)[] Targets =
        {
            (typeof(Last.Map.MapManager), "RequestLoadMap", null, "Map.RequestLoadMap"),
            (typeof(Last.Map.MapManager), "LoadAsset", null, "Map.LoadAsset"),
            (typeof(Last.Map.MapManager), "CreateMapModel", null, "Map.CreateMapModel"),
            (typeof(Last.Map.MapManager), "SetMapModelData", null, "Map.SetMapModelData"),
            (typeof(Last.Map.MapManager), "SettingTileset", null, "Map.SettingTileset"),
            (typeof(Last.Map.MapManager), "CreateMapEntity", null, "Map.CreateMapEntity"),
            (typeof(Last.Map.MapManager), "CreateResidentEntity", null, "Map.CreateResidentEntity"),
            (typeof(Last.Map.MapManager), "RequestLoadScenerySprites", null, "Map.RequestLoadScenerySprites"),
            (typeof(Last.Map.MapManager), "RequestLoadFieldEntityAssets", null, "Map.RequestLoadFieldEntityAssets"),
            (typeof(Last.Map.MapManager), "LoadFieldEntityAssets", null, "Map.LoadFieldEntityAssets"),
            (typeof(Last.Map.MapManager), "MapLoadTask", null, "Map.MapLoadTask"),
            (typeof(Last.Map.MapManager), "MapLoadWaitTask", null, "Map.MapLoadWaitTask"),
            (typeof(Last.Map.MapManager), "ResidentEntityLoadTask", null, "Map.ResidentEntityLoadTask"),
            (typeof(Last.Map.MapManager), "FieldEntityAssetLoadTask", null, "Map.FieldEntityAssetLoadTask"),
            (typeof(Last.Map.MapManager), "MapSceneryTextureLoadTask", null, "Map.MapSceneryTextureLoadTask"),
            (typeof(Last.Map.MapManager), "MapScreenEffectTextureLoadTask", null, "Map.MapScreenEffectTextureLoadTask"),
            (typeof(Last.Map.MapManager), "BgmAssetLoadTask", null, "Map.BgmAssetLoadTask"),
            (typeof(Last.Map.MapManager), "FieldMapInitCompleteWaitTask", null, "Map.FieldMapInitCompleteWaitTask"),
            (typeof(Last.Management.ResourceManager), "Update", null, "Res.Update"),
            (typeof(Last.Management.ResourceManager), "LoadOperationTask", null, "Res.LoadOperationTask"),
            (typeof(Last.Management.ResourceManager), "UpdateLoaderTask", null, "Res.UpdateLoaderTask"),
            (typeof(Last.Management.ResourceManager), "RequestGroupLoadAssetBundle", null, "Res.RequestGroupLoadAssetBundle"),
            (typeof(Last.Management.ResourceManager), "RequestLoadAssetBundle", null, "Res.RequestLoadAssetBundle"),
            (typeof(Last.Management.ResourceManager), "CheckCompleteAsset", null, "Res.CheckCompleteAsset"),
            (typeof(Last.Management.ResourceManager), "CheckLoadAssetCompleted", null, "Res.CheckLoadAssetCompleted"),
            (typeof(Last.Management.ResourceManager), "CheckGroupLoadAssetCompleted", null, "Res.CheckGroupLoadAssetCompleted"),
            (typeof(Last.Management.ResourceManager), "IsLoadAssetCompleted", null, "Res.IsLoadAssetCompleted"),
            (typeof(Last.Management.ResourceManager), "DestroyGroupAsset", null, "Res.DestroyGroupAsset"),
            (typeof(Last.Management.ResourceManager), "DestroyAsset", null, "Res.DestroyAsset"),
            (typeof(Last.Management.ResourceManager), "DestroyAssetData", null, "Res.DestroyAssetData"),
            (typeof(Resources), "UnloadUnusedAssets", null, "Unity.UnloadUnusedAssets"),
            (typeof(Il2CppSystem.GC), "Collect", null, "Unity.GC.Collect"),
            (typeof(ImageConversion), "LoadImage", null, "Unity.LoadImage(PNG decode)"),
            // Never time Texture2D.Apply: hooking it routed native calls through the managed wrapper, which threw
            // "Texture ... is not readable" on game textures and broke KupoUI's swaps (258 errors, run 2026-11-06).
            (typeof(UnityEngine.Object), "Internal_CloneSingle", null, "Unity.Instantiate"),
            (typeof(UnityEngine.Object), "Internal_CloneSingleWithParent", null, "Unity.Instantiate"),
            (typeof(UnityEngine.Object), "Internal_InstantiateSingle", null, "Unity.Instantiate"),
            (typeof(UnityEngine.Object), "Internal_InstantiateSingleWithParent", null, "Unity.Instantiate"),
            (typeof(Shader), "WarmupAllShaders", null, "Unity.WarmupAllShaders"),
        };

        internal static void ReadSwitch(string configPath)
        {
            try
            {
                if (configPath == null || !System.IO.File.Exists(configPath)) return;
                var text = System.IO.File.ReadAllText(configPath);
                Enabled = Regex.IsMatch(text, "\"profileGame\"\\s*:\\s*true");
            }
            catch (Exception) { Enabled = false; }
        }

        internal static void Install(HarmonyLib.Harmony harmony)
        {
            if (!Enabled) return;
            mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            var pre = new HarmonyLib.HarmonyMethod(typeof(GameProfiler).GetMethod(nameof(Pre), BindingFlags.Static | BindingFlags.NonPublic)) { priority = HarmonyLib.Priority.First };
            var post = new HarmonyLib.HarmonyMethod(typeof(GameProfiler).GetMethod(nameof(Post), BindingFlags.Static | BindingFlags.NonPublic)) { priority = HarmonyLib.Priority.Last };
            var postNamed = new HarmonyLib.HarmonyMethod(typeof(GameProfiler).GetMethod(nameof(PostNamed), BindingFlags.Static | BindingFlags.NonPublic)) { priority = HarmonyLib.Priority.Last };
            int ok = 0, failed = 0;
            foreach (var (type, method, _, label) in Targets)
            {
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != method || m.IsGenericMethodDefinition) continue;
                    // ref/out parameters: Il2CppInterop builds an invalid wrapper for them (seen 2026-11-05 on
                    // CreateGroupEntity: InvalidProgramException, map entities never created, loading hung). Never patch those.
                    bool byRef = false;
                    foreach (var pi in m.GetParameters()) if (pi.ParameterType.IsByRef) byRef = true;
                    if (byRef) continue;
                    // Asset checks with a string first parameter also report the asset name when slow.
                    var ps = m.GetParameters();
                    bool named = label.StartsWith("Res.Check") || label.StartsWith("Res.IsLoad");
                    named = named && ps.Length >= 1 && ps[0].ParameterType == typeof(string);
                    try { harmony.Patch(m, prefix: pre, postfix: named ? postNamed : post); names[m] = label; ok++; }
                    catch (Exception e) { failed++; Plugin.L.LogWarning($"[GameProf] could not time {type.Name}.{method}: {e.Message}"); }
                }
            }
            Plugin.L.LogWarning($"[GameProf] game profiler on: {ok} methods timed, {failed} failed. Turn off with \"profileGame\": false and a restart (info, not an error)");
            // KupoUI's in-place texture swap (decompiled 2026-11-06: TextureResolver.TryReplaceTextureInPlace reads the
            // file, LoadImage()s it into the game's texture and Apply()s it). KupoUI may load after us: patch when it does.
            kupoHarmony = harmony; kupoPre = pre; kupoPost = post;
            if (!TryHookKupo()) AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }

        static HarmonyLib.Harmony kupoHarmony;
        static HarmonyLib.HarmonyMethod kupoPre, kupoPost;
        static bool kupoHooked;

        static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs e)
        {
            try
            {
                if (e.LoadedAssembly.GetName().Name != "KupoUI.PR") return;
                if (TryHookKupo()) AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            }
            catch (Exception ex) { Plugin.L.LogWarning("[GameProf] KupoUI hook failed: " + ex.Message); }
        }

        static bool TryHookKupo()
        {
            if (kupoHooked) return true;
            Type t = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "KupoUI.PR") { t = a.GetType("KupoUI.PR.Textures.TextureResolver"); break; }
            if (t == null) return false;
            kupoHooked = true;
            foreach (var (method, label) in new[] { ("TryReplaceTextureInPlace", "Kupo.ReplaceInPlace(total)"), ("LoadTexture", "Kupo.LoadTexture"), ("LoadTextureMetadata", "Kupo.LoadMetadata") })
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != method || m.IsGenericMethodDefinition) continue;
                    bool byRef = false;
                    foreach (var pi in m.GetParameters()) if (pi.ParameterType.IsByRef) byRef = true;
                    if (byRef) continue; // same caution as above
                    try { kupoHarmony.Patch(m, prefix: kupoPre, postfix: kupoPost); names[m] = label; Plugin.L.LogWarning($"[GameProf] timing KupoUI {method} (info, not an error)"); }
                    catch (Exception e) { Plugin.L.LogWarning($"[GameProf] could not time KupoUI {method}: {e.Message}"); }
                }
            return true;
        }

        static void Pre(out long __state)
        {
            __state = System.Threading.Thread.CurrentThread.ManagedThreadId == mainThread ? Stopwatch.GetTimestamp() : 0;
        }

        static void Post(long __state, MethodBase __originalMethod)
        {
            if (__state == 0) return;
            try
            {
                double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
                if (!names.TryGetValue(__originalMethod, out var label)) label = __originalMethod.Name;
                Add(frame, label, ms);
                if (inTransition) Add(transition, label, ms);
                if (label == "Map.RequestLoadMap") StartTransition();
            }
            catch (Exception) { }
        }

        static readonly List<string> slowAssets = new List<string>();

        static void PostNamed(long __state, MethodBase __originalMethod, string __0)
        {
            if (__state == 0) return;
            try
            {
                double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
                if (ms > 5 && slowAssets.Count < 12) slowAssets.Add($"{__0} {ms:0}");
            }
            catch (Exception) { }
            Post(__state, __originalMethod);
        }

        static void Add(Dictionary<string, Stat> d, string k, double ms)
        {
            if (!d.TryGetValue(k, out var s)) d[k] = s = new Stat();
            s.Ms += ms; s.Calls++; if (ms > s.Max) s.Max = ms;
        }

        static void StartTransition()
        {
            if (inTransition) return;
            inTransition = true;
            transStart = Stopwatch.GetTimestamp();
            transFrames = transSlow = calmFrames = 0; transWorst = 0;
            transition.Clear();
            foreach (var kv in frame) transition[kv.Key] = new Stat { Ms = kv.Value.Ms, Max = kv.Value.Max, Calls = kv.Value.Calls };
            transMap = SceneName();
        }

        static string SceneName()
        {
            try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch (Exception) { return "?"; }
        }

        /// <summary>Called once per frame from the watcher's Update: closes the previous frame's numbers.</summary>
        internal static void Frame()
        {
            if (!Enabled) return;
            long now = Stopwatch.GetTimestamp();
            double frameMs = frameStart == 0 ? 0 : (now - frameStart) * 1000.0 / Stopwatch.Frequency;
            frameStart = now;
            if (frameMs > 100)
            {
                double hooked = 0; // inclusive sums overlap; show top items instead of a misleading total
                Plugin.L.LogWarning($"[GameProf] slow frame {frameMs:0} ms ({SceneName()}{(LoadingIcon.Showing ? ", loading icon" : "")}): {Top(frame, 8, out hooked)}"
                    + (slowAssets.Count > 0 ? " | slow assets (ms): " + string.Join(", ", slowAssets) : "") + " (info, not an error)");
            }
            frame.Clear();
            slowAssets.Clear();
            if (!inTransition) return;
            transFrames++;
            if (frameMs > 33.4) { transSlow++; calmFrames = 0; } else calmFrames++;
            if (frameMs > transWorst) transWorst = frameMs;
            if (calmFrames >= 30)
            {
                double wall = (now - transStart) * 1000.0 / Stopwatch.Frequency;
                Plugin.L.LogWarning($"[GameProf] area change {transMap} -> {SceneName()}: {wall:0} ms until smooth, {transFrames} frames, {transSlow} over 33 ms, worst {transWorst:0} ms; timed calls (ms total / worst / calls): {Top(transition, 14, out _)} (info, not an error)");
                inTransition = false;
                transition.Clear();
            }
        }

        static string Top(Dictionary<string, Stat> d, int n, out double sum)
        {
            sum = 0;
            var list = new List<KeyValuePair<string, Stat>>(d);
            list.Sort((a, b) => b.Value.Ms.CompareTo(a.Value.Ms));
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < list.Count && i < n; i++)
            {
                var s = list[i].Value;
                if (s.Ms < 0.5 && !list[i].Key.Contains("Unload")) continue; // unload calls return fast but their work lands in later frames
                sum += s.Ms;
                sb.Append($"{list[i].Key} {s.Ms:0.0}/{s.Max:0.0}/{s.Calls}; ");
            }
            return sb.Length == 0 ? "nothing timed" : sb.ToString();
        }
    }
}
