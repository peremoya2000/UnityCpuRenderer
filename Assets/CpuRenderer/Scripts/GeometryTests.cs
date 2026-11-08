using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public static class GeometryTests
{
    private const float NEAR = 0.5f;
    private const float GUARD_BAND_SCALE = 2f;
    private const float NO_GUARD_BAND = 1000f;
    private const float TOLERANCE = 1e-5f;

    public static void Run()
    {
        TestNearClip();
        TestOffScreenReject();
        TestGuardBandClip();
        TestBoxCulling();

        Debug.Log("Geometry tests finished.");
    }

    private static void TestNearClip()
    {
        float4 a = Vertex(1f, 2f), b = Vertex(4f, -1f), c = Vertex(-2f, 0.2f);
        AssertClip(NO_GUARD_BAND, a, b, c, a, Vertex(2.5f, 0.5f), Vertex(-1.5f, 0.5f));

        a = Vertex(0f, 2f); b = Vertex(2f, 3f); c = Vertex(4f, -1f);
        AssertClip(NO_GUARD_BAND, a, b, c, a, b, Vertex(3.25f, 0.5f), Vertex(2f, 0.5f));

        a = Vertex(0f, 1f); b = Vertex(1f, 2f); c = Vertex(2f, NEAR);
        AssertClip(NO_GUARD_BAND, a, b, c, a, b, c);

        AssertClip(NO_GUARD_BAND, Vertex(0f, 0.4f), Vertex(1f, -1f), Vertex(2f, 0.1f));
    }

    private static void TestOffScreenReject()
    {
        AssertClip(NO_GUARD_BAND, Vertex(1.5f, 0f, 1f), Vertex(1.8f, 0.5f, 1f), Vertex(3f, 0f, 2f));
        AssertClip(NO_GUARD_BAND, Vertex(-1.5f, 0f, 1f), Vertex(-1.8f, 0.5f, 1f), Vertex(-3f, 0f, 2f));
        AssertClip(NO_GUARD_BAND, Vertex(0f, 1.5f, 1f), Vertex(0.5f, 1.8f, 1f), Vertex(0f, 3f, 2f));
        AssertClip(NO_GUARD_BAND, Vertex(0f, -1.5f, 1f), Vertex(0.5f, -1.8f, 1f), Vertex(0f, -3f, 2f));

        float4 a = Vertex(1.2f, 0.9f, 1f), b = Vertex(0.9f, 1.2f, 1f), c = Vertex(1.5f, 1.5f, 1f);
        AssertClip(NO_GUARD_BAND, a, b, c, a, b, c);
    }

    private static void TestGuardBandClip()
    {
        float4 a = Vertex(0.5f, 0f, 1f), b = Vertex(1.5f, 0f, 1f), c = Vertex(0.5f, 1.5f, 1f);
        AssertClip(GUARD_BAND_SCALE, a, b, c, a, b, c);

        a = Vertex(1f, 1f); b = Vertex(5f, 1f); c = Vertex(1f, 3f);
        AssertClip(GUARD_BAND_SCALE, a, b, c, a, Vertex(2f, 1f), Vertex(3.5f, 1.75f), c);

        a = Vertex(-10f, -10f, 1f); b = Vertex(10f, -10f, 1f); c = Vertex(0f, 10f, 1f);
        AssertClip(GUARD_BAND_SCALE, a, b, c, Vertex(-2f, -2f, 1f), Vertex(2f, -2f, 1f), Vertex(2f, 2f, 1f), Vertex(-2f, 2f, 1f));

        a = Vertex(0f, 2f); b = Vertex(3f, 2f); c = Vertex(30f, -1f);
        AssertClip(GUARD_BAND_SCALE, a, b, c, a, b, Vertex(42f / 11f, 21f / 11f), Vertex(10f / 3f, 5f / 3f));
    }

    private static void TestBoxCulling()
    {
        float4x4 viewProjection = Matrix4x4.Perspective(60f, 16f / 9f, NEAR, 1000f) * Matrix4x4.Scale(new Vector3(1f, 1f, -1f));

        AssertBoxOffScreen("box right of the view", viewProjection, new float3(11f, 0f, 6f), new float3(1f, 1f, 1f), true);
        AssertBoxOffScreen("box straight ahead", viewProjection, new float3(0f, 0f, 6f), new float3(1f, 1f, 1f), false);
        AssertBoxOffScreen("big box behind the camera", viewProjection, new float3(0f, 0f, -14f), new float3(8f, 8f, 8f), true);
        AssertBoxOffScreen("wall straddling the view", viewProjection, new float3(0f, 0f, 10f), new float3(20f, 1f, 0.1f), false);
    }

    private static void AssertBoxOffScreen(string description, float4x4 viewProjection, float3 center, float3 extents, bool expected)
    {
        bool actual = Geometry.IsBoxOffScreen(viewProjection, center, extents, NEAR);
        Debug.Assert(actual == expected, $"IsBoxOffScreen, {description}: returned {actual}, expected {expected}.");
    }

    private static void AssertClip(float guardBandScale, float4 a, float4 b, float4 c, params float4[] expected)
    {
        AssertClipOnce(guardBandScale, a, b, c, expected);
        AssertClipOnce(guardBandScale, b, c, a, expected);
        AssertClipOnce(guardBandScale, c, a, b, expected);
    }

    private static void AssertClipOnce(float guardBandScale, float4 v0, float4 v1, float4 v2, float4[] expected)
    {
        float4[] actual = Geometry.ClipTriangle(v0, v1, v2, NEAR, guardBandScale).ToArray();
        Debug.Assert(IsSamePolygon(actual, expected), $"ClipTriangle({v0}, {v1}, {v2}, guard band {guardBandScale}) returned [{string.Join(", ", actual)}], expected [{string.Join(", ", expected)}].");
    }

    private static bool IsSamePolygon(float4[] actual, float4[] expected)
    {
        if (actual.Length != expected.Length) return false;
        if (actual.Length == 0) return true;

        for (int startCorner = 0; startCorner < actual.Length; ++startCorner)
        {
            if (MatchesFromCorner(actual, expected, startCorner)) return true;
        }
        return false;
    }

    private static bool MatchesFromCorner(float4[] actual, float4[] expected, int startCorner)
    {
        for (int i = 0; i < expected.Length; ++i)
        {
            float4 corner = actual[(startCorner + i) % actual.Length];
            if (math.any(math.abs(corner - expected[i]) >= TOLERANCE)) return false;
        }
        return true;
    }

    private static float4 Vertex(float x, float w) => new float4(x, 0f, 0f, w);

    private static float4 Vertex(float x, float y, float w) => new float4(x, y, 0f, w);
}
