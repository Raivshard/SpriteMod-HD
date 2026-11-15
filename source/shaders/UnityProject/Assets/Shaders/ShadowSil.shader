// Step 2 (2026-11-13): one body's outline into the shadow buffer (ARGBHalf, cleared to 0).
// Drawn by the plugin with CommandBuffer.DrawRenderer on the game's own body renderer, so the game's
// mesh, transform, flip and UVs are used as they are; nothing is copied on the CPU.
// Per body (set on its material):
//   _MainTex the body's texture (per-body material)
//   _GsG0.._GsG3  up to 8 ground points (world x, y; two per vector), left to right; a lifted body's points
//                 are already lowered by its lift
//   _GsGN   x = number of points, y = lift (world units), z = body id (1..)
//   _DepthTex, _GsDepth  the sprite's depth map (KupoUI file + ".depth.png"), if any (2026-11-14)
// Output per covered pixel: r = front surface depth z_f (world units, from the depth map; 0 without one),
// g = height above the ground line (world units), b = lift, a = id (>= 1; 0 = no body: the mask).
Shader "SpriteMod/ShadowSil"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" }
        Pass
        {
            ZTest Always Cull Off ZWrite Off Blend Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            sampler2D _MainTex, _DepthTex;
            float4 _DepthTex_TexelSize;
            float4 _GsG0, _GsG1, _GsG2, _GsG3, _GsGN;
            float4 _GsDepth;   // x = 1 if this body has a depth map, y = full depth range D (world units)

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float2 wp : TEXCOORD1; };

            v2f vert (appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy;
                o.wp = mul(unity_ObjectToWorld, v.vertex).xy;
                return o;
            }

            // ground line y under world x: the polyline, flat beyond its ends
            float GroundAt (float x)
            {
                float2 p[8] = { _GsG0.xy, _GsG0.zw, _GsG1.xy, _GsG1.zw, _GsG2.xy, _GsG2.zw, _GsG3.xy, _GsG3.zw };
                int n = clamp((int)(_GsGN.x + 0.5), 1, 8);
                float g = x <= p[0].x ? p[0].y : p[n - 1].y;
                [unroll] for (int i = 0; i < 7; i++)
                {
                    if (i + 1 < n)
                    {
                        float2 a = p[i], b = p[i + 1];
                        if (x >= a.x && x <= b.x) g = (b.x - a.x) < 1e-4 ? a.y : lerp(a.y, b.y, (x - a.x) / (b.x - a.x));
                    }
                }
                return g;
            }

            // front surface depth from the sprite's depth map (bright = near), world units, 0 = the body's middle plane,
            // negative = toward the camera. The map's outermost pixel ring is black (cut 1 px in ComfyUI): take the
            // brightest of 4 neighbours 2 texels in; no map or still black = middle plane.
            float FrontZ (float2 uv)
            {
                if (_GsDepth.x < 0.5) return 0;
                float v = tex2D(_DepthTex, uv).r;
                if (v < 0.04)
                {
                    float2 t = _DepthTex_TexelSize.xy * 2;
                    v = max(max(tex2D(_DepthTex, uv + float2(t.x, 0)).r, tex2D(_DepthTex, uv - float2(t.x, 0)).r),
                            max(tex2D(_DepthTex, uv + float2(0, t.y)).r, tex2D(_DepthTex, uv - float2(0, t.y)).r));
                    if (v < 0.04) v = 0.5;
                }
                return (0.5 - v) * _GsDepth.y;
            }

            float4 frag (v2f i) : SV_Target
            {
                float a = tex2D(_MainTex, i.uv).a;
                clip(a - 0.5);
                return float4(FrontZ(i.uv), i.wp.y - GroundAt(i.wp.x), _GsGN.y, _GsGN.z);
            }
            ENDCG
        }
    }
}
