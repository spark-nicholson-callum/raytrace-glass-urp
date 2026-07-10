using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace CallumNicholson.RaytraceGlassURP
{
    public class RayTracingSceneManager : MonoBehaviour
    {
        public static RayTracingSceneManager Instance;

        public RayTracingAccelerationStructure Rtas { get; private set; }

        public void Awake()
        {
            Instance = this;

            var settings = new RayTracingAccelerationStructure.Settings();
            settings.layerMask = ~LayerMask.GetMask("UI");
            settings.managementMode = RayTracingAccelerationStructure.ManagementMode.Manual;
            settings.rayTracingModeMask = RayTracingAccelerationStructure.RayTracingModeMask.DynamicTransform
                                        | RayTracingAccelerationStructure.RayTracingModeMask.Static;
            Rtas = new RayTracingAccelerationStructure(settings);
        }

        public void RebuildSceneData()
        {
            Rtas.ClearInstances();

            var allRenderers = GameObject.FindObjectsByType<Renderer>()
                .Where(x => x.gameObject.activeInHierarchy)
                .ToArray();

            foreach (var renderer in allRenderers)
            {
                int subMeshCount = renderer.sharedMaterials.Length;
                var subMeshFlags = Enumerable.Range(0, subMeshCount)
                    .Select(x => RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly)
                    .ToArray();

                Rtas.AddInstance(renderer, subMeshFlags);
            }
        }

        public void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (Rtas != null) Rtas.Release();
        }
    }
}
