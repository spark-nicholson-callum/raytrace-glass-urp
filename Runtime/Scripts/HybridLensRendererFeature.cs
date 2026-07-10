using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace CallumNicholson.RaytraceGlassURP
{
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
        [SerializeField] private ComputeShader fallbackComputeShader;
        [SerializeField] private Cubemap skybox;

        private LensGatherPass gatherPass;
        private ScreenSpaceTracePass tracePass;
        private FallbackTracePass fallbackTracePass;
        private LensProjectorPass projectorPass;

        public override void Create()
        {
            if (lensComputeShader == null) return;
            if (fallbackComputeShader == null) return;
            if (!SystemInfo.supportsRayTracing)
            {
                Debug.LogWarning("HybridLensFeature: Hardware Ray Tracing is not suppported!");
                return;
            }

            // Set up passes
            gatherPass = new LensGatherPass(lensComputeShader);
            gatherPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;

            tracePass = new ScreenSpaceTracePass(lensComputeShader, skybox);
            tracePass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques + 1;

            fallbackTracePass = new FallbackTracePass(fallbackComputeShader);
            fallbackTracePass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques + 2;

            projectorPass = new LensProjectorPass();
            projectorPass.renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
        }

        protected override void Dispose(bool disposing)
        {
            if (tracePass != null) tracePass.Dispose();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (lensComputeShader == null) return;
            if (fallbackComputeShader == null) return;
            if (HybridLens.ActiveLens == null) return;
            if (RayTracingSceneManager.Instance == null) return;
            if (!SystemInfo.supportsRayTracing)
            {
                Debug.LogWarning("HybridLensFeature: Hardware Ray Tracing is not suppported!");
                return;
            }

            // TODO // Filter cameras?

            renderer.EnqueuePass(gatherPass);
            renderer.EnqueuePass(tracePass);
            renderer.EnqueuePass(fallbackTracePass);
            renderer.EnqueuePass(projectorPass);
        }
    }
}
