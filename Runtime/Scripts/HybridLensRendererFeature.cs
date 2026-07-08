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
        public TextureHandle OutputTextureHandle;
        public BufferHandle ActivePixelsBufferHandle;

        public override void Reset()
        {
            NormalBufferHandle = TextureHandle.nullHandle;
            ActivePixelsBufferHandle = BufferHandle.nullHandle;
        }
    }

    public class GatherPass : ScriptableRenderPass
    {
        private ShaderTagId shaderTagId = new ShaderTagId("HybridLens/Gather");
        private FilteringSettings filteringSettings;

        public GatherPass()
        {
            filteringSettings = new FilteringSettings(RenderQueueRange.opaque);
        }

        private class PassData
        {
            // TODO // Normal buffer doesn't have to be in pass data
            public TextureHandle NormalBuffer;
            public RendererListHandle RendererList;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var lensData      = frameData.GetOrCreate<HybridLensData>();
            var renderingData = frameData.Get<UniversalRenderingData>();
            var cameraData    = frameData.Get<UniversalCameraData>();
            var lightData     = frameData.Get<UniversalLightData>();
            var resourceData  = frameData.Get<UniversalResourceData>();

            // Create the texture based on the camera texture
            var texDesc = cameraData.cameraTargetDescriptor;
            texDesc.colorFormat = RenderTextureFormat.ARGB32;
            texDesc.depthBufferBits = 0;

            var normalBufferHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, texDesc, "_LensNormalBuffer", false);

            lensData.NormalBufferHandle = normalBufferHandle;

            // Build the renderer list
            var sortingCriteria = cameraData.defaultOpaqueSortFlags;
            var drawingSettings = CreateDrawingSettings(shaderTagId, renderingData, cameraData, lightData, sortingCriteria);

            var listParams = new RendererListParams(renderingData.cullResults, drawingSettings, filteringSettings);
            var rendererListHandle = renderGraph.CreateRendererList(listParams);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Lens Gatherer Pass", out var passData))
            {
                passData.NormalBuffer = normalBufferHandle;
                passData.RendererList = rendererListHandle;

                builder.UseRendererList(rendererListHandle);
                builder.SetRenderAttachment(normalBufferHandle, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    context.cmd.ClearRenderTarget(false, true, Color.clear);
                    context.cmd.DrawRendererList(data.RendererList);
                });
            }
        }
    }

    public class CompactionPass : ScriptableRenderPass
    {
        private ComputeShader lensCompute;
        private int compactionKernel;

        public CompactionPass(ComputeShader shader)
        {
            lensCompute = shader;
            compactionKernel = lensCompute.FindKernel("NormalCompaction");
        }

        private class PassData
        {
            public ComputeShader Compute;
            public int Kernel;

            public int ThreadGroupsX;
            public int ThreadGroupsY;

            public TextureHandle NormalBuffer;
            public BufferHandle ActivePixelsBuffer;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var lensData   = frameData.Get<HybridLensData>();
            var cameraData = frameData.Get<UniversalCameraData>();

            int width = cameraData.cameraTargetDescriptor.width;
            int height = cameraData.cameraTargetDescriptor.height;

            var normalBufferHandle = lensData.NormalBufferHandle;

            var bufferDesc = new BufferDesc(width * height, sizeof(uint) * 2)
            {
                name = "Active Lens Pixels Buffer",
                target = GraphicsBuffer.Target.Append
            };
            var activePixelsHandle = renderGraph.CreateBuffer(bufferDesc);

            lensData.ActivePixelsBufferHandle = activePixelsHandle;

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
        private ComputeShader lensCompute;
        private int clearKernel;
        private int setupKernel;
        private int traceKernel;

        private RayTracingAccelerationStructure rtas;

        public TracePass(ComputeShader shader, RayTracingAccelerationStructure rtas)
        {
            lensCompute = shader;
            clearKernel = lensCompute.FindKernel("ClearOutput");
            setupKernel = lensCompute.FindKernel("ArgsSetup");
            traceKernel = lensCompute.FindKernel("TraceLens");

            this.rtas = rtas;
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
            public TextureHandle OpaqueTexture;
            public TextureHandle OutputTexture;
            public BufferHandle ActivePixelsBuffer;
            public BufferHandle ArgsBuffer;

            public Matrix4x4 ViewProj;
            public Matrix4x4 InverseViewProj;
            public RayTracingAccelerationStructure Rtas;
            public Vector3 CameraPos;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
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

            using (var builder = renderGraph.AddComputePass<PassData>("Lens Trace Pass", out var passData))
            {
                // TODO // Temporarily disable culling for debugging
                builder.AllowPassCulling(false);

                passData.Compute = lensCompute;
                passData.ClearKernel = clearKernel;
                passData.SetupKernel = setupKernel;
                passData.TraceKernel = traceKernel;

                // TODO // Fetch the group size!
                passData.ClearGroupsX = (width  + 8 - 1) / 8;
                passData.ClearGroupsY = (height + 8 - 1) / 8;

                builder.UseTexture(lensData.NormalBufferHandle, AccessFlags.Read);
                passData.NormalTexture = lensData.NormalBufferHandle;

                builder.UseTexture(resourceData.activeDepthTexture, AccessFlags.Read);
                passData.DepthTexture = resourceData.activeDepthTexture;

                builder.UseTexture(resourceData.activeColorTexture, AccessFlags.Read);
                passData.OpaqueTexture = resourceData.activeColorTexture;

                builder.UseTexture(outputTextureHandle, AccessFlags.Write);
                passData.OutputTexture = outputTextureHandle;

                passData.ActivePixelsBuffer = builder.UseBuffer(lensData.ActivePixelsBufferHandle, AccessFlags.Read);
                passData.ArgsBuffer = builder.UseBuffer(argsHandle, AccessFlags.Write);

                Matrix4x4 viewMatrix = cameraData.GetViewMatrix();
                Matrix4x4 projMatrix = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true);
                passData.ViewProj = projMatrix * viewMatrix;
                passData.InverseViewProj = passData.ViewProj.inverse;

                passData.Rtas = rtas;
                passData.CameraPos = cameraData.worldSpaceCameraPos;

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
                    context.cmd.SetComputeTextureParam(data.Compute, data.TraceKernel, "_CameraDepthTexture", data.DepthTexture);
                    context.cmd.SetComputeTextureParam(data.Compute, data.TraceKernel, "_CameraOpaqueTexture", data.OpaqueTexture);
                    context.cmd.SetComputeTextureParam(data.Compute, data.TraceKernel, "_RayTraceOutput", data.OutputTexture);
                    context.cmd.SetComputeBufferParam(data.Compute, data.TraceKernel, "_ActivePixelsBufferRead", data.ActivePixelsBuffer);
                    context.cmd.SetComputeBufferParam(data.Compute, data.TraceKernel, "_IndirectArgsBuffer", data.ArgsBuffer);

                    context.cmd.SetComputeMatrixParam(data.Compute, "_ViewProjMatrix", data.ViewProj);
                    context.cmd.SetComputeMatrixParam(data.Compute, "_InverseViewProjMatrix", data.InverseViewProj);
                    context.cmd.SetRayTracingAccelerationStructure(data.Compute, data.TraceKernel, "_SceneRtas", data.Rtas);
                    context.cmd.SetComputeVectorParam(data.Compute, "_CameraPos", data.CameraPos);

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
    private GatherPass gatherPass;
    private CompactionPass compactionPass;
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
        gatherPass = new GatherPass();
        gatherPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;

        compactionPass = new CompactionPass(lensComputeShader);
        compactionPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques + 1;

        tracePass = new TracePass(lensComputeShader, rtas);
        tracePass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques + 2;

        projectorPass = new ProjectorPass();
        projectorPass.renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
    }

    protected override void Dispose(bool disposing)
    {
        if (rtas != null) rtas.Release();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (lensComputeShader == null) return;
        if (!SystemInfo.supportsRayTracing)
        {
            Debug.LogWarning("HybridLensFeature: Hardware Ray Tracing is not suppported!");
            return;
        }

        /* switch (renderingData.cameraData.cameraType) */
        /* { */
        /*     case CameraType.Game: */
        /*     case CameraType.SceneView: */
        /*         break; */
        /*     default: */
        /*         return; */
        /* } */

        renderer.EnqueuePass(gatherPass);
        renderer.EnqueuePass(compactionPass);
        renderer.EnqueuePass(tracePass);
        renderer.EnqueuePass(projectorPass);
    }
}
