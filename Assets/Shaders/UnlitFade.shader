Shader "Custom/UnlitFade"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Opacity ("Opacity", Range(0, 1)) = 1.0
        _BorderColor ("Border Color", Color) = (0, 1, 1, 1)
        _BorderWidth ("Border Width", Range(0, 0.1)) = 0.01
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            Name "UnlitFade"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 rawUV : TEXCOORD1;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _Opacity;
                float4 _BorderColor;
                float _BorderWidth;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.rawUV = input.uv; // untransformed UVs for border
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // Use raw UVs (0-1) for border, transformed UVs for texture
                float2 rawUV = input.rawUV;

                // Border detection: distance from nearest edge
                float distLeft   = rawUV.x;
                float distRight  = 1.0 - rawUV.x;
                float distBottom = rawUV.y;
                float distTop    = 1.0 - rawUV.y;
                float edgeDist = min(min(distLeft, distRight), min(distBottom, distTop));

                // Anti-aliased border using smoothstep
                float borderMask = 1.0 - smoothstep(_BorderWidth - fwidth(edgeDist), _BorderWidth, edgeDist);

                half4 texCol = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                half4 col = lerp(texCol, _BorderColor, borderMask);
                col.a *= _Opacity;

                return col;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
