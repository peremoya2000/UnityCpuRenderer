using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public static class Rasterizer
{
    private const int SUBPIXEL_BITS = 4;
    private const int SUBPIXEL_SCALE = 1 << SUBPIXEL_BITS;
    private const int HALF_PIXEL = SUBPIXEL_SCALE / 2;

    public static void DrawTriangle(NativeArray<Color32> colorBuffer, int bufferWidth, int2 renderSize,
                                    float2 v0, float2 v1, float2 v2, Color32 color)
    {
        int2 v0i = (int2)math.round(v0 * SUBPIXEL_SCALE);
        int2 v1i = (int2)math.round(v1 * SUBPIXEL_SCALE);
        int2 v2i = (int2)math.round(v2 * SUBPIXEL_SCALE);

        int area = EdgeFunction(v0i, v1i, v2i);
        if (area == 0) return;

        if (area < 0)
        {
            (v1i, v2i) = (v2i, v1i);
            area = -area;
        }

        int bias0 = IsTopOrLeft(v1i, v2i) ? 0 : -1;
        int bias1 = IsTopOrLeft(v2i, v0i) ? 0 : -1;
        int bias2 = IsTopOrLeft(v0i, v1i) ? 0 : -1;

        int minX = math.max(0, math.min(v0i.x, math.min(v1i.x, v2i.x)) >> SUBPIXEL_BITS);
        int maxX = math.min(renderSize.x - 1, math.max(v0i.x, math.max(v1i.x, v2i.x)) >> SUBPIXEL_BITS);
        int minY = math.max(0, math.min(v0i.y, math.min(v1i.y, v2i.y)) >> SUBPIXEL_BITS);
        int maxY = math.min(renderSize.y - 1, math.max(v0i.y, math.max(v1i.y, v2i.y)) >> SUBPIXEL_BITS);

        for (int y = minY; y <= maxY; ++y)
        {
            for (int x = minX; x <= maxX; ++x)
            {
                int2 p = new int2((x << SUBPIXEL_BITS) + HALF_PIXEL, (y << SUBPIXEL_BITS) + HALF_PIXEL);
                int w0 = EdgeFunction(v1i, v2i, p);
                int w1 = EdgeFunction(v2i, v0i, p);
                int w2 = EdgeFunction(v0i, v1i, p);
                if (w0 + bias0 >= 0 && w1 + bias1 >= 0 && w2 + bias2 >= 0)
                {
                    int index = y * bufferWidth + x;
                    colorBuffer[index] = color;
                }
            }
        }
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
