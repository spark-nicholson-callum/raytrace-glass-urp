Shader "Custom/HybridLens"
{
    Properties
    {
        _BaseColor ("Main Color", Color) = (1.0, 1.0, 1.0, 1.0)
        _Density ("Optical Density", Float) = 0.0
        _BaseIor ("Base Index of Refraction", Float) = 1.511
        _Dispersion ("Dispersion", Float) = 0.00425
    }

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

            struct Output
            {
                float4 normalTexture : SV_Target0;
                float4 materialTexture: SV_Target1;
            };

            TEXTURE2D_ARRAY(_LensDepthBuffer);

            CBUFFER_START(UnityPerMaterial)
                float _BaseIor;
                float _Dispersion;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                OUT.pos = TransformObjectToHClip(IN.vertex.xyz);
                OUT.screenPos = ComputeScreenPos(OUT.pos);
                OUT.worldNormal = TransformObjectToWorldNormal(IN.normal);

                return OUT;
            }

            Output frag(Varyings IN)
            {
                Output OUT;

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

                OUT.normalTexture = float4(colorNormal, 1.0);
                OUT.materialTexture = float4(_BaseIor, _Dispersion, 0.0, 1.0);

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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

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

                // TODO // Technically relys on index of refraction, but it's fine for now
                float3 viewDir = GetWorldSpaceNormalizeViewDir(IN.worldPos);
                float3 normal = normalize(IN.worldNorm);
                float NdotV = saturate(dot(normal, viewDir));
                float NdotVp = 1.0 - NdotV;
                float fresnel = 0.04 + (1.0 - 0.04) * NdotVp * NdotVp * NdotVp * NdotVp * NdotVp;

                float3 combinedColor = lerp(refractionColor, reflectionColor * 1.5, fresnel);

                // Calculate standard blinn-phong specular lighting
                Light mainLight = GetMainLight();
                float3 halfVector = normalize(mainLight.direction + viewDir);
                float NdotH = saturate(dot(normal, halfVector));

                // Probably make this a parameter
                float shininess = 256;
                float3 specularHighlight = mainLight.color * pow(NdotH, shininess);

                float3 finalColor = combinedColor + specularHighlight;

                return float4(finalColor, 1.0);
            }

            ENDHLSL
        }
    }
}

