using System;
using System.Reflection;
using UnityEngine;

namespace FF5MenuSprites
{
    /// <summary>
    /// MenuLayout "cleanup": the game forces Il2CppSystem.GC.Collect() and calls Resources.UnloadUnusedAssets() once or
    /// twice after battles, menus and map changes. Profiled 2026-11-05 [verified, GameProf log]: GC.Collect 38–58 ms in a
    /// 250–320 ms frame, followed by a ~220 ms frame with no timed game call (the unload sweep runs after its call returns;
    /// inferred from the pattern). The incremental GC (boot.config gc-max-time-slice=3) collects on its own, so
    /// "skipGcCollect" drops the forced full collection. "unloadEveryMinutes" lets a real unload sweep through at most
    /// that often (it frees textures nobody references, e.g. ones Magicite made); skipped calls return the last real
    /// AsyncOperation, which is already done. 0 = game's behaviour. Values apply live; hooks are installed at start.
    /// </summary>
    static class Cleanup
    {
        internal static bool SkipGcCollect;
        internal static float UnloadEveryMinutes;
        static AsyncOperation lastUnload;
        static float lastUnloadTime = -1e9f;
        static int skippedGc, skippedUnload;

        internal static void Install(HarmonyLib.Harmony harmony)
        {
            try
            {
                var gcPre = new HarmonyLib.HarmonyMethod(typeof(Cleanup).GetMethod(nameof(GcPrefix), BindingFlags.Static | BindingFlags.NonPublic));
                foreach (var m in typeof(Il2CppSystem.GC).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    if (m.Name == "Collect") harmony.Patch(m, prefix: gcPre); // every overload (game calls are timed under one label)
                var ua = HarmonyLib.AccessTools.Method(typeof(Resources), "UnloadUnusedAssets", Type.EmptyTypes);
                harmony.Patch(ua, prefix: new HarmonyLib.HarmonyMethod(typeof(Cleanup).GetMethod(nameof(UnloadPrefix), BindingFlags.Static | BindingFlags.NonPublic)),
                                  postfix: new HarmonyLib.HarmonyMethod(typeof(Cleanup).GetMethod(nameof(UnloadPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.L.LogInfo("[Cleanup] hooks applied");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("[Cleanup] hooks failed; the game's own clean-up stays: " + e.Message);
            }
        }

        static bool GcPrefix()
        {
            try
            {
                if (!SkipGcCollect) return true;
                skippedGc++;
                return false;
            }
            catch (Exception) { return true; }
        }

        static bool UnloadPrefix(ref AsyncOperation __result)
        {
            try
            {
                if (UnloadEveryMinutes <= 0f) return true;
                if (lastUnload == null || lastUnload.WasCollected) return true;          // nothing to hand back yet
                if (Time.realtimeSinceStartup - lastUnloadTime >= UnloadEveryMinutes * 60f) return true;
                if (!lastUnload.isDone) return true;                                     // never hand back a running one
                __result = lastUnload;
                skippedUnload++;
                return false;
            }
            catch (Exception) { return true; }
        }

        static void UnloadPostfix(AsyncOperation __result, bool __runOriginal)
        {
            try
            {
                if (!__runOriginal || __result == null) return;
                lastUnload = __result;
                lastUnloadTime = Time.realtimeSinceStartup;
            }
            catch (Exception) { }
        }

        /// <summary>For the [Perf] line: skipped calls since last report and the process's memory.</summary>
        internal static string Report()
        {
            string s = $"cleanup skipped: GC.Collect {skippedGc}, UnloadUnusedAssets {skippedUnload}; memory {System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / (1024 * 1024)} MB";
            skippedGc = skippedUnload = 0;
            return s;
        }
    }
}
