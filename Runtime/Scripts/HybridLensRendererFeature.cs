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

    [SerializeField] private ComputeShader lensComputeShader;
    [SerializeField] private Cubemap skybox;

    private LensGatherPass gatherPass;
    private ScreenSpaceTracePass tracePass;
    private LensProjectorPass projectorPass;

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
        gatherPass = new LensGatherPass(lensComputeShader);
        gatherPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;

        tracePass = new ScreenSpaceTracePass(lensComputeShader, rtas, skybox);
        tracePass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques + 1;

        projectorPass = new LensProjectorPass();
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
