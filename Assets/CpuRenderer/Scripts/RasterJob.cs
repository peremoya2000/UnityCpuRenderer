using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static CpuRenderer;
using static Rasterizer;

[BurstCompile(CompileSynchronously = true)]
public struct RasterJob : IJob
{
    public Framebuffer Framebuffer;
    public Color32 ClearColor;
    [ReadOnly] public NativeArray<TriangleSetup> Setups;

    public void Execute()
    {
        int2 tileCount = (Framebuffer.RenderSize + TILE_SIZE - 1) / TILE_SIZE;
        int totalTiles = tileCount.x * tileCount.y;

        for (int tileIndex = 0; tileIndex < totalTiles; ++tileIndex)
        {
            int4 tileRect;
            tileRect.x = (tileIndex % tileCount.x) * TILE_SIZE;
            tileRect.y = (tileIndex / tileCount.x) * TILE_SIZE;
            tileRect.z = tileRect.x + TILE_SIZE - 1;
            tileRect.w = tileRect.y + TILE_SIZE - 1;

            ClearRect(tileRect);

            for (int i = 0; i < Setups.Length; ++i)
            {
                TriangleSetup triSetup = Setups[i];
                int4 intersectedRect = GetIntersectingRect(triSetup.Bounds, tileRect);
                if (math.all(intersectedRect.xy <= intersectedRect.zw))
                {
                    Rasterizer.DrawTriangle(Framebuffer, triSetup, intersectedRect);
                }
            }
        }
    }

    private void ClearRect(int4 rect)
    {
        for (int y = rect.y; y <= rect.w; ++y)
        {
            for (int x = rect.x; x <= rect.z; ++x)
            {
                int index = y * Framebuffer.BufferWidth + x;
                Framebuffer.Color[index] = ClearColor;
                Framebuffer.InvDepth[index] = 0f;
            }
        }
    }

    private static int4 GetIntersectingRect(int4 rect0, int4 rect1)
    {
        int4 intersection = new int4(math.max(rect0.xy, rect1.xy), math.min(rect0.zw, rect1.zw));
        return intersection;
    }
}
