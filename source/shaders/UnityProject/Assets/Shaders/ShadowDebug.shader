// Debug view (step 2 2026-11-13, step 3 depth added): the shadow buffer drawn over the battle picture.
//   body pixels: grey by thickness from the depth map (black at the outline, white = full depth R)
//   yellow: within 2 screen px of the ground line (the contact the shadow will start from)
//   red:    below the ground line (the line sits too high there)
//   blue tint: lifted bodies (flyers, Float)
// _MainTex = shadow buffer, _Depth = depth map (r = half-thickness, g = R), _GsDbg.x = world units per screen pixel.
Shader "SpriteMod/ShadowDebug"
{
    Properties { _MainTex ("buffer", 2D) = "black" {} _Depth ("depth", 2D) = "black" {} }
    SubShader
    {
        Tags { "RenderType" = "Transparent" }
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            sampler2D _MainTex, _Depth, _Cast;
            float4 _GsDbg;

            float4 frag (v2f_img i) : SV_Target
            {
                float4 s = tex2D(_MainTex, i.uv);
                // step 4: the computed shadow (cast pass output) in red outside the bodies
                if (s.a < 0.5) { float cs = tex2D(_Cast, i.uv).r; return cs > 0.01 ? float4(1, 0, 0, 0.6 * cs) : float4(0, 0, 0, 0); }
                float px = max(_GsDbg.x, 1e-4);
                float h = s.g;
                if (h < -2 * px) return float4(1, 0, 0, 0.85);
                if (h <= 2 * px) return float4(1, 1, 0, 0.95);
                float2 dp = tex2D(_Depth, i.uv).rg;
                float v = dp.g > 0 ? saturate(dp.r / dp.g) : 0;
                float3 c = v.xxx;
                if (s.b > 0.5) c *= float3(0.55, 0.7, 1);
                return float4(c, 0.85);
            }
            ENDCG
        }
    }
}
