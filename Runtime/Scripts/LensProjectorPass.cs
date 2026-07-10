using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

class LensProjectorPass : ScriptableRenderPass
{
    private static readonly int rayTraceOutputId = Shader.PropertyToID("_RayTraceOutput");
    private ShaderTagId shaderTagId = new ShaderTagId("HybridLens/Project");
    private FilteringSettings filteringSettings;

    public LensProjectorPass()
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
        var lensData      = frameData.Get<HybridLensRendererFeature.HybridLensData>();
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
