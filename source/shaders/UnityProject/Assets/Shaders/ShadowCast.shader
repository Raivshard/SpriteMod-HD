// Step 4 (2026-11-13): per-pixel shadow by marching from each ground pixel toward the light.
// Geometry (world units, orthographic view): a ground point at screen (x, y) lies at ground depth y / t
// (t = groundTilt); a body point of height H above its ground appears H higher on screen. The light comes from
// azimuth a (screen direction the shadow falls) at elevation e. From ground pixel P, the ray back to the light at
// ground distance L is at screen Q = P + L * (-cos a, -sin a * t + tan e) with height L tan e.
// Each body pixel is a slab in depth (2026-11-14): its front at z_f (shadow buffer r, from the sprite's depth
// map: arm in front, cape behind), its thickness 2h (depth map pass r: rounded from the outline), so its middle
// at z_c = z_f + h (world units, + = away from the camera). Its ground point is at screen y
//   standing: c + z_c t, with c and the depth scale D from pass 2 = both soles on the ground (fallback: the body slid
//             down until its lowest 3D point touches); every sole touches, whatever its height on screen: no hinge;
//   lifted (flyer, Float): c from pass 2 = its lowest 3D point exactly the effective gap (shadow buffer b) above it.
// A body pixel at Q holds the ray point when the ray's ground position matches: |(P.y - L sin a t) - ground| <= h t
// (+ half a step). Side light gets the body's real depth as width.
// Pass 0 cast (half res): out r = shadow strength (1 at the feet, 1 - falloff at the top). Pass 1 composite: black x
// opacity x strength onto the camera. Pass 3 blur: Gaussian, across then down (step 5).
// Pass 2 fit (16 x 1, texel = body id): both soles on the ground -> depth scale D and ground c (see the pass).
Shader "SpriteMod/ShadowCast"
{
    Properties { _MainTex ("in", 2D) = "black" {} _Sil ("buffer", 2D) = "black" {} _Depth ("depth", 2D) = "black" {} }
    SubShader
    {
        ZTest Always Cull Off ZWrite Off
        CGINCLUDE
        #include "UnityCG.cginc"
        sampler2D _MainTex, _Sil, _Depth;
        float4 _GsWorld;   // xy = world units across the screen, z = groundTilt t
        float4 _GsLight;   // x = -cos a, y = -sin a * t + tan e (screen move per unit L), z = tan e, w = sin a * t
        float4 _GsMarch;   // x = step in L (world), y = number of steps, z = minimum tolerance (world)
        float4 _GsBox;     // all bodies' screen box in uv (x0, y0, x1, y1)
        float4 _GsShade;   // x = opacity, y = falloff (0..1: how much lighter the far end is than at the feet)
        float4 _GsBlurDir; // blur pass (global, set per pass): xy = uv per tap, z = radius in taps
        float4 _GsMap;     // composite: background-camera uv -> screen uv (xy scale, zw offset)
        float4 _GsDc0, _GsDc1, _GsDc2, _GsDc3;   // fallback depth scale per body id (depthRange x width, world units)
        sampler2D _GsFit;  // 16 x 1: r = c of body id (texel id - 1)
        float4 _GsPx;      // xy = screen size in px
        float4 _GsB0, _GsB1, _GsB2, _GsB3, _GsB4, _GsB5, _GsB6, _GsB7, _GsB8, _GsB9, _GsB10, _GsB11, _GsB12, _GsB13, _GsB14, _GsB15;   // body boxes, uv
        ENDCG

        // Passes are numbered by their ORDER in this file (Unity), not by these labels: keep 0 cast, 1 composite, 2 fit,
        // 3 blur in this order (2026-11-14: a blur pass inserted before the fit swapped them: 15 FPS, garbage shadow).
        Pass // 0 cast
        {
            Blend Off
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.5
            float4 frag (v2f_img i) : SV_Target
            {
                float2 dirUv = _GsLight.xy / _GsWorld.xy;           // uv per unit L
                float Lmax = _GsMarch.x * _GsMarch.y;
                // skip pixels whose ray cannot reach any body
                float2 e = i.uv + dirUv * Lmax;
                float2 lo = min(i.uv, e), hi = max(i.uv, e);
                if (hi.x < _GsBox.x || lo.x > _GsBox.z || hi.y < _GsBox.y || lo.y > _GsBox.w) return 0;
                float py = i.uv.y * _GsWorld.y;
                int n = (int)_GsMarch.y;
                [loop] for (int k = 0; k <= n; k++)
                {
                    float L = k * _GsMarch.x;
                    float2 q = i.uv + dirUv * L;
                    if (q.x < 0 || q.x > 1 || q.y < 0 || q.y > 1) continue;
                    float4 b = tex2Dlod(_Sil, float4(q, 0, 0));
                    if (b.a < 0.5) continue;
                    float3 d = tex2Dlod(_Depth, float4(q, 0, 0)).rgb;
                    // body's ground (screen y, world units) under q, and the ray's ground position
                    float2 f = tex2Dlod(_GsFit, float4((b.a - 0.5) / 16.0, 0.5, 0, 0)).rg;   // c (relative to the box bottom), D
                    float4 bxs[16] = { _GsB0, _GsB1, _GsB2, _GsB3, _GsB4, _GsB5, _GsB6, _GsB7, _GsB8, _GsB9, _GsB10, _GsB11, _GsB12, _GsB13, _GsB14, _GsB15 };
                    f.r += bxs[clamp((int)(b.a + 0.5) - 1, 0, 15)].y * _GsWorld.y;
                    float zc = b.r * f.g + d.r;                                    // middle of the slab
                    float yg;
                    yg = f.r + zc * _GsWorld.z;                                    // standing and lifted alike (fit pass)
                    float ys = py - L * _GsLight.w;
                    if (abs(ys - yg) <= d.r * _GsWorld.z + _GsMarch.z)
                    {
                        // Step 5 falloff: the march runs from the ground up, so this is the lowest part of the body that
                        // blocks the light; its height as a share of the body's height (box + a flyer's gap) sets how light
                        // the shadow is: full at the feet, 1 - falloff at the top.
                        float4 bb = bxs[clamp((int)(b.a + 0.5) - 1, 0, 15)];
                        float Hbody = max((bb.w - bb.y) * _GsWorld.y + b.b, 1e-3);
                        float H = L * _GsLight.z;
                        return float4(1 - _GsShade.y * saturate(H / Hbody), 0, 0, 1);
                    }
                }
                return 0;
            }
            ENDCG
        }

        Pass // 1 composite
        {
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.5
            float4 frag (v2f_img i) : SV_Target
            {
                // the background camera's picture is larger than the screen (fitBackground: 3414x1707 vs 2560x1440 [verified,
                // F8 dump]); _GsMap maps its uv to screen uv (= the shadow's uv): xy scale, zw offset (2026-11-14)
                float2 uv = i.uv * _GsMap.xy + _GsMap.zw;
                if (uv.x < 0 || uv.x > 1 || uv.y < 0 || uv.y > 1) return 0;
                float c = tex2D(_MainTex, uv).r;
                return float4(0, 0, 0, saturate(c * _GsShade.x));
            }
            ENDCG
        }

        Pass // 2 fit
        {
            Blend Off
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.5
            float4 frag (v2f_img i) : SV_Target
            {
                int k = clamp((int)(i.uv.x * 16), 0, 15);
                float4 bx[16] = { _GsB0, _GsB1, _GsB2, _GsB3, _GsB4, _GsB5, _GsB6, _GsB7, _GsB8, _GsB9, _GsB10, _GsB11, _GsB12, _GsB13, _GsB14, _GsB15 };
                float4 B = bx[k];
                float dc[16] = { _GsDc0.x, _GsDc0.y, _GsDc0.z, _GsDc0.w, _GsDc1.x, _GsDc1.y, _GsDc1.z, _GsDc1.w,
                                 _GsDc2.x, _GsDc2.y, _GsDc2.z, _GsDc2.w, _GsDc3.x, _GsDc3.y, _GsDc3.z, _GsDc3.w };
                float id = k + 1;
                float t = _GsWorld.z;
                float Wy = _GsWorld.y, Wx = _GsWorld.x;
                // Same as tools/gpu_shadow/sim/shadow_sim.py fit_robust (approved in the simulator).
                float2 px = float2(2.0 / _GsPx.x, 2.0 / _GsPx.y);          // 2 screen px
                float u0 = max(B.x, 0), u1 = min(B.z, 1), v0 = max(B.y, 0), v1 = min(B.w, 1);
                // A. the figure's own box (the texture has empty margins), every 4 px
                float4 F = float4(2, 2, -1, -1);
                [loop] for (float ua = u0; ua <= u1; ua += px.x * 2)
                [loop] for (float va = v0; va <= v1; va += px.y * 2)
                    if (abs(tex2Dlod(_Sil, float4(ua, va, 0, 0)).a - id) < 0.5) F = float4(min(F.x, ua), min(F.y, va), max(F.z, ua), max(F.w, va));
                if (F.z < F.x) return float4(0, dc[k], 0, 0);
                F.y = max(v0, F.y - px.y * 2);                              // the coarse scan may stop up to 4 px above the soles
                float Wa = F.z - F.x, Ha = F.w - F.y, vtop = F.y + Ha * 0.25;
                // B. front sole = the lowest body pixel of the figure's bottom quarter; C. rear sole = the lowest one at
                //    least 20 % of the FIGURE width away
                float4 s1 = float4(0, 1e6, 0, 0), s2 = float4(0, 1e6, 0, 0);   // u, v, -, lifted
                [loop] for (float u = F.x; u <= F.z; u += px.x)
                [loop] for (float v = F.y; v <= vtop; v += px.y)
                {
                    float4 b = tex2Dlod(_Sil, float4(u, v, 0, 0));
                    if (abs(b.a - id) < 0.5) { if (v < s1.y) s1 = float4(u, v, 0, b.b); break; }
                }
                [loop] for (float u2 = F.x; u2 <= F.z; u2 += px.x)
                {
                    if (abs(u2 - s1.x) < Wa * 0.2) continue;
                    [loop] for (float v = F.y; v <= vtop; v += px.y)
                    {
                        float4 b = tex2Dlod(_Sil, float4(u2, v, 0, 0));
                        if (abs(b.a - id) < 0.5) { if (v < s2.y) s2 = float4(u2, v, 0, b.b); break; }
                    }
                }
                float D = dc[k], ok = 0, dy = 0, c = s1.y * Wy;
                if (s1.y < 1e5 && s2.y < 1e5 && s1.w < 0.5)
                {
                    // D. each foot: mean depth s and thickness h over its lowest band (4 % of the figure width each side,
                    //    6 % of its height up), away from the outline (all 4 neighbours 2 px out are this body)
                    float4 acc[2] = { float4(0, 0, 0, 0), float4(0, 0, 0, 0) };   // sum s, sum h, n
                    float2 sole[2] = { s1.xy, s2.xy };
                    [unroll] for (int f = 0; f < 2; f++)
                    {
                        [loop] for (float fu = sole[f].x - Wa * 0.04; fu <= sole[f].x + Wa * 0.04; fu += px.x)
                        [loop] for (float fv = sole[f].y; fv <= sole[f].y + Ha * 0.06; fv += px.y)
                        {
                            float4 b = tex2Dlod(_Sil, float4(fu, fv, 0, 0));
                            if (abs(b.a - id) >= 0.5) continue;
                            if (abs(tex2Dlod(_Sil, float4(fu + px.x, fv, 0, 0)).a - id) >= 0.5 || abs(tex2Dlod(_Sil, float4(fu - px.x, fv, 0, 0)).a - id) >= 0.5 ||
                                abs(tex2Dlod(_Sil, float4(fu, fv + px.y, 0, 0)).a - id) >= 0.5 || abs(tex2Dlod(_Sil, float4(fu, fv - px.y, 0, 0)).a - id) >= 0.5) continue;
                            acc[f] += float4(b.r, tex2Dlod(_Depth, float4(fu, fv, 0, 0)).r, 1, 0);
                        }
                    }
                    if (acc[0].z > 0 && acc[1].z > 0)
                    {
                        float sF = acc[0].x / acc[0].z, hF = acc[0].y / acc[0].z, sR = acc[1].x / acc[1].z, hR = acc[1].y / acc[1].z;
                        dy = (s2.y - s1.y) * Wy;
                        float ds = sR - sF;
                        // E. both soles are on the ground: D = (dy / t - (hR - hF)) / ds, within 0.1-2 x
                        //    figure width, else depthRange x width; the ground goes through the front sole's slab middle.
                        float Dsol = ds >= 0.03 ? (dy / t - (hR - hF)) / ds : -1;
                        if (Dsol >= 0.1 * Wa * Wx && Dsol <= 2 * Wa * Wx) { D = Dsol; ok = 1; c = s1.y * Wy - (sF * D + hF) * t; }
                    }
                }
                if (s1.w > 0.5)
                {
                    // Lifted (flyer, Float, approved in the simulator): the gap is empty space under the
                    // sprite, so the body is placed with its lowest 3D point exactly the gap above the ground (the shadow
                    // buffer's b = the plugin's effective gap: 2 x the gap to the default shadow's base). No point is lower
                    // than the gap, so no shadow starts closer than gap / tan(elevation). Whole figure, every 4 px.
                    float best = 1e6;
                    [loop] for (float u4 = F.x; u4 <= F.z; u4 += px.x * 2)
                    [loop] for (float v = F.y; v <= F.w; v += px.y * 2)
                    {
                        float4 b = tex2Dlod(_Sil, float4(u4, v, 0, 0));
                        if (abs(b.a - id) < 0.5) best = min(best, v * Wy - (b.r * D + tex2Dlod(_Depth, float4(u4, v, 0, 0)).r) * t);
                    }
                    c = best - s1.w;
                    ok = 0;
                }
                else if (ok < 0.5)
                {
                    // fallback ground: the body slid down until its lowest point in 3D touches it (bottom quarter)
                    float best = 1e6;
                    [loop] for (float u3 = F.x; u3 <= F.z; u3 += px.x)
                    [loop] for (float v = F.y; v <= vtop; v += px.y)
                    {
                        float4 b = tex2Dlod(_Sil, float4(u3, v, 0, 0));
                        if (abs(b.a - id) < 0.5) best = min(best, v * Wy - (b.r * D + tex2Dlod(_Depth, float4(u3, v, 0, 0)).r) * t);
                    }
                    c = best;
                }
                // c is stored relative to the box bottom, so it stays right while the fit is only redone every few frames
                return float4(c - B.y * Wy, D, ok, dy);
            }
            ENDCG
        }

        Pass // 3 blur (step 5): separable Gaussian, run once across and once down (_GsBlurDir set per pass)
        {
            Blend Off
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.5
            float4 frag (v2f_img i) : SV_Target
            {
                float n = _GsBlurDir.z;
                if (n < 0.5) return float4(tex2D(_MainTex, i.uv).r, 0, 0, 1);
                float sigma = max(n * 0.5, 0.5), sum = 0, wsum = 0;
                [loop] for (int k = -32; k <= 32; k++)
                {
                    if (abs(k) > n) continue;
                    float w = exp(-(k * k) / (2 * sigma * sigma));
                    sum += w * tex2Dlod(_MainTex, float4(i.uv + _GsBlurDir.xy * k, 0, 0)).r;
                    wsum += w;
                }
                return float4(sum / wsum, 0, 0, 1);
            }
            ENDCG
        }
    }
}
