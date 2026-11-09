using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using static Rasterizer;

[BurstCompile(CompileSynchronously = true)]
public struct RasterJob : IJob
{
    public Framebuffer Framebuffer;
    [ReadOnly] public NativeArray<TriangleSetup> Setups;

    public void Execute()
    {
        for (int i = 0; i < Setups.Length; ++i)
        {
            Rasterizer.DrawTriangle(Framebuffer, Setups[i]);
        }
    }
}
