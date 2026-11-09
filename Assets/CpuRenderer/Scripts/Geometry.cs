using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public static class Geometry
{
    public struct ClippedTriangle
    {
        public float4 V0;
        public float4 V1;
        public float4 V2;
        public Color32 Color;
    }

    public static FixedList512Bytes<float4> ClipTriangle(float4 v0, float4 v1, float4 v2, float near, float guardBandScale)
    {
        FixedList512Bytes<float4> polygon = default;
        if (IsOffScreen(v0, v1, v2)) return polygon;

        polygon.Add(v0);
        polygon.Add(v1);
        polygon.Add(v2);

        polygon = ClipAgainstPlane(polygon, new float4(0f, 0f, 0f, 1f), near);
        if (!HasVertexOutsideGuardBand(polygon, guardBandScale)) return polygon;

        polygon = ClipAgainstPlane(polygon, new float4(-1f, 0f, 0f, guardBandScale));
        polygon = ClipAgainstPlane(polygon, new float4(1f, 0f, 0f, guardBandScale));
        polygon = ClipAgainstPlane(polygon, new float4(0f, -1f, 0f, guardBandScale));
        polygon = ClipAgainstPlane(polygon, new float4(0f, 1f, 0f, guardBandScale));
        return polygon;
    }

    public static bool IsBoxOffScreen(float4x4 viewProjection, float3 center, float3 extents, float near)
    {
        float4x4 rows = math.transpose(viewProjection);
        FixedList128Bytes<float4> planes = default;
        planes.Add(rows.c3 + rows.c0);//left
        planes.Add(rows.c3 - rows.c0);//right
        planes.Add(rows.c3 + rows.c1);//bottom
        planes.Add(rows.c3 - rows.c1);//top
        planes.Add(rows.c3 - new float4(0f, 0f, 0f, near));//near

        for (int i = 0; i < planes.Length; ++i)
        {
            if (IsBoxOutsidePlane(planes[i], center, extents))
            {
                return true;
            }
        }

        return false;
    }

    public static Color32 HashedColor(int i)
    {
        uint hash = (uint)i * 2654435761u;
        return new Color32((byte)(hash >> 24), (byte)(hash >> 16), (byte)(hash >> 8), 255);
    }

    private static bool IsBoxOutsidePlane(float4 plane, float3 center, float3 extents)
    {
        float distance = math.dot(plane.xyz, center) + plane.w;
        float radius = math.dot(math.abs(plane.xyz), extents);
        return distance < -radius;
    }

    private static bool IsOffScreen(float4 v0, float4 v1, float4 v2)
    {
        float3 x = new(v0.x, v1.x, v2.x);
        float3 y = new(v0.y, v1.y, v2.y);
        float3 w = new(v0.w, v1.w, v2.w);
        return math.all(x > w) || math.all(x < -w) || math.all(y > w) || math.all(y < -w);
    }

    private static bool HasVertexOutsideGuardBand(FixedList512Bytes<float4> polygon, float guardBandScale)
    {
        for (int i = 0; i < polygon.Length; ++i)
        {
            float4 v = polygon[i];
            if (math.any(math.abs(v.xy) > guardBandScale * v.w))
            {
                return true;
            }
        }

        return false;
    }

    private static FixedList512Bytes<float4> ClipAgainstPlane(FixedList512Bytes<float4> input, float4 planeNormal, float planeDistance = 0f)
    {
        FixedList512Bytes<float4> output = default;
        for (int i = 0; i < input.Length; ++i)
        {
            float4 current = input[i];
            float4 next = input[(i + 1) % input.Length];
            float currentD = math.dot(planeNormal, current) - planeDistance;
            float nextD = math.dot(planeNormal, next) - planeDistance;
            bool currentInside = currentD >= 0f;
            bool nextInside = nextD >= 0f;

            //We keep 2 types of verts: the ones that are inside
            if (currentInside)
            {
                output.Add(current);
            }
            //and we also keep the intersection point if the plane happens to be between the current and next vert
            if (currentInside != nextInside)
            {
                if (currentInside)
                {
                    //inside to outside edge
                    output.Add(Intersect(current, next, currentD, nextD));
                }
                else
                {
                    //outside to inside edge
                    output.Add(Intersect(next, current, nextD, currentD));
                }
            }
        }
        return output;
    }

    private static float4 Intersect(float4 inside, float4 outside, float insideD, float outsideD)
    {
        return math.lerp(inside, outside, insideD / (insideD - outsideD));
    }
}