using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FF5MenuSprites
{
    /// <summary>
    /// Lava animation speed ("slow down the water overlay animation over lava tiles - half speed").
    /// The game draws lava with its water shader: Last.Map.Custom.WaterLayer.ApplyParamsForWaterShader(Material, Int32,
    /// MapCustomProperties, String[], Boolean) puts the map's MapWaterLayerProperty values (scroll_speed_x/y,
    /// noise_speed, normal_map_scroll_speed_x/y, normal_map_speed_scale, condensing_pattern_speed ...) on the material
    /// [verified, interop dump]; Karnak's lava is that shader with a custom orange colour #d94000 [verified, export].
    /// After each call, on a material with a lava-coloured colour property (red &gt; 0.6, green &lt; 0.6, blue &lt; 0.25), every
    /// float property whose name contains "speed" or "scroll" is multiplied by MenuLayout "fieldLavaSpeed" (default 0.5;
    /// 1 = the game's speed). Which properties exist is not known yet: the first call per material logs them all
    /// [the property names are inferred from the data fields; the log checks them]. Water that is not lava-coloured is
    /// left alone. Any error: off, logged once.
    /// </summary>
    static class FieldLava
    {
        internal static float Speed = 0.5f;
        /// <summary>MenuLayout "fieldLavaWaterBlend" (default 0.5): float properties of lava materials whose name contains
        /// "blend" are multiplied by this, so the animated lava tile shows through the water shader (
        /// "make 'water' animation partly transparent"). The property name is not known yet [inferred from
        /// MapWaterLayerProperty.water_blend_ratio]; the log lists every float property once.</summary>
        internal static float Blend = 1f;

        /// <summary>MenuLayout "fieldLavaAnimSpeed" (default 1, 0 = still): speed of the frame animations, i.e. the mobile lava
        /// layer (FieldMapShadows) and the tile animations from map_tiles/anim (FieldTiles, the lava back walls). Both
        /// read one clock that advances by real time x speed, so they stay in step and a live change never jumps frames.</summary>
        internal static float AnimSpeed = 1f;
        static int clockFrame = -1;
        static double clockMs;
        internal static double AnimClockMs()
        {
            if (Time.frameCount != clockFrame)
            {
                clockFrame = Time.frameCount;
                clockMs += Time.unscaledDeltaTime * 1000.0 * AnimSpeed;
            }
            return clockMs;
        }
        const string Tag = "[Lava] ";
        static bool broken;
        static readonly HashSet<IntPtr> logged = new HashSet<IntPtr>();
        // Live values ("fieldLavaSpeed and fieldLavaWaterBlend don't work"): the game sets a water
        // material up once and re-uses it on later visits, so the factors were applied only at the first setup. Each
        // lava material is kept with the game's own values; Tick() writes game value x current factor whenever the
        // factors change (MenuLayout saves apply live).
        sealed class Mat { public Material M; public readonly Dictionary<string, float> Speed = new Dictionary<string, float>(), Blend = new Dictionary<string, float>(); }
        static readonly Dictionary<IntPtr, Mat> mats = new Dictionary<IntPtr, Mat>();
        static float appliedSpeed = -1f, appliedBlend = -1f;
        static int posts;

        internal static void Tick()
        {
            if (broken || mats.Count == 0 || (Speed == appliedSpeed && Blend == appliedBlend)) return;
            try
            {
                Apply();
                Plugin.L.LogInfo(Tag + $"applied speed x{Speed}, blend x{Blend} to {mats.Count} lava material(s)");
            }
            catch (Exception e) { broken = true; Plugin.L.LogWarning(Tag + "stopped: " + e.Message); }
        }

        static void Apply()
        {
            var dead = new List<IntPtr>();
            foreach (var kv in mats)
            {
                var m = kv.Value.M;
                if (m == null || m.WasCollected) { dead.Add(kv.Key); continue; }
                foreach (var s in kv.Value.Speed) m.SetFloat(s.Key, s.Value * Speed);
                foreach (var b in kv.Value.Blend) m.SetFloat(b.Key, b.Value * Blend);
            }
            foreach (var k in dead) mats.Remove(k);
            appliedSpeed = Speed; appliedBlend = Blend;
        }

        internal static void Install(HarmonyLib.Harmony h)
        {
            var m = HarmonyLib.AccessTools.Method(typeof(Last.Map.Custom.WaterLayer), "ApplyParamsForWaterShader");
            h.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(FieldLava).GetMethod(nameof(Post),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
            Plugin.L.LogInfo(Tag + "water shader patch applied");
        }

        static void Post(Material __0)
        {
            if (broken || __0 == null) return;
            try
            {
                var sh = __0.shader;
                if (sh == null) return;
                int n = sh.GetPropertyCount();
                bool lava = false;
                var colours = new List<string>();
                var speeds = new List<int>();
                var blends = new List<int>();
                var allf = new List<string>();
                for (int i = 0; i < n; i++)
                {
                    string name = sh.GetPropertyName(i);
                    var type = sh.GetPropertyType(i);
                    if (type == ShaderPropertyType.Color && __0.HasProperty(name))
                    {
                        var c = __0.GetColor(name);
                        colours.Add($"{name}=({c.r:0.##},{c.g:0.##},{c.b:0.##})");
                        if (c.r > 0.6f && c.g < 0.6f && c.b < 0.25f) lava = true;
                    }
                    else if (type == ShaderPropertyType.Float || type == ShaderPropertyType.Range)
                    {
                        allf.Add($"{name}={__0.GetFloat(name):0.###}");
                        if (name.IndexOf("speed", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("scroll", StringComparison.OrdinalIgnoreCase) >= 0) speeds.Add(i);
                        else if (name.IndexOf("blend", StringComparison.OrdinalIgnoreCase) >= 0) blends.Add(i);
                    }
                }
                var before = new List<string>();
                foreach (int i in speeds) { string name = sh.GetPropertyName(i); before.Add($"{name}={__0.GetFloat(name):0.###}"); }
                if (lava)
                {
                    // the game just wrote its own values: keep them, then write value x factor
                    var rec = new Mat { M = __0 };
                    foreach (int i in speeds) { string name = sh.GetPropertyName(i); rec.Speed[name] = __0.GetFloat(name); }
                    foreach (int i in blends) { string name = sh.GetPropertyName(i); rec.Blend[name] = __0.GetFloat(name); }
                    mats[__0.Pointer] = rec;
                    foreach (var s in rec.Speed) __0.SetFloat(s.Key, s.Value * Speed);
                    foreach (var b in rec.Blend) __0.SetFloat(b.Key, b.Value * Blend);
                    appliedSpeed = Speed; appliedBlend = Blend;
                    if (++posts <= 30) Plugin.L.LogInfo(Tag + $"water set up by the game ({posts}): speed x{Speed}, blend x{Blend}");
                }
                if (logged.Count < 20 && logged.Add(__0.Pointer))
                    Plugin.L.LogInfo(Tag + $"material '{__0.name}' shader '{sh.name}': lava {lava}; colours {string.Join(" ", colours)}; speed properties {string.Join(" ", before)}" +
                        (lava && Speed != 1f ? $" -> x{Speed}" : "") + $"; blend properties x{(lava ? Blend : 1f)}: {blends.Count}; all floats: {string.Join(" ", allf)}");
            }
            catch (Exception e)
            {
                broken = true;
                Plugin.L.LogWarning(Tag + "stopped, lava keeps the game's speed: " + e.Message);
            }
        }
    }
}
