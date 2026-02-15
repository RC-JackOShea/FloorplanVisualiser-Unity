Shader "Custom/TronGrid"
{
    Properties
    {
        _GridColor ("Grid Color", Color) = (0, 1, 1, 1)
        _BackgroundColor ("Background Color", Color) = (0, 0, 0, 0)
        _GridSize ("Grid Size (metres)", Float) = 1.0
        _LineWidth ("Line Width", Range(0.001, 0.1)) = 0.02
        _Fade ("Fade", Range(0, 1)) = 1.0
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
            Name "TronGrid"

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
                float3 positionWS : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _GridColor;
                float4 _BackgroundColor;
                float _GridSize;
                float _LineWidth;
                float _Fade;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // World-space grid — stable regardless of quad size or position
                float2 worldPos = input.positionWS.xz;
                float2 grid = abs(frac(worldPos / _GridSize + 0.5) - 0.5);

                // Anti-aliased lines using screen-space derivatives
                float2 fw = fwidth(worldPos / _GridSize);
                float halfLine = _LineWidth * 0.5 / _GridSize;
                float2 lines = smoothstep(halfLine + fw, halfLine - fw, grid);
                float gridMask = saturate(lines.x + lines.y);

                // Blend grid colour over background
                half4 col = lerp(_BackgroundColor, _GridColor, gridMask);
                col.a *= _Fade;

                return col;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
