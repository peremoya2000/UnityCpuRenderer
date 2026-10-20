using Unity.Mathematics;

public static class Geometry
{
    public static int ClipTriangle(float4 v0, float4 v1, float4 v2, float near, float4[] output)
    {
        bool3 inside = new(v0.w >= near, v1.w >= near, v2.w >= near);
        int insideCount = (inside.x ? 1 : 0) + (inside.y ? 1 : 0) + (inside.z ? 1 : 0);
        if (insideCount == 0)
        {
            return 0;
        }
        if (insideCount == 3)
        {
            output[0] = v0;
            output[1] = v1;
            output[2] = v2;
            return 1;
        }
        if (insideCount == 1)
        {
            output[0] = v0;
            output[1] = v1;
            output[2] = v2;

            int timesToRotate = GetIndexOfDifferent(inside);
            RotateVertices(output, timesToRotate);

            output[1] = IntersectNear(output[0], output[1], near);
            output[2] = IntersectNear(output[0], output[2], near);
            return 1;
        }
        if (insideCount == 2)
        {
            output[0] = v0;
            output[1] = v1;
            output[2] = v2;

            int timesToRotate = (GetIndexOfDifferent(inside) + 1) % 3;
            RotateVertices(output, timesToRotate);

            float4 ia = IntersectNear(output[0], output[2], near);
            float4 ib = IntersectNear(output[1], output[2], near);
            output[2] = ib;

            output[3] = output[0];
            output[4] = ib;
            output[5] = ia;

            return 2;
        }

        return -1;
    }

    private static float4 IntersectNear(float4 inside, float4 outside, float near)
    {
        float t = (near - inside.w) / (outside.w - inside.w);
        return math.lerp(inside, outside, t);
    }

    private static int GetIndexOfDifferent(bool3 inside)
    {
        bool firstTwoSame = inside.x == inside.y;
        bool lastTwoSame = inside.y == inside.z;

        if (firstTwoSame && lastTwoSame) return -1;
        if (firstTwoSame) return 2;
        if (lastTwoSame) return 0;
        return 1;
    }

    private static void RotateVertices(float4[] vertices, int times)
    {
        for (int i = 0; i < times; ++i)
        {
            float4 temp = vertices[0];
            vertices[0] = vertices[1];
            vertices[1] = vertices[2];
            vertices[2] = temp;
        }
    }
}
