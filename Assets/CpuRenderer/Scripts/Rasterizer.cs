using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public static class Rasterizer
{
    public const float GUARD_BAND_SCALE = 2f;
    private const int SUBPIXEL_BITS = 4;
    private const int SUBPIXEL_SCALE = 1 << SUBPIXEL_BITS;
    private const int HALF_PIXEL = SUBPIXEL_SCALE / 2;

    public struct TriangleSetup
    {
        public int2 V0;
        public int2 V1;
        public int2 V2;
        public float3 InvW;
        public int3 Bias;
        public int4 Bounds;
        public float InvArea;
        public Color32 Color;
    }

    public static void AssertEdgeValuesFitInt32(int2 renderSize)
    {
        long maxXDifference = (long)(GUARD_BAND_SCALE * renderSize.x * SUBPIXEL_SCALE);
        long maxYDifference = (long)(GUARD_BAND_SCALE * renderSize.y * SUBPIXEL_SCALE);
        long maxEdgeValue = 2 * maxXDifference * maxYDifference;
        Debug.Assert(maxEdgeValue <= int.MaxValue, $"Max edge value {maxEdgeValue} overflows int32.");
    }

    public static TriangleSetup SetupTriangle(float4 clip0, float4 clip1, float4 clip2, Color32 color, int2 renderSize)
    {
        float3 screen0 = ProjectToScreen(clip0, renderSize);
        float3 screen1 = ProjectToScreen(clip1, renderSize);
        float3 screen2 = ProjectToScreen(clip2, renderSize);

        int2 v0i = (int2)math.round(screen0.xy * SUBPIXEL_SCALE);
        int2 v1i = (int2)math.round(screen1.xy * SUBPIXEL_SCALE);
        int2 v2i = (int2)math.round(screen2.xy * SUBPIXEL_SCALE);
        int area = EdgeFunction(v0i, v1i, v2i);

        //if no area make max lower than min to ensure no pixels are drawn
        if (area == 0) return new TriangleSetup { Bounds = new int4(0, 0, -1, -1) };

        if (area < 0)
        {
            (v1i, v2i) = (v2i, v1i);
            (screen1, screen2) = (screen2, screen1);
            area = -area;
        }

        int bias0 = IsTopOrLeft(v1i, v2i) ? 0 : -1;
        int bias1 = IsTopOrLeft(v2i, v0i) ? 0 : -1;
        int bias2 = IsTopOrLeft(v0i, v1i) ? 0 : -1;

        int minX = math.max(0, math.min(v0i.x, math.min(v1i.x, v2i.x)) >> SUBPIXEL_BITS);
        int maxX = math.min(renderSize.x - 1, math.max(v0i.x, math.max(v1i.x, v2i.x)) >> SUBPIXEL_BITS);
        int minY = math.max(0, math.min(v0i.y, math.min(v1i.y, v2i.y)) >> SUBPIXEL_BITS);
        int maxY = math.min(renderSize.y - 1, math.max(v0i.y, math.max(v1i.y, v2i.y)) >> SUBPIXEL_BITS);

        TriangleSetup triangleSetup = new TriangleSetup
        {
            V0 = v0i,
            V1 = v1i,
            V2 = v2i,
            InvW = new float3(screen0.z, screen1.z, screen2.z),
            Bias = new int3(bias0, bias1, bias2),
            Bounds = new int4(minX, minY, maxX, maxY),
            InvArea = 1.0f / area,
            Color = color
        };

        return triangleSetup;
    }

    public static void DrawTriangle(Framebuffer framebuffer, TriangleSetup triangleSetup)
    {
        for (int y = triangleSetup.Bounds.y; y <= triangleSetup.Bounds.w; ++y)
        {
            for (int x = triangleSetup.Bounds.x; x <= triangleSetup.Bounds.z; ++x)
            {
                int2 p = new int2((x << SUBPIXEL_BITS) + HALF_PIXEL, (y << SUBPIXEL_BITS) + HALF_PIXEL);
                int e0 = EdgeFunction(triangleSetup.V1, triangleSetup.V2, p);
                int e1 = EdgeFunction(triangleSetup.V2, triangleSetup.V0, p);
                int e2 = EdgeFunction(triangleSetup.V0, triangleSetup.V1, p);

                if (e0 + triangleSetup.Bias.x >= 0 && e1 + triangleSetup.Bias.y >= 0 && e2 + triangleSetup.Bias.z >= 0)
                {
                    int index = y * framebuffer.BufferWidth + x;

                    float b0 = e0 * triangleSetup.InvArea;
                    float b1 = e1 * triangleSetup.InvArea;
                    float b2 = e2 * triangleSetup.InvArea;
                    float invW = b0 * triangleSetup.InvW.x + b1 * triangleSetup.InvW.y + b2 * triangleSetup.InvW.z;
                    if (invW > framebuffer.InvDepth[index])
                    {
                        framebuffer.Color[index] = triangleSetup.Color;
                        framebuffer.InvDepth[index] = invW;
                    }
                }
            }
        }
    }

    private static float3 ProjectToScreen(float4 clip, int2 renderSize)
    {
        float invW = 1f / clip.w;
        float3 ndc = clip.xyz * invW;
        float2 screen = (ndc.xy * 0.5f + 0.5f) * renderSize;
        return new float3(screen, invW);
    }

    private static int EdgeFunction(int2 a, int2 b, int2 p)
    {
        return (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
    }

    private static bool IsTopOrLeft(int2 a, int2 b)
    {
        int2 d = b - a;
        return d.y < 0 || (d.y == 0 && d.x < 0);
    }
}
