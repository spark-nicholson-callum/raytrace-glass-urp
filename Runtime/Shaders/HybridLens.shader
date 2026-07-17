Shader "Custom/HybridLens"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "GlassDepthPrepass"
            Tags {"LightMode" = "HybridLens/DepthPrepass"}

            ColorMask 0
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 position : POSITION;
            };

            struct Varyings
            {
                float4 pos : SV_POSITION;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.pos = TransformObjectToHClip(IN.position.xyz);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_TARGET
            {
                // We only care about depth, colour o
                return 0;
            }
            ENDHLSL
        }

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
                float4 screenPos : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
            };

            TEXTURE2D_ARRAY(_LensDepthBuffer);

            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                OUT.pos = TransformObjectToHClip(IN.vertex.xyz);
                OUT.screenPos = ComputeScreenPos(OUT.pos);
                OUT.worldNormal = TransformObjectToWorldNormal(IN.normal);

                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target0
            {
                float2 screenUv = IN.screenPos.xy / IN.screenPos.w;
                float currentDepth = IN.pos.z;
                float closestGlass = SAMPLE_TEXTURE2D_ARRAY(_LensDepthBuffer, sampler_PointClamp, screenUv, 0).r;

                currentDepth = LinearEyeDepth(currentDepth, _ZBufferParams);
                closestGlass = LinearEyeDepth(closestGlass, _ZBufferParams);

                if (currentDepth > closestGlass + 0.01)
                {
                    discard;
                }

                float3 normal = normalize(IN.worldNormal);
                float3 colorNormal = normal * 0.5 + 0.5;

                return float4(colorNormal, 1.0);
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
                float3 normal : NORMAL;
            };

            struct Varyings
            {
                float4 pos : SV_POSITION;
                float4 screenPos : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float3 worldNorm : TEXCOORD2;
            };

            TEXTURE2D_ARRAY(_LensDepthBuffer);

            TEXTURE2D(_RefractionOutputTexture);
            SAMPLER(sampler_RefractionOutputTexture);

            TEXTURE2D(_ReflectionOutputTexture);
            SAMPLER(sampler_ReflectionOutputTexture);

            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                OUT.pos = TransformObjectToHClip(IN.vertex.xyz);
                OUT.screenPos = ComputeScreenPos(OUT.pos);
                OUT.worldPos = TransformObjectToWorld(IN.vertex.xyz);
                OUT.worldNorm = TransformObjectToWorldNormal(IN.normal);

                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                float2 screenUv = IN.screenPos.xy / IN.screenPos.w;

                float currentDepth = IN.pos.z;
                float closestGlass = SAMPLE_TEXTURE2D_ARRAY(_LensDepthBuffer, sampler_PointClamp, screenUv, 0).r;

                currentDepth = LinearEyeDepth(currentDepth, _ZBufferParams);
                closestGlass = LinearEyeDepth(closestGlass, _ZBufferParams);

                if (currentDepth > closestGlass + 0.01)
                {
                    discard;
                }

                float4 refractionColor = SAMPLE_TEXTURE2D(_RefractionOutputTexture, sampler_RefractionOutputTexture, screenUv);
                float4 reflectionColor = SAMPLE_TEXTURE2D(_ReflectionOutputTexture, sampler_ReflectionOutputTexture, screenUv);

                // Don't really need both, but it doesn't hurt
                clip(refractionColor.a - 0.01);
                clip(reflectionColor.a - 0.01);

                float3 viewDir = GetWorldSpaceNormalizeViewDir(IN.worldPos);
                float3 normal = normalize(IN.worldNorm);
                float NdotV = saturate(dot(normal, viewDir));
                float NdotVp = 1.0 - NdotV;
                float fresnel = 0.04 + (1.0 - 0.04) * NdotVp * NdotVp * NdotVp * NdotVp * NdotVp;

                float3 combinedColor = lerp(refractionColor, reflectionColor * 1.5, fresnel);

                return float4(combinedColor, 1.0);
            }

            ENDHLSL
        }
    }
}

