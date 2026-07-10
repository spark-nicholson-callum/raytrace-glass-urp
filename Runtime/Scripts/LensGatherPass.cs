
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

class LensGatherPass : ScriptableRenderPass
{
    private ComputeShader lensCompute;
    private int compactionKernel;

    private ShaderTagId shaderTagId = new ShaderTagId("HybridLens/Gather");
    private FilteringSettings filteringSettings;

    public LensGatherPass(ComputeShader shader)
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
        var lensData      = frameData.GetOrCreate<HybridLensRendererFeature.HybridLensData>();
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
