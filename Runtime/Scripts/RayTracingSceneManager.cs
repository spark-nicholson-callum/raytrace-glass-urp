using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace CallumNicholson.RaytraceGlassURP
{
    public class RayTracingSceneManager : MonoBehaviour
    {
        public static RayTracingSceneManager Instance;

        public RayTracingAccelerationStructure Rtas { get; private set; }
        public RenderTexture GlobalTextureArray { get; private set; }
        public ComputeBuffer GlobalTextureIndexBuffer { get; private set; }

        [SerializeField] private int MaxTextures = 64;
        [SerializeField] private int TextureResolution = 512;

        private Texture2D fallbackTexture;

        private Dictionary<Texture, int> textureSliceMap = new();
        private int currentSlice;

        public void Awake()
        {
            Instance = this;

            var settings = new RayTracingAccelerationStructure.Settings();
            settings.layerMask = ~LayerMask.GetMask("UI");
            settings.managementMode = RayTracingAccelerationStructure.ManagementMode.Manual;
            settings.rayTracingModeMask = RayTracingAccelerationStructure.RayTracingModeMask.DynamicTransform
                                        | RayTracingAccelerationStructure.RayTracingModeMask.Static;
            Rtas = new RayTracingAccelerationStructure(settings);

            GlobalTextureArray = new RenderTexture(TextureResolution, TextureResolution, 0, RenderTextureFormat.ARGB32)
            {
                dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray,
                volumeDepth = MaxTextures,
                useMipMap = true,
                autoGenerateMips = false,
                enableRandomWrite = true
            };
            GlobalTextureArray.Create();

            fallbackTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            fallbackTexture.SetPixel(0, 0, Color.white);
            fallbackTexture.Apply();

            GetTextureSlice(fallbackTexture);
        }

        public void RebuildSceneData()
        {
            Rtas.ClearInstances();

            var allRenderers = GameObject.FindObjectsByType<Renderer>()
                .Where(x => x.gameObject.activeInHierarchy)
                .ToArray();

            var sliceData = new int[allRenderers.Length];
            int currentIndex = 0;

            foreach (var renderer in allRenderers)
            {
                int subMeshCount = renderer.sharedMaterials.Length;
                var subMeshFlags = Enumerable.Range(0, subMeshCount)
                    .Select(x => RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly)
                    .ToArray();

                Rtas.AddInstance(renderer, subMeshFlags);

                Texture mainTexture = null;
                if (renderer.sharedMaterial != null && renderer.sharedMaterial.HasProperty("_MainTex"))
                    mainTexture = renderer.sharedMaterial.mainTexture;
                sliceData[currentIndex] = GetTextureSlice(mainTexture);

                ++currentIndex;
            }

            if (GlobalTextureIndexBuffer != null) GlobalTextureIndexBuffer.Release();
            GlobalTextureIndexBuffer = new ComputeBuffer(Mathf.Max(1, allRenderers.Length), 4);
            GlobalTextureIndexBuffer.SetData(sliceData);
        }

        public int GetTextureSlice(Texture source)
        {
            if (source == null) return 0;

            if (textureSliceMap.TryGetValue(source, out int existingSlice)) return existingSlice;
            if (currentSlice >= MaxTextures)
            {
                Debug.LogWarning("Ray tracing texture atlas is full!");
                return 0;
            }

            Graphics.Blit(source, GlobalTextureArray, 0, currentSlice);
            GlobalTextureArray.GenerateMips();
            textureSliceMap[source] = currentSlice;

            return currentSlice++;
        }

        public void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (Rtas != null) Rtas.Release();
            if (GlobalTextureArray != null) GlobalTextureArray.Release();
            if (GlobalTextureIndexBuffer != null) GlobalTextureIndexBuffer.Release();
            if (fallbackTexture != null) Destroy(fallbackTexture);
        }
    }
}
