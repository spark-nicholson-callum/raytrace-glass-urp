#ifndef CALLLUM_NICHOLSON_RAYTRACE_GLASS_SHARED_DEFINED__
#define CALLLUM_NICHOLSON_RAYTRACE_GLASS_SHARED_DEFINED__

struct FallbackPayload
{
    uint2 frag;             // 8
    uint instance;          // 4
    uint primitive;         // 4
    float2 barycentrics;    // 8

    float2 padding;         // 8
    // = 32 = 16 * 2 Bytes
};

struct MeshInstanceData
{
    int textureSlice;       // 4
    int indexOffset;        // 4
    int vertexOffset;       // 4

    float padding;          // 4

    float4 uvTransform;     // 16
    float4x4 localToWorld;  // 64
    float4x4 worldToLocal;  // 64
    // = 160 = 16 * 10 Bytes
};

struct MeshVertexData
{
    float3 position;        // 12
    float3 normal;          // 12
    float2 uv;              // 8
    // = 32 = 16 * 2 Bytes
};

#endif//CALLLUM_NICHOLSON_RAYTRACE_GLASS_SHARED_DEFINED__
