using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public static class Rasterizer
{
    private const int SUBPIXEL_BITS = 4;
    private const int SUBPIXEL_SCALE = 1 << SUBPIXEL_BITS;
    private const int HALF_PIXEL = SUBPIXEL_SCALE / 2;

    private static readonly float4[] clippingResult = new float4[6];

    public static void DrawTriangle(NativeArray<Color32> colorBuffer, NativeArray<float> invDepthBuffer, int bufferWidth, int2 renderSize,
                                    float3 v0, float3 v1, float3 v2, Color32 color)
    {
        int2 v0i = (int2)math.round(v0.xy * SUBPIXEL_SCALE);
        int2 v1i = (int2)math.round(v1.xy * SUBPIXEL_SCALE);
        int2 v2i = (int2)math.round(v2.xy * SUBPIXEL_SCALE);

        int area = EdgeFunction(v0i, v1i, v2i);
        if (area == 0) return;

        if (area < 0)
        {
            (v1i, v2i) = (v2i, v1i);
            (v1, v2) = (v2, v1);
            area = -area;
        }

        int bias0 = IsTopOrLeft(v1i, v2i) ? 0 : -1;
        int bias1 = IsTopOrLeft(v2i, v0i) ? 0 : -1;
        int bias2 = IsTopOrLeft(v0i, v1i) ? 0 : -1;

        int minX = math.max(0, math.min(v0i.x, math.min(v1i.x, v2i.x)) >> SUBPIXEL_BITS);
        int maxX = math.min(renderSize.x - 1, math.max(v0i.x, math.max(v1i.x, v2i.x)) >> SUBPIXEL_BITS);
        int minY = math.max(0, math.min(v0i.y, math.min(v1i.y, v2i.y)) >> SUBPIXEL_BITS);
        int maxY = math.min(renderSize.y - 1, math.max(v0i.y, math.max(v1i.y, v2i.y)) >> SUBPIXEL_BITS);

        float invArea = 1.0f / area;
        for (int y = minY; y <= maxY; ++y)
        {
            for (int x = minX; x <= maxX; ++x)
            {
                int2 p = new int2((x << SUBPIXEL_BITS) + HALF_PIXEL, (y << SUBPIXEL_BITS) + HALF_PIXEL);
                int e0 = EdgeFunction(v1i, v2i, p);
                int e1 = EdgeFunction(v2i, v0i, p);
                int e2 = EdgeFunction(v0i, v1i, p);

                if (e0 + bias0 >= 0 && e1 + bias1 >= 0 && e2 + bias2 >= 0)
                {
                    int index = y * bufferWidth + x;

                    float b0 = e0 * invArea;
                    float b1 = e1 * invArea;
                    float b2 = e2 * invArea;
                    float invW = b0 * v0.z + b1 * v1.z + b2 * v2.z;
                    if (invW > invDepthBuffer[index])
                    {
                        colorBuffer[index] = color;
                        invDepthBuffer[index] = invW;
                    }
                }
            }
        }
    }

    public static float3 ProjectToScreen(float4 clip, int2 renderSize)
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
