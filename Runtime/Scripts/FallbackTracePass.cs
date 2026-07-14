using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace CallumNicholson.RaytraceGlassURP
{
    class FallbackTracePass : ScriptableRenderPass
    {
        private ComputeShader fallbackCompute;
        private int setupKernel;
        private int traceKernel;

        private Texture skybox;
        private RTHandle skyboxHandle;

        public FallbackTracePass(ComputeShader shader, Texture skybox)
        {
            fallbackCompute = shader;
            setupKernel = fallbackCompute.FindKernel("ArgsSetup");
            traceKernel = fallbackCompute.FindKernel("FallbackTrace");

            this.skybox = skybox;
            if (skybox != null)
            {
                skyboxHandle = RTHandles.Alloc(skybox);
            }
        }

        public void Dispose()
        {
            if (skyboxHandle != null) skyboxHandle.Release();
        }

        private class PassData
        {
            public ComputeShader Compute;
            public int SetupKernel;
            public int TraceKernel;

            public TextureHandle OutputTexture;
            public BufferHandle ArgsBuffer;
            public BufferHandle OccludedRayBuffer;

            public Vector3 MainLightDirection;
            public Color MainLightColor;
            public Vector3 CameraPos;

            public TextureHandle SkyboxTexture;
            public Matrix4x4 SkyRotation;

            public Vector4 SHAr;
            public Vector4 SHAg;
            public Vector4 SHAb;
            public Vector4 SHBr;
            public Vector4 SHBg;
            public Vector4 SHBb;
            public Vector4 SHC;

            public TextureHandle GlobalTextureArray;
            public ComputeBuffer GlobalInstanceDataBuffer;
            public ComputeBuffer GlobalSubmeshDataBuffer;
            public ComputeBuffer GlobalIndexBuffer;
            public ComputeBuffer GlobalVertexBuffer;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var lensData = frameData.Get<HybridLensRendererFeature.HybridLensData>();
            var cameraData = frameData.Get<UniversalCameraData>();

            BufferDesc argsDesc = new BufferDesc(4, sizeof(uint))
            {
                name = "Indirect Args Buffer",
                target = GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured
            };
            BufferHandle argsHandle = renderGraph.CreateBuffer(argsDesc);

            Light mainLight = RenderSettings.sun;
            if (mainLight == null)
            {
                mainLight = Object.FindObjectsByType<Light>()
                    .Where(l => l.type == LightType.Directional)
                    .FirstOrDefault();
            }

            using (var builder = renderGraph.AddComputePass<PassData>("Trace Lookup Pass", out var passData))
            {
                passData.Compute = fallbackCompute;
                passData.SetupKernel = setupKernel;
                passData.TraceKernel = traceKernel;

                builder.UseTexture(lensData.OutputTextureHandle, AccessFlags.Write);
                passData.OutputTexture = lensData.OutputTextureHandle;

                passData.ArgsBuffer = builder.UseBuffer(argsHandle, AccessFlags.Write);
                passData.OccludedRayBuffer = builder.UseBuffer(lensData.OccludedRayBufferHandle, AccessFlags.Read);

                passData.GlobalTextureArray = renderGraph.ImportTexture(
                    RTHandles.Alloc(RayTracingSceneManager.Instance.GlobalTextureArray)
                );
                builder.UseTexture(passData.GlobalTextureArray);

                if (mainLight != null)
                {
                    passData.MainLightDirection = -mainLight.transform.forward;
                    passData.MainLightColor = mainLight.color.linear * mainLight.intensity;
                }
                else
                {
                    passData.MainLightDirection = Vector3.up;
                    passData.MainLightColor = Color.black.linear;
                }

                passData.CameraPos = cameraData.worldSpaceCameraPos;

                SphericalHarmonicsL2 sh = RenderSettings.ambientProbe;
                passData.SHAr = new Vector4(sh[0, 3], sh[0, 1], sh[0, 2], sh[0, 0] - sh[0, 6]);
                passData.SHAg = new Vector4(sh[1, 3], sh[1, 1], sh[1, 2], sh[1, 0] - sh[1, 6]);
                passData.SHAb = new Vector4(sh[2, 3], sh[2, 1], sh[2, 2], sh[2, 0] - sh[2, 6]);
                passData.SHBr = new Vector4(sh[0, 4], sh[0, 5], sh[0, 6] * 3.0f, sh[0, 7]);
                passData.SHBg = new Vector4(sh[1, 4], sh[1, 5], sh[1, 6] * 3.0f, sh[1, 7]);
                passData.SHBb = new Vector4(sh[2, 4], sh[2, 5], sh[2, 6] * 3.0f, sh[2, 7]);
                passData.SHC  = new Vector4(sh[0, 8], sh[1, 8], sh[2, 8], 1.0f);

                if (skyboxHandle != null)
                {
                    passData.SkyboxTexture = renderGraph.ImportTexture(skyboxHandle);
                    builder.UseTexture(passData.SkyboxTexture, AccessFlags.Read);
                }

                float skyRotation = 0f;
                if (RenderSettings.skybox != null && RenderSettings.skybox.HasProperty("_Rotation"))
                {
                    skyRotation = RenderSettings.skybox.GetFloat("_Rotation");
                }
                passData.SkyRotation = Matrix4x4.Rotate(Quaternion.Euler(0, skyRotation, 0));

                passData.GlobalInstanceDataBuffer = RayTracingSceneManager.Instance.GlobalInstanceDataBuffer;
                passData.GlobalSubmeshDataBuffer = RayTracingSceneManager.Instance.GlobalSubmeshDataBuffer;
                passData.GlobalIndexBuffer = RayTracingSceneManager.Instance.GlobalIndexBuffer;
                passData.GlobalVertexBuffer = RayTracingSceneManager.Instance.GlobalVertexBuffer;

                builder.SetRenderFunc(static (PassData data, ComputeGraphContext context) =>
                {
                    // ScreenSpaceTracePass already built the RTAS

                    // Setup indirect argument buffer
                    context.cmd.CopyCounterValue(data.OccludedRayBuffer, data.ArgsBuffer, 3 * sizeof(uint));
                    context.cmd.SetComputeBufferParam(data.Compute, data.SetupKernel, "_IndirectArgsBuffer", data.ArgsBuffer);
                    context.cmd.DispatchCompute(data.Compute, data.SetupKernel, 1, 1, 1);

                    // Call the fallback trace
                    context.cmd.SetComputeTextureParam(data.Compute, data.TraceKernel, "_RayTraceOutput", data.OutputTexture);
                    context.cmd.SetComputeBufferParam(data.Compute, data.TraceKernel, "_IndirectArgsBuffer", data.ArgsBuffer);
                    context.cmd.SetComputeBufferParam(data.Compute, data.TraceKernel, "_OccludedRayBuffer", data.OccludedRayBuffer);

                    context.cmd.SetComputeVectorParam(data.Compute, "_MainLightDirection", data.MainLightDirection);
                    context.cmd.SetComputeVectorParam(data.Compute, "_MainLightColor", data.MainLightColor);
                    context.cmd.SetComputeVectorParam(data.Compute, "_CameraPos", data.CameraPos);

                    if (data.SkyboxTexture.IsValid())
                    {
                        context.cmd.SetComputeTextureParam(data.Compute, data.TraceKernel, "_SkyboxTexture", data.SkyboxTexture);
                    }
                    context.cmd.SetComputeMatrixParam(data.Compute, "_SkyRotation", data.SkyRotation);

                    context.cmd.SetComputeVectorParam(data.Compute, "unity_SHAr", data.SHAr);
                    context.cmd.SetComputeVectorParam(data.Compute, "unity_SHAg", data.SHAg);
                    context.cmd.SetComputeVectorParam(data.Compute, "unity_SHAb", data.SHAb);
                    context.cmd.SetComputeVectorParam(data.Compute, "unity_SHBr", data.SHBr);
                    context.cmd.SetComputeVectorParam(data.Compute, "unity_SHBg", data.SHBg);
                    context.cmd.SetComputeVectorParam(data.Compute, "unity_SHBb", data.SHBb);
                    context.cmd.SetComputeVectorParam(data.Compute, "unity_SHC", data.SHC);

                    context.cmd.SetComputeTextureParam(data.Compute, data.TraceKernel, "_GlobalTextures", data.GlobalTextureArray);
                    context.cmd.SetComputeBufferParam(data.Compute, data.TraceKernel, "_GlobalInstanceData", data.GlobalInstanceDataBuffer);
                    context.cmd.SetComputeBufferParam(data.Compute, data.TraceKernel, "_GlobalSubmeshData", data.GlobalSubmeshDataBuffer);
                    context.cmd.SetComputeBufferParam(data.Compute, data.TraceKernel, "_GlobalIndices", data.GlobalIndexBuffer);
                    context.cmd.SetComputeBufferParam(data.Compute, data.TraceKernel, "_GlobalVertices", data.GlobalVertexBuffer);

                    context.cmd.DispatchCompute(data.Compute, data.TraceKernel, data.ArgsBuffer, 0);
                });
            }
        }
    }
}
