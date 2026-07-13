using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace CallumNicholson.RaytraceGlassURP
{
    class FallbackTracePass : ScriptableRenderPass
    {
        private ComputeShader fallbackCompute;
        private int setupKernel;
        private int traceKernel;

        public FallbackTracePass(ComputeShader shader)
        {
            fallbackCompute = shader;
            setupKernel = fallbackCompute.FindKernel("ArgsSetup");
            traceKernel = fallbackCompute.FindKernel("FallbackTrace");
        }

        private class PassData
        {
            public ComputeShader Compute;
            public int SetupKernel;
            public int TraceKernel;

            public TextureHandle OutputTexture;
            public BufferHandle ArgsBuffer;
            public BufferHandle OccludedRayBuffer;

            public TextureHandle GlobalTextureArray;
            public ComputeBuffer GlobalInstanceDataBuffer;
            public ComputeBuffer GlobalIndexBuffer;
            public ComputeBuffer GlobalVertexBuffer;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var lensData = frameData.Get<HybridLensRendererFeature.HybridLensData>();

            BufferDesc argsDesc = new BufferDesc(4, sizeof(uint))
            {
                name = "Indirect Args Buffer",
                target = GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured
            };
            BufferHandle argsHandle = renderGraph.CreateBuffer(argsDesc);

            using (var builder = renderGraph.AddComputePass<PassData>("Trace Lookup Pass", out var passData))
            {
                passData.Compute = fallbackCompute;
                passData.SetupKernel = setupKernel;
                passData.TraceKernel = traceKernel;

                builder.UseTexture(lensData.OutputTextureHandle, AccessFlags.Write);
                passData.OutputTexture = lensData.OutputTextureHandle;

                passData.ArgsBuffer = builder.UseBuffer(argsHandle, AccessFlags.Write);
                passData.OccludedRayBuffer = builder.UseBuffer(lensData.OccludedRayBufferHandle, AccessFlags.Read);

                passData.GlobalTextureArray = renderGraph.ImportTexture(
                    RTHandles.Alloc(RayTracingSceneManager.Instance.GlobalTextureArray)
                );
                builder.UseTexture(passData.GlobalTextureArray);

                passData.GlobalInstanceDataBuffer = RayTracingSceneManager.Instance.GlobalInstanceDataBuffer;
                passData.GlobalIndexBuffer = RayTracingSceneManager.Instance.GlobalIndexBuffer;
                passData.GlobalVertexBuffer = RayTracingSceneManager.Instance.GlobalVertexBuffer;

                builder.SetRenderFunc(static (PassData data, ComputeGraphContext context) =>
                {
                    // ScreenSpaceTracePass already built the RTAS

                    // Setup indirect argument buffer
                    context.cmd.CopyCounterValue(data.OccludedRayBuffer, data.ArgsBuffer, 3 * sizeof(uint));
                    context.cmd.SetComputeBufferParam(data.Compute, data.SetupKernel, "_IndirectArgsBuffer", data.ArgsBuffer);
                    context.cmd.DispatchCompute(data.Compute, data.SetupKernel, 1, 1, 1);

                    // Call the fallback trace
                    context.cmd.SetComputeTextureParam(data.Compute, data.TraceKernel, "_RayTraceOutput", data.OutputTexture);
                    context.cmd.SetComputeBufferParam(data.Compute, data.TraceKernel, "_IndirectArgsBuffer", data.ArgsBuffer);
                    context.cmd.SetComputeBufferParam(data.Compute, data.TraceKernel, "_OccludedRayBuffer", data.OccludedRayBuffer);

                    context.cmd.SetComputeTextureParam(data.Compute, data.TraceKernel, "_GlobalTextures", data.GlobalTextureArray);
                    context.cmd.SetComputeBufferParam(data.Compute, data.TraceKernel, "_GlobalInstanceData", data.GlobalInstanceDataBuffer);
                    context.cmd.SetComputeBufferParam(data.Compute, data.TraceKernel, "_GlobalIndices", data.GlobalIndexBuffer);
                    context.cmd.SetComputeBufferParam(data.Compute, data.TraceKernel, "_GlobalVertices", data.GlobalVertexBuffer);

                    context.cmd.DispatchCompute(data.Compute, data.TraceKernel, data.ArgsBuffer, 0);
                });
            }
        }
    }
}
