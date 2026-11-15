// Step 1 smoke test (2026-11-13): proves a shader built here loads and runs in FF5 PR.
// The plugin blits it into a 64x64 render texture once and reads the centre pixel:
// expected (0.5, 0.5, 0.25, 1) = uv x, uv y, and a loop sum (the GPU ran real per-pixel code).
Shader "SpriteMod/SmokeTest"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float acc = 0;
                [loop] for (int k = 0; k < 16; k++) acc += 1.0 / 16.0;
                return float4(i.uv.x, i.uv.y, 0.25 * acc, 1);
            }
            ENDCG
        }
    }
}
