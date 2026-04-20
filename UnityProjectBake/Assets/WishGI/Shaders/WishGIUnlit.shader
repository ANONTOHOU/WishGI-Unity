Shader "WishGI/UnlitProbe"
{
    Properties
    {
        _ProbeMap ("Probe Map", 2D) = "white" {}
        _ProbeCount ("Probe Count", Float) = 128
        _TexelsPerProbe ("Texels Per Probe", Float) = 7
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _UseBaseMap ("Use Base Map", Range(0,1)) = 1
        _GIIntensity ("GI Intensity", Float) = 1
        _UsePiNormalize ("Use 1/PI", Range(0,1)) = 0
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
            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
                float _ProbeCount;
                float _TexelsPerProbe;
                float4 _BaseColor;
                float _UseBaseMap;
                float _GIIntensity;
                float _UsePiNormalize;
                float4 _EmissionTint;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv0        : TEXCOORD0;
                float4 uv2        : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float3 giColor    : TEXCOORD1;
                float2 uv0        : TEXCOORD2;
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = posInputs.positionCS;
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.uv0 = IN.uv0;
                // 在顶点阶段评估 GI，减少片元阶段负担。
                OUT.giColor = SampleWishGI(TEXTURE2D_ARGS(_ProbeMap, sampler_ProbeMap), _TexelsPerProbe, _ProbeCount, IN.uv2, OUT.normalWS);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float3 baseMap = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv0).rgb;
                float3 baseColor = lerp(_BaseColor.rgb, baseMap * _BaseColor.rgb, saturate(_UseBaseMap));

                // 将探针结果按“漫反射受光”语义与材质反射率调制。
                float piFactor = lerp(1.0, 0.31830988618, saturate(_UsePiNormalize));
                float3 gi = IN.giColor * baseColor * _GIIntensity * piFactor;

                // _EmissionTint 保留为最终调色/亮度微调。
                gi *= _EmissionTint.rgb;
                return half4(gi, 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
