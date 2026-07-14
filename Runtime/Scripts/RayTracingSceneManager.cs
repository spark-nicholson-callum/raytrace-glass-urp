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
        public ComputeBuffer GlobalInstanceDataBuffer { get; private set; }
        public ComputeBuffer GlobalSubmeshDataBuffer { get; private set; }
        public ComputeBuffer GlobalIndexBuffer { get; private set; }
        public ComputeBuffer GlobalVertexBuffer { get; private set; }

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

            List<MeshInstanceData> instanceData = new();
            List<SubmeshData>      submeshData  = new();
            List<int>              indexData    = new();
            List<MeshVertexData>   vertexData   = new();

            // There is a delicate balance here with the index of the data.
            // We *require* the instance data array and the RTAS be in the same order
            foreach (var renderer in allRenderers)
            {
                MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
                if (meshFilter == null || meshFilter.sharedMesh == null) continue;
                var mesh = meshFilter.sharedMesh;

                int subMeshCount = renderer.sharedMaterials.Length;
                var subMeshFlags = Enumerable.Range(0, subMeshCount)
                    .Select(x => RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly)
                    .ToArray();

                Rtas.AddInstance(renderer, subMeshFlags, id: (uint)instanceData.Count);

                instanceData.Add(new MeshInstanceData
                {
                    submeshOffset = submeshData.Count,
                    localToWorld = renderer.transform.localToWorldMatrix,
                    worldToLocal = renderer.transform.worldToLocalMatrix,
                });

                int submeshIndex = 0;
                foreach (var material in renderer.sharedMaterials)
                {
                    Texture texture = null;
                    if (material.HasProperty("_MainTex")) texture = material.mainTexture;

                    Vector4 uvTransform = new Vector4(1, 1, 0, 0);
                    if      (material.HasProperty("_BaseMap_ST")) uvTransform = material.GetVector("_BaseMap_ST");
                    else if (material.HasProperty("_MainTex_ST")) uvTransform = material.GetVector("_MainTex_ST");

                    Color baseColor = Color.white;
                    if      (material.HasProperty("_BaseColor")) baseColor = material.GetColor("_BaseColor");
                    else if (material.HasProperty("_Color"))     baseColor = material.GetColor("_Color");

                    submeshData.Add(new SubmeshData
                    {
                        textureSlice = GetTextureSlice(texture),
                        indexOffset = indexData.Count,
                        vertexOffset = vertexData.Count,
                        padding = 0f,
                        baseColor = baseColor.linear,
                        uvTransform = uvTransform,
                    });

                    indexData.AddRange(mesh.GetIndices(submeshIndex));

                    ++submeshIndex;
                }

                vertexData.AddRange(mesh.vertices
                    .Zip(mesh.normals, (vert, norm) => new {vert, norm})
                    .Zip(mesh.uv, (data, uv) => new MeshVertexData
                    {
                        position = data.vert,
                        normal = data.norm,
                        uv = uv,
                    })
                );
            }

            if (GlobalInstanceDataBuffer != null) GlobalInstanceDataBuffer.Release();
            GlobalInstanceDataBuffer = new ComputeBuffer(Mathf.Max(1, instanceData.Count), MeshInstanceData.Size);
            GlobalInstanceDataBuffer.SetData(instanceData);

            if (GlobalSubmeshDataBuffer != null) GlobalSubmeshDataBuffer.Release();
            GlobalSubmeshDataBuffer = new ComputeBuffer(Mathf.Max(1, submeshData.Count), SubmeshData.Size);
            GlobalSubmeshDataBuffer.SetData(submeshData);

            if (GlobalIndexBuffer != null) GlobalIndexBuffer.Release();
            GlobalIndexBuffer = new ComputeBuffer(Mathf.Max(1, indexData.Count), sizeof(uint));
            GlobalIndexBuffer.SetData(indexData);

            if (GlobalVertexBuffer != null) GlobalVertexBuffer.Release();
            GlobalVertexBuffer = new ComputeBuffer(Mathf.Max(1, vertexData.Count), MeshVertexData.Size);
            GlobalVertexBuffer.SetData(vertexData);
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
            if (GlobalInstanceDataBuffer != null) GlobalInstanceDataBuffer.Release();
            if (GlobalSubmeshDataBuffer != null) GlobalSubmeshDataBuffer.Release();
            if (GlobalIndexBuffer != null) GlobalIndexBuffer.Release();
            if (GlobalVertexBuffer != null) GlobalVertexBuffer.Release();
            if (fallbackTexture != null) Destroy(fallbackTexture);
        }
    }
}
