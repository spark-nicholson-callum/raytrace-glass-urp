using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class HybridLensRendererFeature : ScriptableRendererFeature
{
    public class HybridLensData : ContextItem
    {
        public TextureHandle NormalBufferHandle;
        public TextureHandle DepthBufferHandle;
        public TextureHandle OutputTextureHandle;
        public BufferHandle ActivePixelsBufferHandle;
        public BufferHandle OccludedRayBufferHandle;

        public override void Reset()
        {
            NormalBufferHandle = TextureHandle.nullHandle;
            DepthBufferHandle = TextureHandle.nullHandle;
            OutputTextureHandle = TextureHandle.nullHandle;
            ActivePixelsBufferHandle = BufferHandle.nullHandle;
            OccludedRayBufferHandle = BufferHandle.nullHandle;
        }
    }

    public class GatherPass : ScriptableRenderPass
    {
        private ComputeShader lensCompute;
        private int compactionKernel;

        private ShaderTagId shaderTagId = new ShaderTagId("HybridLens/Gather");
        private FilteringSettings filteringSettings;

        public GatherPass(ComputeShader shader)
        {
            lensCompute = shader;
            compactionKernel = lensCompute.FindKernel("NormalCompaction");

            filteringSettings = new FilteringSettings(RenderQueueRange.opaque);
        }

        private class PassData
        {
            public ComputeShader Compute;
            public int Kernel;

            public int ThreadGroupsX;
            public int ThreadGroupsY;

            public RendererListHandle RendererList;
            public TextureHandle NormalBuffer;
            public BufferHandle ActivePixelsBuffer;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var lensData      = frameData.GetOrCreate<HybridLensData>();
            var renderingData = frameData.Get<UniversalRenderingData>();
            var cameraData    = frameData.Get<UniversalCameraData>();
            var lightData     = frameData.Get<UniversalLightData>();
            var resourceData  = frameData.Get<UniversalResourceData>();

            int width = cameraData.cameraTargetDescriptor.width;
            int height = cameraData.cameraTargetDescriptor.height;

            // Create the texture based on the camera texture
            var normalDesc = cameraData.cameraTargetDescriptor;
            normalDesc.colorFormat = RenderTextureFormat.ARGB32;
            normalDesc.depthBufferBits = 0;
            var normalBufferHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, normalDesc, "_LensNormalBuffer", false);
            lensData.NormalBufferHandle = normalBufferHandle;

            // Create the depth texture based on the camera texture
            var depthDesc = cameraData.cameraTargetDescriptor;
            depthDesc.colorFormat = RenderTextureFormat.RFloat;
            depthDesc.depthBufferBits = 0;
            var depthBufferHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, depthDesc, "_LensDepthBuffer", false);
            lensData.DepthBufferHandle = depthBufferHandle;

            // Build the renderer list
            var sortingCriteria = cameraData.defaultOpaqueSortFlags;
            var drawingSettings = CreateDrawingSettings(shaderTagId, renderingData, cameraData, lightData, sortingCriteria);

            var listParams = new RendererListParams(renderingData.cullResults, drawingSettings, filteringSettings);
            var rendererListHandle = renderGraph.CreateRendererList(listParams);

            // Create the active pixel buffer
            var bufferDesc = new BufferDesc(width * height, sizeof(uint) * 2)
            {
                name = "Active Lens Pixels Buffer",
                target = GraphicsBuffer.Target.Append
            };
            var activePixelsHandle = renderGraph.CreateBuffer(bufferDesc);
            lensData.ActivePixelsBufferHandle = activePixelsHandle;

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Lens Gatherer Pass", out var passData))
            {
                passData.RendererList = rendererListHandle;

                builder.UseRendererList(rendererListHandle);
                builder.SetRenderAttachment(normalBufferHandle, 0, AccessFlags.Write);
                builder.SetRenderAttachment(depthBufferHandle, 1, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    context.cmd.ClearRenderTarget(false, true, Color.clear);
                    context.cmd.DrawRendererList(data.RendererList);
                });
            }
            using (var builder = renderGraph.AddComputePass<PassData>("Lens Compaction Pass", out var passData))
            {
                passData.Compute = lensCompute;
                passData.Kernel  = compactionKernel;

                // TODO // Fetch the group size!
                passData.ThreadGroupsX = (width  + 8 - 1) / 8;
                passData.ThreadGroupsY = (height + 8 - 1) / 8;

                builder.UseTexture(normalBufferHandle, AccessFlags.Read);
                passData.NormalBuffer = normalBufferHandle;

                passData.ActivePixelsBuffer = builder.UseBuffer(activePixelsHandle, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, ComputeGraphContext context) =>
                {
                    context.cmd.SetBufferCounterValue(data.ActivePixelsBuffer, 0);
                    context.cmd.SetComputeTextureParam(data.Compute, data.Kernel, "_LensNormalBuffer", data.NormalBuffer);
                    context.cmd.SetComputeBufferParam(data.Compute, data.Kernel, "_ActivePixelsBufferWrite", data.ActivePixelsBuffer);

                    context.cmd.DispatchCompute(data.Compute, data.Kernel, data.ThreadGroupsX, data.ThreadGroupsY, 1);
                });
            }
        }
    }

    public class TracePass : ScriptableRenderPass
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

        private RayTracingAccelerationStructure rtas;
        private RTHandle skyboxHandle;

        public TracePass(ComputeShader shader, RayTracingAccelerationStructure rtas, Texture skybox)
        {
            lensCompute = shader;
            clearKernel = lensCompute.FindKernel("ClearOutput");
            setupKernel = lensCompute.FindKernel("ArgsSetup");
            traceKernel = lensCompute.FindKernel("TraceLens");

            this.rtas = rtas;
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
            public TextureHandle OutputTexture;
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

            var lensData     = frameData.Get<HybridLensData>();
            var cameraData   = frameData.Get<UniversalCameraData>();
            var resourceData = frameData.Get<UniversalResourceData>();

            int width = cameraData.cameraTargetDescriptor.width;
            int height = cameraData.cameraTargetDescriptor.height;

            var texDesc = cameraData.cameraTargetDescriptor;
            texDesc.enableRandomWrite = true;
            texDesc.colorFormat = RenderTextureFormat.ARGB32;
            texDesc.depthBufferBits = 0;

            TextureHandle outputTextureHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, texDesc, "_RayTraceOutput", false);

            lensData.OutputTextureHandle = outputTextureHandle;

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

                builder.UseTexture(outputTextureHandle, AccessFlags.Write);
                passData.OutputTexture = outputTextureHandle;

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
                passData.Rtas = rtas;
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
                    context.cmd.SetComputeTextureParam(data.Compute, data.ClearKernel, "_RayTraceOutput", data.OutputTexture);
                    context.cmd.DispatchCompute(data.Compute, data.ClearKernel, data.ClearGroupsX, data.ClearGroupsY, 1);

                    // Generate the thread group sizes
                    context.cmd.CopyCounterValue(data.ActivePixelsBuffer, data.ArgsBuffer, 3 * sizeof(uint));

                    context.cmd.SetComputeBufferParam(data.Compute, data.SetupKernel, "_IndirectArgsBuffer", data.ArgsBuffer);
                    context.cmd.DispatchCompute(data.Compute, data.SetupKernel, 1, 1, 1);

                    // Run the lens tracing kernel
                    context.cmd.SetComputeTextureParam(data.Compute, data.TraceKernel, "_LensNormalBuffer", data.NormalTexture);
                    context.cmd.SetComputeTextureParam(data.Compute, data.TraceKernel, "_LensDepthBuffer", data.DepthTexture);
                    context.cmd.SetComputeTextureParam(data.Compute, data.TraceKernel, "_CameraDepthTexture", data.CameraDepthTexture);
                    context.cmd.SetComputeTextureParam(data.Compute, data.TraceKernel, "_CameraOpaqueTexture", data.OpaqueTexture);
                    context.cmd.SetComputeTextureParam(data.Compute, data.TraceKernel, "_RayTraceOutput", data.OutputTexture);
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

    private class ProjectorPass : ScriptableRenderPass
    {
        private static readonly int rayTraceOutputId = Shader.PropertyToID("_RayTraceOutput");
        private ShaderTagId shaderTagId = new ShaderTagId("HybridLens/Project");
        private FilteringSettings filteringSettings;

        public ProjectorPass()
        {
            filteringSettings = new FilteringSettings(RenderQueueRange.all);
        }

        private class PassData
        {
            public TextureHandle OutputTexture;
            public RendererListHandle RendererList;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var lensData      = frameData.Get<HybridLensData>();
            var renderingData = frameData.Get<UniversalRenderingData>();
            var cameraData    = frameData.Get<UniversalCameraData>();
            var lightData     = frameData.Get<UniversalLightData>();
            var resourceData  = frameData.Get<UniversalResourceData>();

            var sortingCriteria = cameraData.defaultOpaqueSortFlags;
            var drawingSettings = CreateDrawingSettings(shaderTagId, renderingData, cameraData, lightData, sortingCriteria);

            var listParams = new RendererListParams(renderingData.cullResults, drawingSettings, filteringSettings);
            var rendererListHandle = renderGraph.CreateRendererList(listParams);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Lens Projection Pass", out var passData))
            {
                // Needed to enable setting the output texture
                builder.AllowGlobalStateModification(true);

                builder.UseRendererList(rendererListHandle);
                passData.RendererList = rendererListHandle;

                builder.UseTexture(lensData.OutputTextureHandle, AccessFlags.Read);
                passData.OutputTexture = lensData.OutputTextureHandle;

                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Read);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    context.cmd.SetGlobalTexture(rayTraceOutputId, data.OutputTexture);
                    context.cmd.DrawRendererList(data.RendererList);
                });
            }
        }
    }

    [SerializeField] private ComputeShader lensComputeShader;
    [SerializeField] private Cubemap skybox;

    private GatherPass gatherPass;
    private TracePass tracePass;
    private ProjectorPass projectorPass;

    private RayTracingAccelerationStructure rtas;

    public override void Create()
    {
        if (lensComputeShader == null) return;
        if (!SystemInfo.supportsRayTracing)
        {
            Debug.LogWarning("HybridLensFeature: Hardware Ray Tracing is not suppported!");
            return;
        }

        // Set up the RTAS
        var settings = new RayTracingAccelerationStructure.Settings();
        settings.layerMask = ~LayerMask.GetMask("UI");
        settings.managementMode = RayTracingAccelerationStructure.ManagementMode.Automatic;
        settings.rayTracingModeMask = RayTracingAccelerationStructure.RayTracingModeMask.DynamicTransform
                                    | RayTracingAccelerationStructure.RayTracingModeMask.Static;
        rtas = new RayTracingAccelerationStructure(settings);

        // Set up passes
        gatherPass = new GatherPass(lensComputeShader);
        gatherPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;

        tracePass = new TracePass(lensComputeShader, rtas, skybox);
        tracePass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques + 1;

        projectorPass = new ProjectorPass();
        projectorPass.renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
    }

    protected override void Dispose(bool disposing)
    {
        if (rtas != null) rtas.Release();
        if (tracePass != null) tracePass.Dispose();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (lensComputeShader == null) return;
        if (HybridLens.ActiveLens == null) return;
        if (!SystemInfo.supportsRayTracing)
        {
            Debug.LogWarning("HybridLensFeature: Hardware Ray Tracing is not suppported!");
            return;
        }

        // TODO // Filter cameras?

        renderer.EnqueuePass(gatherPass);
        renderer.EnqueuePass(tracePass);
        renderer.EnqueuePass(projectorPass);
    }
}
