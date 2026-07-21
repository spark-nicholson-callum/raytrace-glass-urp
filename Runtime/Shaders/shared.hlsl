#ifndef CALLLUM_NICHOLSON_RAYTRACE_GLASS_SHARED_DEFINED__
#define CALLLUM_NICHOLSON_RAYTRACE_GLASS_SHARED_DEFINED__

struct FallbackPayload
{
    uint2 frag;             // 8
    uint instance;          // 4
    uint submesh;           // 4
    uint primitive;         // 4
    float2 barycentrics;    // 8
    float3 colorFilter;     // 12
    // 0 = refraction
    // 1 = reflection
    uint type;              // 4

    float padding;          // 4

    // = 48 = 16 * 3 Bytes
};

struct MeshInstanceData
{
    int submeshOffset;      // 4

    float3 padding;         // 12

    float4x4 localToWorld;  // 64
    float4x4 worldToLocal;  // 64
    // = 144 = 16 * 9 Bytes
};

struct SubmeshData
{
    int textureSlice;       // 4
    int indexOffset;        // 4
    int vertexOffset;       // 4
    float baseIor;          // 4
    float dispersion;       // 4

    float3 padding;         // 12

    float4 baseColor;       // 16
    float4 uvTransform;     // 16
    // = 64 = 16 * 4 Bytes
};

struct MeshVertexData
{
    float3 position;        // 12
    float3 normal;          // 12
    float2 uv;              // 8
    // = 32 = 16 * 2 Bytes
};

#endif//CALLLUM_NICHOLSON_RAYTRACE_GLASS_SHARED_DEFINED__
