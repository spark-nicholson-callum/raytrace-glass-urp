using UnityEngine;

namespace CallumNicholson.RaytraceGlassURP
{
    public struct MeshInstanceData
    {
        public const int Size = 144;
        public int textureSlice;
        public int indexOffset;
        public int vertexOffset;

        public float padding;

        public Matrix4x4 localToWorld;
        public Matrix4x4 worldToLocal;
    }

    public struct MeshVertexData
    {
        public const int Size = 32;
        public Vector3 position;
        public Vector3 normal;
        public Vector2 uv;
    }
}
