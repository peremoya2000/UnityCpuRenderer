using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using static CpuRenderer;
using static Geometry;

[BurstCompile(CompileSynchronously = true)]
public struct CullClipJob : IJob
{
    [ReadOnly] public NativeArray<MeshInstance> Instances;
    [ReadOnly] public NativeArray<float3> VertexBuffer;
    [ReadOnly] public NativeArray<int> IndexBuffer;
    [ReadOnly] public NativeArray<float3> FaceNormals;
    [ReadOnly] public NativeArray<float4> ClipVertices;
    public float Near;
    public NativeList<ClippedTriangle> ClippedTriangles;

    public void Execute()
    {
        for (int i = 0; i < Instances.Length; ++i)
        {
            MeshInstance instance = Instances[i];
            for (int t = instance.Mesh.FirstTriangle; t < instance.Mesh.FirstTriangle + instance.Mesh.TriangleCount; ++t)
            {
                int i0 = IndexBuffer[t * 3 + 0];
                int i1 = IndexBuffer[t * 3 + 1];
                int i2 = IndexBuffer[t * 3 + 2];

                if (math.dot(FaceNormals[t], VertexBuffer[instance.Mesh.FirstVertex + i0] - instance.LocalCameraPosition) > 0f) continue;

                FixedList512Bytes<float4> polygon = Geometry.ClipTriangle(ClipVertices[instance.FirstClipVertex + i0], ClipVertices[instance.FirstClipVertex + i1], ClipVertices[instance.FirstClipVertex + i2], Near, Rasterizer.GUARD_BAND_SCALE);
                int triangleCount = polygon.Length - 2;
                for (int j = 0; j < triangleCount; ++j)
                {
                    ClippedTriangles.Add(new ClippedTriangle
                    {
                        V0 = polygon[0],
                        V1 = polygon[j + 1],
                        V2 = polygon[j + 2],
                        Color = HashedColor(1 + t)
                    });
                }
            }
        }
    }
}
