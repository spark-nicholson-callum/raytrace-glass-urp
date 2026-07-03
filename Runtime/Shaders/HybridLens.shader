Shader "Custom/HybridLens"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "LensGather"
            Tags { "LightMode" = "HybridLens/Gather" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };


            struct Varyings
            {
                float4 pos : SV_POSITION;
                float3 worldNormal : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                OUT.pos = TransformObjectToHClip(IN.vertex.xyz);
                OUT.worldNormal = TransformObjectToWorldNormal(IN.normal);

                return OUT;
            }

            float4 frag(Varyings IN) : SV_TARGET
            {
                float3 normal = normalize(IN.worldNormal);
                float3 colorNormal = normal * 0.5 + 0.5;

                return float4(colorNormal, 1.0);
            }

            ENDHLSL
        }
    }
}

