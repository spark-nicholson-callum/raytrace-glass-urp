Shader "Custom/HybridLens"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "LensGather"
            Tags { "LightMode" = "HybridLens/Gather" }
            ZWrite Off
            ZTest LEqual

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

            struct FragOutput
            {
                float4 normal : SV_Target0;
                float4 depth  : SV_Target1;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                OUT.pos = TransformObjectToHClip(IN.vertex.xyz);
                OUT.worldNormal = TransformObjectToWorldNormal(IN.normal);

                return OUT;
            }

            FragOutput frag(Varyings IN)
            {
                FragOutput OUT;

                float3 normal = normalize(IN.worldNormal);
                float3 colorNormal = normal * 0.5 + 0.5;

                OUT.normal = float4(colorNormal, 1.0);
                OUT.depth  = IN.pos.z;

                return OUT;
            }

            ENDHLSL
        }

        Pass
        {
            Name "LensProjector"
            Tags {"LightMode" = "HybridLens/Project"}

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 vertex : POSITION;
            };

            struct Varyings
            {
                float4 pos : SV_POSITION;
                float4 screenPos : TEXCOORD0;
            };

            TEXTURE2D(_RayTraceOutput);
            SAMPLER(sampler_RayTraceOutput);

            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                OUT.pos = TransformObjectToHClip(IN.vertex.xyz);
                OUT.screenPos = ComputeScreenPos(OUT.pos);

                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                float2 screenUv = IN.screenPos.xy / IN.screenPos.w;
                float4 rtColor = SAMPLE_TEXTURE2D(_RayTraceOutput, sampler_RayTraceOutput, screenUv);

                clip(rtColor.a - 0.01);
                return rtColor;
            }

            ENDHLSL
        }
    }
}

