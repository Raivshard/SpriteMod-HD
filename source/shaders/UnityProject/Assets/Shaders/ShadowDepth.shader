// Step 3 (2026-11-13): GPU depth map from the outlines, by jump flooding (half resolution).
// Pass 0 seed:    _MainTex = shadow buffer (ShadowSil output). A pixel where the body id changes to a
//                 neighbour (2 full-res px away) is a seed: out = its own uv; else (-1, -1).
// Pass 1 jump:    _MainTex = seeds, _GsStep.xy = jump in uv. Keeps the nearest seed of 9 taps.
// Pass 2 resolve: _MainTex = seeds, _Sil = shadow buffer. d = world distance to the nearest outline;
//                 half-thickness h = R (1 - (1 - min(d / R, 1))^2) (round edge, flat middle), R per body
//                 id from _GsR0.._GsR3 (ids 1..16, world units). out r = h (0 outside bodies), g = R.
// _GsWorld.xy = world units across the screen (x, y).
Shader "SpriteMod/ShadowDepth"
{
    Properties { _MainTex ("in", 2D) = "black" {} _Sil ("buffer", 2D) = "black" {} }
    SubShader
    {
        ZTest Always Cull Off ZWrite Off Blend Off
        CGINCLUDE
        #include "UnityCG.cginc"
        sampler2D _MainTex, _Sil;
        float4 _MainTex_TexelSize, _Sil_TexelSize;
        float4 _GsStep, _GsWorld, _GsR0, _GsR1, _GsR2, _GsR3;
        float Dist2 (float2 a, float2 b) { float2 d = (a - b) * _GsWorld.xy; return dot(d, d); }
        ENDCG

        Pass // 0 seed
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.5
            float4 frag (v2f_img i) : SV_Target
            {
                float2 t = _Sil_TexelSize.xy * 2;
                float id = tex2D(_Sil, i.uv).a;
                float2 o[4] = { float2(t.x, 0), float2(-t.x, 0), float2(0, t.y), float2(0, -t.y) };
                bool edge = false;
                [unroll] for (int k = 0; k < 4; k++)
                {
                    float4 n = tex2D(_Sil, i.uv + o[k]);
                    float nid = n.a;
                    if (abs(nid - id) > 0.5) edge = true;
                }
                return edge ? float4(i.uv, 0, 1) : float4(-1, -1, 0, 0);
            }
            ENDCG
        }

        Pass // 1 jump
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.5
            float4 frag (v2f_img i) : SV_Target
            {
                float2 best = float2(-1, -1);
                float bd = 1e20;
                [unroll] for (int y = -1; y <= 1; y++)
                [unroll] for (int x = -1; x <= 1; x++)
                {
                    float2 s = tex2Dlod(_MainTex, float4(i.uv + float2(x, y) * _GsStep.xy, 0, 0)).xy;
                    if (s.x >= 0)
                    {
                        float d = Dist2(s, i.uv);
                        if (d < bd) { bd = d; best = s; }
                    }
                }
                return float4(best, 0, best.x >= 0 ? 1 : 0);
            }
            ENDCG
        }

        Pass // 2 resolve
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.5
            float BodyR (float id)
            {
                float r[16] = { _GsR0.x, _GsR0.y, _GsR0.z, _GsR0.w, _GsR1.x, _GsR1.y, _GsR1.z, _GsR1.w,
                                _GsR2.x, _GsR2.y, _GsR2.z, _GsR2.w, _GsR3.x, _GsR3.y, _GsR3.z, _GsR3.w };
                int k = clamp((int)(id + 0.5) - 1, 0, 15);
                return max(r[k], 1e-3);
            }
            float4 frag (v2f_img i) : SV_Target
            {
                float4 b = tex2D(_Sil, i.uv);
                if (b.a < 0.5) return 0;
                float2 s = tex2D(_MainTex, i.uv).xy;
                float R = BodyR(b.a);
                float d = s.x >= 0 ? sqrt(Dist2(s, i.uv)) : R;
                float q = 1 - min(d / R, 1);
                return float4(R * (1 - q * q), R, 0, 1);
            }
            ENDCG
        }
    }
}
