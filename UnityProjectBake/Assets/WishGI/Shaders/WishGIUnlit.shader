Shader "WishGI/UnlitProbe"
{
    Properties
    {
        _ProbeMap ("Probe Map", 2D) = "white" {}
        _ProbeCount ("Probe Count", Float) = 128
        _TexelsPerProbe ("Texels Per Probe", Float) = 7
        _EmissionTint ("Emission Tint", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" }
        Pass
        {
            Name "ForwardUnlit"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _SCREEN_SPACE_OCCLUSION
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "WishGIProbe.hlsl"

            TEXTURE2D(_ProbeMap); SAMPLER(sampler_ProbeMap);
            CBUFFER_START(UnityPerMaterial)
                float _ProbeCount;
                float _TexelsPerProbe;
                float4 _EmissionTint;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 uv2        : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float3 giColor    : TEXCOORD1;
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = posInputs.positionCS;
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                // 在顶点阶段评估 GI，减少片元阶段负担，符合 WishGI 的轻量化目标。
                OUT.giColor = SampleWishGI(TEXTURE2D_ARGS(_ProbeMap, sampler_ProbeMap), _TexelsPerProbe, _ProbeCount, IN.uv2, OUT.normalWS);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                // _EmissionTint 用作全局亮度/色调微调。
                float3 gi = IN.giColor * _EmissionTint.rgb;
                return half4(gi, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
