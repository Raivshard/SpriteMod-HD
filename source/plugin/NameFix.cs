using System;
using System.Collections.Generic;
using System.Reflection;

namespace FF5MenuSprites
{
    /// <summary>
    /// MenuLayout "characterNames": { "Lenna": "Reina" }. Party names are stored per character in the save
    /// (Last.Data.User.OwnedCharacterData.Name, saved via OwnedCharacterSaveData.Name [verified, interop dump]), so a
    /// text pack that renames MSG_CHAR_NAME_02 only reaches new games. A postfix on OwnedCharacterData.get_Name swaps
    /// an exact old name for the new one; any other name (e.g. one the player typed) is left alone.
    /// </summary>
    static class NameFix
    {
        internal static Dictionary<string, string> Map = new Dictionary<string, string>();
        static bool logged;

        internal static void Install(HarmonyLib.Harmony harmony)
        {
            try
            {
                var target = HarmonyLib.AccessTools.PropertyGetter(typeof(Last.Data.User.OwnedCharacterData), "Name");
                harmony.Patch(target, postfix: new HarmonyLib.HarmonyMethod(typeof(NameFix).GetMethod(nameof(Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.L.LogInfo("[Names] character name hook applied");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("[Names] character name hook failed; saved names stay as they are: " + e.Message);
            }
        }

        static void Post(ref string __result)
        {
            try
            {
                if (__result == null || Map.Count == 0) return;
                if (Map.TryGetValue(__result, out var to) && !string.IsNullOrEmpty(to))
                {
                    if (!logged) { logged = true; Plugin.L.LogInfo($"[Names] saved name '{__result}' shown as '{to}'"); }
                    __result = to;
                }
            }
            catch (Exception) { }
        }
    }
}
