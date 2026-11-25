// Lava under the game's water ("it needs to be on top with a burn or linear blend, or a variable
// 'blend if' setting for color or black/white ranges"). Drawn per lava cell after the game's opaque tiles: the water as
// the game drew it (screen grab) is the TOP layer, the animated mobile lava frame (_MainTex) the BOTTOM layer.
// _Mode: 0 normal, 1 multiply, 2 screen, 3 overlay, 4 linear burn, 5 colour burn, 6 linear dodge, 7 soft light,
// 8 vivid light, 9 linear light, 10 color (Photoshop formulas; top = water or the edge PNG, bottom = lava).
// Blend if (Photoshop "This Layer" / "Underlying Layer", 0..1, split sliders): (black lo, black hi, white lo, white hi)
// on luminance; the blended result shows where both weights allow it, elsewhere the lava shows.
// Edge glow ("a slightly brighter edge to the top of the frame, something that mimics the brighter
// spots of the shader"): on cells flagged by vertex colour green = 1 (top edge of a channel), the user's painted layer
// _EdgeTex (its own colours, alpha = coverage; user: "the color property ... washes everything out") is blended over
// the result like a Photoshop layer, with its own mode and opacity, and blend-if ranges like
// the lava blend (user: "needs to be able to blend like fieldLavaBlend"): _EdgeTop on the water's luminance, _EdgeBase on
// the luminance of the blended lava under the glow. Optional tint _EdgeTint (alpha 1 = on; user: "I wanted the color
// setting to have that effect"): applied to the PNG like a Photoshop Color layer (tint hue + saturation, PNG brightness)
// before the blend, so the PNG's light and dark still drive the blend mode.
Shader "SpriteMod/LavaBlend"
{
    Properties
    {
        [PerRendererData] _MainTex ("Lava frame", 2D) = "white" {}
        _Mode ("Blend mode", Float) = 4
        _Opacity ("Opacity", Range(0, 1)) = 1
        _TopRange ("Blend if: water (b0, b1, w0, w1)", Vector) = (0, 0, 1, 1)
        _BaseRange ("Blend if: lava (b0, b1, w0, w1)", Vector) = (0, 0, 1, 1)
        _EdgeTex ("Edge mask", 2D) = "black" {}
        _FrameCols ("_MainTex width / 32", Float) = 16
        _FrameRows ("_MainTex height / 32 (frames use the bottom 32 px)", Float) = 1
        _EdgeTint ("Edge tint (Color mode on the PNG; alpha 0 = off)", Color) = (1, 1, 1, 0)
        _EdgeMode ("Edge blend mode", Float) = 0
        _EdgeOpacity ("Edge opacity", Range(0, 1)) = 0
        _EdgeTop ("Edge blend if: water (b0, b1, w0, w1)", Vector) = (0, 0, 1, 1)
        _EdgeBase ("Edge blend if: lava under it (b0, b1, w0, w1)", Vector) = (0, 0, 1, 1)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }
        Cull Off
        ZWrite Off
        ZTest LEqual
        GrabPass { "_SpriteModWaterGrab" }
        Pass
        {
            Blend One Zero
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _SpriteModWaterGrab;
            float _Mode, _Opacity;
            float4 _TopRange, _BaseRange;
            sampler2D _EdgeTex;
            float _FrameCols, _FrameRows, _EdgeMode, _EdgeOpacity;
            float4 _EdgeTop, _EdgeBase, _EdgeTint;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 grab : TEXCOORD1;
                fixed4 col : COLOR;
            };

            v2f vert(appdata_full v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy;
                o.grab = ComputeGrabScreenPos(o.pos);
                o.col = v.color;
                return o;
            }

            float lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }

            // Photoshop blend-if weight: hidden below b0, ramps up to b1; shown up to w0, ramps down to w1
            float blendIf(float L, float4 r)
            {
                float up = r.y > r.x ? saturate((L - r.x) / (r.y - r.x)) : step(r.x, L);
                float down = r.w > r.z ? 1.0 - saturate((L - r.z) / (r.w - r.z)) : step(L, r.z);
                return up * down;
            }

            // Photoshop's non-separable SetLum + ClipColor (PDF blend mode spec)
            float3 setLum(float3 c, float l)
            {
                c += l - lum(c);
                float L = lum(c), n = min(c.r, min(c.g, c.b)), x = max(c.r, max(c.g, c.b));
                if (n < 0.0) c = L + (c - L) * L / max(L - n, 0.0001);
                if (x > 1.0) c = L + (c - L) * (1.0 - L) / max(x - L, 0.0001);
                return c;
            }

            float3 blend(float3 t, float3 b, float m)
            {
                if (m < 0.5) return t;                                                       // normal
                if (m < 1.5) return t * b;                                                   // multiply
                if (m < 2.5) return 1.0 - (1.0 - t) * (1.0 - b);                             // screen
                if (m < 3.5) return b < 0.5 ? 2.0 * t * b : 1.0 - 2.0 * (1.0 - t) * (1.0 - b); // overlay
                if (m < 4.5) return t + b - 1.0;                                             // linear burn
                if (m < 5.5) return 1.0 - (1.0 - b) / max(t, 0.001);                         // colour burn
                if (m < 6.5) return t + b;                                                   // linear dodge
                if (m < 7.5) return (1.0 - 2.0 * t) * b * b + 2.0 * t * b;                   // soft light
                if (m < 8.5)                                                                 // vivid light
                {
                    float3 burn = 1.0 - (1.0 - b) / max(2.0 * t, 0.001);                     // t < 0.5: colour burn with 2t
                    float3 dodge = b / max(2.0 * (1.0 - t), 0.001);                          // t >= 0.5: colour dodge with 2t - 1
                    return lerp(burn, dodge, step(0.5, t));
                }
                if (m < 9.5) return b + 2.0 * t - 1.0;                                       // linear light
                return setLum(t, lum(b));                                                    // color: top hue + saturation, bottom brightness
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float4 lava = tex2D(_MainTex, i.uv);
                float3 water = tex2Dproj(_SpriteModWaterGrab, UNITY_PROJ_COORD(i.grab)).rgb;
                float3 mixed = saturate(blend(water, lava.rgb, _Mode));
                float w = _Opacity * i.col.a * blendIf(lum(water), _TopRange) * blendIf(lum(lava.rgb), _BaseRange);
                float3 outc = lerp(lava.rgb, mixed, w);
                if (i.col.g > 0.5 && _EdgeOpacity > 0.0)
                {
                    // cell-local uv: _MainTex is a strip of frames side by side
                    float2 euv = float2(frac(i.uv.x * _FrameCols), saturate(i.uv.y * _FrameRows));
                    float4 e = tex2D(_EdgeTex, euv);
                    float f = blendIf(lum(water), _EdgeTop) * blendIf(lum(outc), _EdgeBase);
                    float3 top = _EdgeTint.a > 0.5 ? setLum(_EdgeTint.rgb, lum(e.rgb)) : e.rgb;
                    float3 glow = saturate(blend(top, outc, _EdgeMode));
                    outc = lerp(outc, glow, saturate(e.a * f * _EdgeOpacity));
                }
                // outside the lava frame's own pixels (none: the frames are opaque) the water stays
                return fixed4(lerp(water, outc, lava.a), 1.0);
            }
            ENDCG
        }
    }
}
