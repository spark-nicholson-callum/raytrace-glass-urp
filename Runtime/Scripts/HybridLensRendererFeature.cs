using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class HybridLensRendererFeature : ScriptableRendererFeature
{
    public class GatherPass : ScriptableRenderPass
    {
        private static readonly int normalBufferId = Shader.PropertyToID("_LensNormalBuffer");
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
            // Get URP data we (might) need
            var renderingData = frameData.Get<UniversalRenderingData>();
            var cameraData    = frameData.Get<UniversalCameraData>();
            var lightData     = frameData.Get<UniversalLightData>();
            var resourceData  = frameData.Get<UniversalResourceData>();

            // Create the texture based on the camera texture
            var texDesc = cameraData.cameraTargetDescriptor;
            texDesc.colorFormat = RenderTextureFormat.ARGB32;
            texDesc.depthBufferBits = 0;

            var normalBufferHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, texDesc, "_LensNormalBuffer", false);

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
                // TODO // This is just here to stop it being optimized away (for now)
                builder.SetGlobalTextureAfterPass(normalBufferHandle, normalBufferId);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    context.cmd.ClearRenderTarget(false, true, Color.black);
                    context.cmd.DrawRendererList(data.RendererList);
                });
            }
        }
    }

    private GatherPass gatherPass;

    public override void Create()
    {
        gatherPass = new GatherPass();
        gatherPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        /* switch (renderingData.cameraData.cameraType) */
        /* { */
        /*     case CameraType.Game: */
        /*     case CameraType.SceneView: */
        /*         renderer.EnqueuePass(gatherPass); */
        /*         break; */
        /* } */
        renderer.EnqueuePass(gatherPass);
    }
}
