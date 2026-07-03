using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class OldHybridLensRendererFeature : ScriptableRendererFeature
{
    [SerializeField] private ComputeShader computeShader;
    private ComputePass computePass;

    public class ComputePass : ScriptableRenderPass
    {
        private RayTracingAccelerationStructure rtas;
        private ComputeShader lensCompute;
        private int kernel;

        public ComputePass(ComputeShader compute)
        {
            lensCompute = compute;
            kernel = compute.FindKernel("DiffractGlass");

            var settings = new RayTracingAccelerationStructure.Settings();
            rtas = new(settings);
        }

        class PassData
        {
            public ComputeShader cs;
            public RayTracingAccelerationStructure rtas;

            public int kernel;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            rtas.ClearInstances();

            // Rebuild RTAS
            rtas.ClearInstances();

            // Just grab every Instance for now
            Renderer[] allRenderers = GameObject.FindObjectsByType<Renderer>();
            foreach (var renderer in allRenderers)
            {
                var subMeshFlags = Enumerable.Range(0, renderer.sharedMaterials.Length)
                    .Select(x => RayTracingSubMeshFlags.Enabled)
                    .ToArray();

                rtas.AddInstance(renderer, subMeshFlags, false);
            }

            using (var builder = renderGraph.AddComputePass("ComputePass", out PassData passData))
            {
                passData.cs     = lensCompute;
                passData.rtas   = rtas;
                passData.kernel = kernel;

                builder.SetRenderFunc(static (PassData data, ComputeGraphContext cgContext) => ExecutePass(data, cgContext));
            }
        }

        static void ExecutePass(PassData data, ComputeGraphContext cgContext)
        {
            cgContext.cmd.BuildRayTracingAccelerationStructure(data.rtas);
            cgContext.cmd.SetRayTracingAccelerationStructure(data.cs, data.kernel, "SceneRtas", data.rtas);

            // Set Other stuff here

            cgContext.cmd.DispatchCompute(data.cs, data.kernel, 1, 1, 1);
        }

        public void Dispose()
        {
            if (rtas != null) rtas.Release();
        }
    }

    public override void Create()
    {
        computePass = new ComputePass(computeShader);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (!SystemInfo.supportsComputeShaders)
        {
            Debug.LogWarning("Device does not support compute shaders. The pass will be skipped");
            return;
        }

        if (computeShader == null)
        {
            Debug.LogWarning("The compute shader is null. The pass will be skipped");
            return;
        }

        renderer.EnqueuePass(computePass);
    }

    protected override void Dispose(bool disposing)
    {
        if (computePass != null) computePass.Dispose();
    }
}
