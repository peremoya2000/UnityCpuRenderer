using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class RawImageRenderTest : MonoBehaviour
{
    private const int TILE_SIZE = 32;
    private const int CACHE_LINE_PADDING = 16;

    private static readonly float3[] CubeVertices =
    {
        new(-1f, -1f, -1f), // 0
        new( 1f, -1f, -1f), // 1
        new(-1f,  1f, -1f), // 2
        new( 1f,  1f, -1f), // 3
        new(-1f, -1f,  1f), // 4
        new( 1f, -1f,  1f), // 5
        new(-1f,  1f,  1f), // 6
        new( 1f,  1f,  1f), // 7
    };

    private static readonly int[] CubeIndices =
    {
        // -Z face
        0, 2, 3,  0, 3, 1,
        // +Z face
        4, 5, 7,  4, 7, 6,
        // -X face
        0, 4, 6,  0, 6, 2,
        // +X face
        1, 3, 7,  1, 7, 5,
        // -Y face
        0, 1, 5,  0, 5, 4,
        // +Y face
        2, 6, 7,  2, 7, 3,
    };


    public Vector2 RenderResolution = new Vector2(640, 360);
    public RawImage RawImage;
    public Camera Camera;

    private Vector2Int paddedResolution;
    private Texture2D colorBuffer;
    private NativeArray<float> depthBuffer;
    private Vector2Int lastScreenSize;
    private float4[] clipSpaceVerts;
    private float4[] clippedTrianglesResult;
    private float3[] cubeNormals;

    public int GetIntegerScaling()
    {
        int xRenderScale = Screen.width / (int)RenderResolution.x;
        int yRenderScale = Screen.height / (int)RenderResolution.y;
        return Mathf.Max(1, Mathf.Min(xRenderScale, yRenderScale));
    }

    public void UpdateRenderRectScale()
    {
        int scale = GetIntegerScaling();

        RawImage.rectTransform.sizeDelta = new Vector2(RenderResolution.x * scale, RenderResolution.y * scale);
        RawImage.rectTransform.anchoredPosition = new Vector2((Screen.width - (int)RenderResolution.x * scale) / 2, (Screen.height - (int)RenderResolution.y * scale) / 2);
        lastScreenSize = new Vector2Int(Screen.width, Screen.height);
    }

    void Start()
    {
        paddedResolution.x = (int)(Mathf.Ceil(RenderResolution.x / TILE_SIZE) * TILE_SIZE + CACHE_LINE_PADDING);
        paddedResolution.y = (int)(Mathf.Ceil(RenderResolution.y / TILE_SIZE) * TILE_SIZE);

        colorBuffer = new Texture2D(paddedResolution.x, paddedResolution.y, TextureFormat.RGBA32, false, false);
        colorBuffer.filterMode = FilterMode.Point;
        colorBuffer.wrapMode = TextureWrapMode.Clamp;
        Debug.Assert(RawImage != null, "RawImage is not assigned.", this);
        RawImage.uvRect = new Rect(0, 0, RenderResolution.x / paddedResolution.x, RenderResolution.y / paddedResolution.y);
        RawImage.texture = colorBuffer;
        RawImage.rectTransform.anchorMin = Vector2.zero;
        RawImage.rectTransform.anchorMax = Vector2.zero;
        RawImage.rectTransform.pivot = Vector2.zero;

        UpdateRenderRectScale();

        depthBuffer = new NativeArray<float>(paddedResolution.x * paddedResolution.y, Allocator.Persistent);

        Debug.Assert(Camera != null, "Camera is not assigned.", this);
        Camera.enabled = false;

        clipSpaceVerts = new float4[CubeVertices.Length];
        clippedTrianglesResult = new float4[6];
        CalculateCubeNormals();
#if UNITY_EDITOR
        GeometryTests.Run();
#endif
    }

    void Update()
    {
        if (lastScreenSize.x != Screen.width || lastScreenSize.y != Screen.height)
        {
            UpdateRenderRectScale();
        }

        if (Keyboard.current.f12Key.wasPressedThisFrame)
        {
            ScreenCapture.CaptureScreenshot($"Screenshot_{Screen.width}x{Screen.height}.png");
        }

        NativeArray<Color32> pixels = colorBuffer.GetRawTextureData<Color32>();
        for (int i = 0; i < pixels.Length; ++i)
        {
            pixels[i] = new Color32(32, 32, 32, 255);
            depthBuffer[i] = 0f;
        }

        int2 renderSize = new int2((int)RenderResolution.x, (int)RenderResolution.y);

        Matrix4x4 model0 = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(Time.time * 20f, Time.time * 30f, 0f), Vector3.one);
        Matrix4x4 model1 = Matrix4x4.TRS(new Vector3(2, 0, 2), Quaternion.Euler(Time.time * 2f, Time.time * 3f, 0f), new Vector3(1, 2, 1));

        RenderCube(model0, pixels, renderSize);
        RenderCube(model1, pixels, renderSize);

        if (Keyboard.current.dKey.isPressed)
        {
            for (int i = 0; i < pixels.Length; ++i)
            {
                byte grey = (byte)(math.saturate(depthBuffer[i] * 3f) * 255);
                pixels[i] = new Color32(grey, grey, grey, 255);
            }
        }

        colorBuffer.Apply(false, false);
    }

    private void RenderCube(Matrix4x4 modelMatrix, NativeArray<Color32> pixels, int2 renderSize)
    {
        //Unity uses forward = Z, but Matrix4x4.Perspective expects forward = -Z, so we need to flip the Z axis in the view matrix.
        Matrix4x4 view = Matrix4x4.Scale(new Vector3(1, 1, -1)) * Matrix4x4.TRS(Camera.transform.position, Camera.transform.rotation, Vector3.one).inverse;
        Matrix4x4 perspective = Matrix4x4.Perspective(Camera.fieldOfView, RenderResolution.x / RenderResolution.y, Camera.nearClipPlane, 1000f);
        float4x4 mvp = perspective * view * modelMatrix;

        float3 localCameraPos = math.mul(math.inverse(modelMatrix), new float4(Camera.transform.position, 1f)).xyz;

        for (int i = 0; i < CubeVertices.Length; ++i)
        {
            clipSpaceVerts[i] = math.mul(mvp, new float4(CubeVertices[i], 1f));
        }

        float near = Camera.nearClipPlane;
        for (int i = 0; i < CubeIndices.Length; i += 3)
        {
            int i0 = CubeIndices[i + 0];
            int i1 = CubeIndices[i + 1];
            int i2 = CubeIndices[i + 2];
            if (math.dot(cubeNormals[i / 3], CubeVertices[i0] - localCameraPos) > 0f)
            {
                continue;
            }
            
            int triangleCount = Geometry.ClipTriangle(clipSpaceVerts[i0], clipSpaceVerts[i1], clipSpaceVerts[i2], near, clippedTrianglesResult);
            Color32 color = StripeColor(1 + i / 3);

            for (int j = 0; j < triangleCount; ++j)
            {
                float3 screen0 = Rasterizer.ProjectToScreen(clippedTrianglesResult[j * 3 + 0], renderSize);
                float3 screen1 = Rasterizer.ProjectToScreen(clippedTrianglesResult[j * 3 + 1], renderSize);
                float3 screen2 = Rasterizer.ProjectToScreen(clippedTrianglesResult[j * 3 + 2], renderSize);
                Rasterizer.DrawTriangle(pixels, depthBuffer, paddedResolution.x, renderSize, screen0, screen1, screen2, color);
            }
        }
    }

    private static Color32 StripeColor(int i)
    {
        uint hash = (uint)i * 2654435761u;
        return new Color32((byte)(hash >> 24), (byte)(hash >> 16), (byte)(hash >> 8), 255);
    }

    private void CalculateCubeNormals()
    {
        cubeNormals = new float3[CubeIndices.Length / 3];
        for (int i = 0; i < cubeNormals.Length; ++i)
        {
            float3 v0 = CubeVertices[CubeIndices[i * 3 + 0]];
            float3 v1 = CubeVertices[CubeIndices[i * 3 + 1]];
            float3 v2 = CubeVertices[CubeIndices[i * 3 + 2]];
            cubeNormals[i] = math.normalize(math.cross(v1 - v0, v2 - v0));
        }
    }

    private void OnDestroy()
    {
        if (colorBuffer != null)
        {
            Destroy(colorBuffer);
            colorBuffer = null;
        }
        if (depthBuffer.IsCreated)
        {
            depthBuffer.Dispose();
        }
    }

    private void OnValidate()
    {
        RenderResolution.x = Mathf.Round(RenderResolution.x);
        RenderResolution.y = Mathf.Round(RenderResolution.y);
    }
}
