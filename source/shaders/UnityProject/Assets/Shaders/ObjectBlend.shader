// Tiled map objects with the object layer's blend mode (fireplace embers on an Overlay layer).
// The scene as drawn so far (screen grab, once per frame) is the bottom layer, the object's sprite the top layer.
// _Mode follows Tiled's layer "mode" (Qt composition formulas): 0 normal, 1 add, 2 multiply, 3 screen, 4 overlay,
// 5 darken, 6 lighten, 7 color-dodge, 8 color-burn, 9 hard-light, 10 soft-light, 11 difference, 12 exclusion.
// Opacity = sprite alpha x vertex colour alpha (the plugin puts layer opacity x object opacity in SpriteRenderer.color).
Shader "SpriteMod/ObjectBlend"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite", 2D) = "white" {}
        _Mode ("Blend mode", Float) = 0
        _AlphaFromRgb ("Alpha = brightest channel (effect render textures)", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }
        Cull Off
        ZWrite Off
        ZTest LEqual
        // unnamed grab: taken right before each draw, in the camera drawing it (a named grab is shared per frame and
        // could come from another camera, e.g. the minimap redrawn while the player moves: blend flickered to normal)
        GrabPass { }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _GrabTexture;
            float _Mode, _AlphaFromRgb;

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

            // t = object (top), b = scene (bottom)
            float3 blend(float3 t, float3 b, float m)
            {
                if (m < 0.5) return t;                                                          // normal
                if (m < 1.5) return t + b;                                                      // add
                if (m < 2.5) return t * b;                                                      // multiply
                if (m < 3.5) return 1.0 - (1.0 - t) * (1.0 - b);                                // screen
                if (m < 4.5) return b < 0.5 ? 2.0 * t * b : 1.0 - 2.0 * (1.0 - t) * (1.0 - b);  // overlay
                if (m < 5.5) return min(t, b);                                                  // darken
                if (m < 6.5) return max(t, b);                                                  // lighten
                if (m < 7.5) return b / max(1.0 - t, 0.001);                                    // color dodge
                if (m < 8.5) return 1.0 - (1.0 - b) / max(t, 0.001);                            // color burn
                if (m < 9.5) return t < 0.5 ? 2.0 * t * b : 1.0 - 2.0 * (1.0 - t) * (1.0 - b);  // hard light
                if (m < 10.5) return (1.0 - 2.0 * t) * b * b + 2.0 * t * b;                     // soft light
                if (m < 11.5) return abs(t - b);                                                // difference
                return t + b - 2.0 * t * b;                                                     // exclusion
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float4 s = tex2D(_MainTex, i.uv);
                float3 scene = tex2Dproj(_GrabTexture, UNITY_PROJ_COORD(i.grab)).rgb;
                float a = _AlphaFromRgb > 0.5 ? max(s.r, max(s.g, s.b)) : s.a;
                if (_AlphaFromRgb > 0.5) s.rgb /= max(a, 0.001);   // un-premultiply the glow on black
                return fixed4(saturate(blend(s.rgb, scene, _Mode)), a * i.col.a);
            }
            ENDCG
        }
    }
}
