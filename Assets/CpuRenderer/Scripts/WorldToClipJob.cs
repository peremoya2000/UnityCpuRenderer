using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using static CpuRenderer;

[BurstCompile(CompileSynchronously = true)]
public struct WorldToClipJob : IJob
{
    [ReadOnly] public NativeArray<MeshInstance> Instances;
    [ReadOnly] public NativeArray<float3> VertexBuffer;
    [WriteOnly] public NativeArray<float4> ClipVertices;

    public void Execute()
    {
        for (int i = 0; i < Instances.Length; ++i)
        {
            MeshInstance meshInstance = Instances[i];
            float4x4 mvp = meshInstance.ModelViewProjection;
            for (int v = 0; v < meshInstance.Mesh.VertexCount; ++v)
            {
                ClipVertices[meshInstance.FirstClipVertex + v] = math.mul(mvp, new float4(VertexBuffer[meshInstance.Mesh.FirstVertex + v], 1f));
            }
        }
    }
}
