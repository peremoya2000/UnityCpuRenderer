using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using static Geometry;
using static Rasterizer;

[BurstCompile(CompileSynchronously = true)]
public struct SetupTrianglesJob : IJob
{
    [ReadOnly] public NativeArray<ClippedTriangle> ClippedTriangles;
    [WriteOnly] public NativeArray<TriangleSetup> Setups;
    public int2 RenderSize;

    public void Execute()
    {
        for (int i = 0; i < ClippedTriangles.Length; ++i)
        {
            ClippedTriangle triangle = ClippedTriangles[i];
            Setups[i] = Rasterizer.SetupTriangle(triangle.V0, triangle.V1, triangle.V2, triangle.Color, RenderSize);
        }
    }
}
