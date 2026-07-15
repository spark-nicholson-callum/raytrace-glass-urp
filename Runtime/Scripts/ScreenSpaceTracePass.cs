using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace CallumNicholson.RaytraceGlassURP
{
    class ScreenSpaceTracePass : ScriptableRenderPass
    {
        private const int FallbackSize = 32;
        private ComputeShader lensCompute;
        private Texture skybox;
        private int clearKernel;
        private int setupKernel;
        private int traceKernel;

        private HybridLens currentLens;
        private ComputeBuffer lensNormals;
        private ComputeBuffer lensIndices;

        private RTHandle skyboxHandle;

        public ScreenSpaceTracePass(ComputeShader shader, Texture skybox)
        {
            lensCompute = shader;
            clearKernel = lensCompute.FindKernel("ClearOutput");
            setupKernel = lensCompute.FindKernel("ArgsSetup");
            traceKernel = lensCompute.FindKernel("TraceLens");

            this.skybox = skybox;

            if (skybox != null)
            {
                skyboxHandle = RTHandles.Alloc(skybox);
            }

            UpdateLensData();
        }

        public void Dispose()
        {
            if (skyboxHandle != null) skyboxHandle.Release();
            if (lensNormals != null) lensNormals.Release();
            if (lensIndices != null) lensIndices.Release();
        }

        private void UpdateLensData()
        {
            currentLens = HybridLens.ActiveLens;

            if (lensNormals != null) lensNormals.Release();
            if (lensIndices != null) lensIndices.Release();

            if (currentLens == null) return;

            Mesh lensMesh = currentLens.LensMesh;
            Vector3[] normals = lensMesh.normals;
            int[]     indices = lensMesh.triangles;

            lensNormals = new ComputeBuffer(normals.Length, 12);
            lensNormals.SetData(normals);

            lensIndices = new ComputeBuffer(indices.Length, 4);
            lensIndices.SetData(indices);
        }

        private class PassData
        {
            public ComputeShader Compute;
            public int ClearKernel;
            public int SetupKernel;
            public int TraceKernel;

            public int ClearGroupsX;
            public int ClearGroupsY;

            public TextureHandle NormalTexture;
            public TextureHandle DepthTexture;
            public TextureHandle CameraDepthTexture;
            public TextureHandle OpaqueTexture;
            public TextureHandle RefractionOutputTexture;
            public TextureHandle ReflectionOutputTexture;
            public TextureHandle SkyboxTexture;
            public BufferHandle ActivePixelsBuffer;
            public BufferHandle ArgsBuffer;

            public Matrix4x4 ViewProj;
            public Matrix4x4 InverseViewProj;
            public RayTracingAccelerationStructure Rtas;
            public Vector3 CameraPos;
            public Matrix4x4 SkyRotation;

            public ComputeBuffer LensNormalsBuffer;
            public ComputeBuffer LensIndicesBuffer;
            public Matrix4x4 LensInverseLocalToWorld;

            public BufferHandle OccludedRayBuffer;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (HybridLens.ActiveLens != currentLens) UpdateLensData();

            // TODO // There is no real reason to call this every frame (it is easier though)
            RayTracingSceneManager.Instance.RebuildSceneData();

            var lensData     = frameData.Get<HybridLensRendererFeature.HybridLensData>();
            var cameraData   = frameData.Get<UniversalCameraData>();
            var resourceData = frameData.Get<UniversalResourceData>();

            int width = cameraData.cameraTargetDescriptor.width;
            int height = cameraData.cameraTargetDescriptor.height;

            var texDesc = cameraData.cameraTargetDescriptor;
            texDesc.enableRandomWrite = true;
            texDesc.depthBufferBits = 0;

            TextureHandle refractionOutputTextureHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, texDesc, "_RefractionOutputTexture", false);
            TextureHandle reflectionOutputTextureHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, texDesc, "_ReflectionOutputTexture", false);

            lensData.RefractionOutputTextureHandle = refractionOutputTextureHandle;
            lensData.ReflectionOutputTextureHandle = reflectionOutputTextureHandle;

            BufferDesc argsDesc = new BufferDesc(4, sizeof(uint))
            {
                name = "Indirect Args Buffer",
                target = GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured
            };
            BufferHandle argsHandle = renderGraph.CreateBuffer(argsDesc);

            var bufferDesc = new BufferDesc(width * height, FallbackSize)
            {
                name = "Occluded Ray Buffer",
                target = GraphicsBuffer.Target.Append
            };
            var occludedRayBufferHandle = renderGraph.CreateBuffer(bufferDesc);
            lensData.OccludedRayBufferHandle = occludedRayBufferHandle;

            using (var builder = renderGraph.AddComputePass<PassData>("Lens Trace Pass", out var passData))
            {
                passData.Compute = lensCompute;
                passData.ClearKernel = clearKernel;
                passData.SetupKernel = setupKernel;
                passData.TraceKernel = traceKernel;

                // TODO // Fetch the group size!
                passData.ClearGroupsX = (width  + 8 - 1) / 8;
                passData.ClearGroupsY = (height + 8 - 1) / 8;

                // Textures
                builder.UseTexture(lensData.NormalBufferHandle, AccessFlags.Read);
                passData.NormalTexture = lensData.NormalBufferHandle;

                builder.UseTexture(lensData.DepthBufferHandle, AccessFlags.Read);
                passData.DepthTexture = lensData.DepthBufferHandle;

                builder.UseTexture(resourceData.activeDepthTexture, AccessFlags.Read);
                passData.CameraDepthTexture = resourceData.activeDepthTexture;

                builder.UseTexture(resourceData.activeColorTexture, AccessFlags.Read);
                passData.OpaqueTexture = resourceData.activeColorTexture;

                builder.UseTexture(refractionOutputTextureHandle, AccessFlags.Write);
                passData.RefractionOutputTexture = refractionOutputTextureHandle;

                builder.UseTexture(reflectionOutputTextureHandle, AccessFlags.Write);
                passData.ReflectionOutputTexture = reflectionOutputTextureHandle;

                passData.SkyboxTexture = renderGraph.ImportTexture(skyboxHandle);
                builder.UseTexture(passData.SkyboxTexture, AccessFlags.Read);

                // Buffers
                passData.ActivePixelsBuffer = builder.UseBuffer(lensData.ActivePixelsBufferHandle, AccessFlags.Read);
                passData.ArgsBuffer = builder.UseBuffer(argsHandle, AccessFlags.Write);

                // Misc
                Matrix4x4 viewMatrix = cameraData.GetViewMatrix();
                Matrix4x4 projMatrix = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true);
                passData.ViewProj = projMatrix * viewMatrix;
                passData.InverseViewProj = passData.ViewProj.inverse;
                passData.Rtas = RayTracingSceneManager.Instance.Rtas;
                passData.CameraPos = cameraData.worldSpaceCameraPos;

                float skyRotation = 0f;
                if (RenderSettings.skybox != null && RenderSettings.skybox.HasProperty("_Rotation"))
                {
                    skyRotation = RenderSettings.skybox.GetFloat("_Rotation");
                }

                passData.SkyRotation = Matrix4x4.Rotate(Quaternion.Euler(0, skyRotation, 0));

                // Mesh Data
                passData.LensNormalsBuffer = lensNormals;
                passData.LensIndicesBuffer = lensIndices;
                passData.LensInverseLocalToWorld = currentLens.LensTransform.worldToLocalMatrix;

                passData.OccludedRayBuffer = builder.UseBuffer(occludedRayBufferHandle, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, ComputeGraphContext context) =>
                {
                    context.cmd.BuildRayTracingAccelerationStructure(data.Rtas);

                    // Clear the output texture
                    context.cmd.SetComputeTextureParam(data.Compute, data.ClearKernel, "_RefractionOutputTexture", data.RefractionOutputTexture);
                    context.cmd.SetComputeTextureParam(data.Compute, data.ClearKernel, "_ReflectionOutputTexture", data.ReflectionOutputTexture);
                    context.cmd.DispatchCompute(data.Compute, data.ClearKernel, data.ClearGroupsX, data.ClearGroupsY, 1);

                    // Generate the thread group sizes
                    context.cmd.CopyCounterValue(data.ActivePixelsBuffer, data.ArgsBuffer, 3 * sizeof(uint));

                    context.cmd.SetComputeBufferParam(data.Compute, data.SetupKernel, "_IndirectArgsBuffer", data.ArgsBuffer);
                    context.cmd.DispatchCompute(data.Compute, data.SetupKernel, 1, 1, 1);

                    // Run the lens tracing kernel
                    context.cmd.SetBufferCounterValue(data.OccludedRayBuffer, 0);

                    context.cmd.SetComputeTextureParam(data.Compute, data.TraceKernel, "_LensNormalBuffer", data.NormalTexture);
                    context.cmd.SetComputeTextureParam(data.Compute, data.TraceKernel, "_LensDepthBuffer", data.DepthTexture);
                    context.cmd.SetComputeTextureParam(data.Compute, data.TraceKernel, "_CameraDepthTexture", data.CameraDepthTexture);
                    context.cmd.SetComputeTextureParam(data.Compute, data.TraceKernel, "_CameraOpaqueTexture", data.OpaqueTexture);
                    context.cmd.SetComputeTextureParam(data.Compute, data.TraceKernel, "_RefractionOutputTexture", data.RefractionOutputTexture);
                    context.cmd.SetComputeTextureParam(data.Compute, data.TraceKernel, "_ReflectionOutputTexture", data.ReflectionOutputTexture);
                    context.cmd.SetComputeBufferParam(data.Compute, data.TraceKernel, "_ActivePixelsBufferRead", data.ActivePixelsBuffer);
                    context.cmd.SetComputeBufferParam(data.Compute, data.TraceKernel, "_IndirectArgsBuffer", data.ArgsBuffer);

                    context.cmd.SetComputeTextureParam(data.Compute, data.TraceKernel, "_SkyboxTexture", data.SkyboxTexture);

                    context.cmd.SetComputeMatrixParam(data.Compute, "_ViewProjMatrix", data.ViewProj);
                    context.cmd.SetComputeMatrixParam(data.Compute, "_InverseViewProjMatrix", data.InverseViewProj);
                    context.cmd.SetRayTracingAccelerationStructure(data.Compute, data.TraceKernel, "_SceneRtas", data.Rtas);
                    context.cmd.SetComputeVectorParam(data.Compute, "_CameraPos", data.CameraPos);
                    context.cmd.SetComputeMatrixParam(data.Compute, "_SkyRotation", data.SkyRotation);

                    context.cmd.SetComputeMatrixParam(data.Compute, "_LensInverseLocalToWorld", data.LensInverseLocalToWorld);
                    context.cmd.SetComputeBufferParam(data.Compute, data.TraceKernel, "_LensNormals", data.LensNormalsBuffer);
                    context.cmd.SetComputeBufferParam(data.Compute, data.TraceKernel, "_LensIndices", data.LensIndicesBuffer);

                    context.cmd.SetComputeBufferParam(data.Compute, data.TraceKernel, "_OccludedRayBuffer", data.OccludedRayBuffer);

                    context.cmd.DispatchCompute(data.Compute, data.TraceKernel, data.ArgsBuffer, 0);
                });
            }
        }
    }
}
