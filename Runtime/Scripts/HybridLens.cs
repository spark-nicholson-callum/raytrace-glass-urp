using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(Renderer))]
public class HybridLens : MonoBehaviour
{
    // For now assume only one lens
    public static HybridLens ActiveLens { get; private set; }

    public Mesh LensMesh { get; private set; }
    public Transform LensTransform => transform;

    private void OnEnable()
    {
        ActiveLens = this;
        LensMesh = GetComponent<MeshFilter>().sharedMesh;
    }

    private void OnDisable()
    {
        if (ActiveLens == this) ActiveLens = null;
    }
}
