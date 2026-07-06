using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class HybridLensRendererFeature : ScriptableRendererFeature
{
    public class HybridLensData : ContextItem
    {
        public TextureHandle NormalBufferHandle;
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

        public TracePass(ComputeShader shader)
        {
            lensCompute = shader;
            clearKernel = lensCompute.FindKernel("ClearOutput");
            setupKernel = lensCompute.FindKernel("ArgsSetup");
            traceKernel = lensCompute.FindKernel("TraceLens");
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
            public TextureHandle OutputTexture;
            public BufferHandle ActivePixelsBuffer;
            public BufferHandle ArgsBuffer;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var lensData   = frameData.Get<HybridLensData>();
            var cameraData = frameData.Get<UniversalCameraData>();

            int width = cameraData.cameraTargetDescriptor.width;
            int height = cameraData.cameraTargetDescriptor.height;

            var texDesc = cameraData.cameraTargetDescriptor;
            texDesc.enableRandomWrite = true;
            texDesc.colorFormat = RenderTextureFormat.ARGB32;
            texDesc.depthBufferBits = 0;

            TextureHandle outputTextureHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, texDesc, "_RayTraceOutput", false);

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

                builder.UseTexture(outputTextureHandle, AccessFlags.Write);
                passData.OutputTexture = outputTextureHandle;

                passData.ActivePixelsBuffer = builder.UseBuffer(lensData.ActivePixelsBufferHandle, AccessFlags.Read);
                passData.ArgsBuffer = builder.UseBuffer(argsHandle, AccessFlags.Write);

                builder.SetRenderFunc(static (PassData data, ComputeGraphContext context) =>
                {
                    // Clear the output texture
                    context.cmd.SetComputeTextureParam(data.Compute, data.ClearKernel, "_RayTraceOutput", data.OutputTexture);
                    context.cmd.DispatchCompute(data.Compute, data.ClearKernel, data.ClearGroupsX, data.ClearGroupsY, 1);

                    // Generate the thread group sizes
                    context.cmd.CopyCounterValue(data.ActivePixelsBuffer, data.ArgsBuffer, 3 * sizeof(uint));

                    context.cmd.SetComputeBufferParam(data.Compute, data.SetupKernel, "_IndirectArgsBuffer", data.ArgsBuffer);
                    context.cmd.DispatchCompute(data.Compute, data.SetupKernel, 1, 1, 1);

                    // Run the lens tracing kernel
                    context.cmd.SetComputeTextureParam(data.Compute, data.TraceKernel, "_LensNormalBuffer", data.NormalTexture);
                    context.cmd.SetComputeTextureParam(data.Compute, data.TraceKernel, "_RayTraceOutput", data.OutputTexture);
                    context.cmd.SetComputeBufferParam(data.Compute, data.TraceKernel, "_ActivePixelsBufferRead", data.ActivePixelsBuffer);
                    context.cmd.SetComputeBufferParam(data.Compute, data.TraceKernel, "_IndirectArgsBuffer", data.ArgsBuffer);

                    context.cmd.DispatchCompute(data.Compute, data.TraceKernel, data.ArgsBuffer, 0);
                });
            }
        }
    }

    [SerializeField] private ComputeShader lensComputeShader;
    private GatherPass gatherPass;
    private CompactionPass compactionPass;
    private TracePass tracePass;

    public override void Create()
    {
        if (lensComputeShader == null) return;

        gatherPass = new GatherPass();
        gatherPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;

        compactionPass = new CompactionPass(lensComputeShader);
        compactionPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques + 1;

        tracePass = new TracePass(lensComputeShader);
        tracePass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques + 2;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (lensComputeShader == null) return;

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
    }
}
