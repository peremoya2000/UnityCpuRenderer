using Unity.Mathematics;
using UnityEngine;

public static class GeometryTests
{
    private const float NEAR = 0.5f;
    private const float TOLERANCE = 1e-5f;

    public static void Run()
    {
        float4 a = Vertex(1f, 2f), b = Vertex(4f, -1f), c = Vertex(-2f, 0.2f);
        AssertClipAllRotations(a, b, c, a, Vertex(2.5f, 0.5f), Vertex(-1.5f, 0.5f));

        a = Vertex(0f, 2f); b = Vertex(2f, 3f); c = Vertex(4f, -1f);
        float4 ib = Vertex(3.25f, 0.5f), ia = Vertex(2f, 0.5f);
        AssertClipAllRotations(a, b, c, a, b, ib, a, ib, ia);

        a = Vertex(0f, 1f); b = Vertex(1f, 2f); c = Vertex(2f, NEAR);
        AssertClip(a, b, c, a, b, c);

        AssertClipAllRotations(Vertex(0f, 0.4f), Vertex(1f, -1f), Vertex(2f, 0.1f));

        Debug.Log("Geometry tests finished.");
    }

    private static void AssertClipAllRotations(float4 a, float4 b, float4 c, params float4[] expected)
    {
        AssertClip(a, b, c, expected);
        AssertClip(b, c, a, expected);
        AssertClip(c, a, b, expected);
    }

    private static void AssertClip(float4 v0, float4 v1, float4 v2, params float4[] expected)
    {
        float4[] output = new float4[6];
        int triangleCount = Geometry.ClipTriangle(v0, v1, v2, NEAR, output);
        string call = $"ClipTriangle({v0}, {v1}, {v2})";
        Debug.Assert(triangleCount * 3 == expected.Length, $"{call} returned {triangleCount} triangles, expected {expected.Length / 3}.");
        for (int i = 0; i < expected.Length; ++i)
        {
            Debug.Assert(math.all(math.abs(output[i] - expected[i]) < TOLERANCE), $"{call}: output[{i}] is {output[i]}, expected {expected[i]}.");
        }
    }

    private static float4 Vertex(float x, float w) => new float4(x, 0f, 0f, w);
}
