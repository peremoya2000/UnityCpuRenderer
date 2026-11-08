using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class CpuRenderer : MonoBehaviour
{
    private const int TILE_SIZE = 32;
    private const int CACHE_LINE_PADDING = 16;

    public Vector2 RenderResolution = new Vector2(640, 360);
    public RawImage RawImage;
    public Camera Camera;

    private Vector2Int paddedResolution;
    private Texture2D colorBuffer;
    private NativeArray<float> depthBuffer;
    private Vector2Int lastScreenSize;
    private FrameTimeStatistics frameTimeStatistics;

    private MeshLibrary meshLibrary;
    private NativeList<float4> clipVertices;


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
        Rasterizer.AssertEdgeValuesFitInt32(new int2((int)RenderResolution.x, (int)RenderResolution.y));

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

        meshLibrary = new MeshLibrary();
        clipVertices = new NativeList<float4>(Allocator.Persistent);
        depthBuffer = new NativeArray<float>(paddedResolution.x * paddedResolution.y, Allocator.Persistent);

        Debug.Assert(Camera != null, "Camera is not assigned.", this);
        Camera.enabled = false;

        frameTimeStatistics = new FrameTimeStatistics();

#if UNITY_EDITOR
        GeometryTests.Run();
#endif
    }

    void LateUpdate()
    {
        if (lastScreenSize.x != Screen.width || lastScreenSize.y != Screen.height)
        {
            UpdateRenderRectScale();
        }

        if (Keyboard.current.f12Key.wasPressedThisFrame)
        {
            ScreenCapture.CaptureScreenshot($"Screenshot_{Screen.width}x{Screen.height}.png");
        }

        frameTimeStatistics.StartMeasuringFrame();
        NativeArray<Color32> pixels = colorBuffer.GetRawTextureData<Color32>();
        for (int i = 0; i < pixels.Length; ++i)
        {
            pixels[i] = new Color32(32, 32, 32, 255);
            depthBuffer[i] = 0f;
        }

        Framebuffer framebuffer = new Framebuffer
        {
            Color = pixels,
            InvDepth = depthBuffer,
            BufferWidth = paddedResolution.x,
            RenderSize = new int2((int)RenderResolution.x, (int)RenderResolution.y),
        };

        //Unity uses forward = Z, but Matrix4x4.Perspective expects forward = -Z, so we need to flip the Z axis in the view matrix.
        Matrix4x4 view = Matrix4x4.Scale(new Vector3(1, 1, -1)) * Matrix4x4.TRS(Camera.transform.position, Camera.transform.rotation, Vector3.one).inverse;
        Matrix4x4 perspective = Matrix4x4.Perspective(Camera.fieldOfView, RenderResolution.x / RenderResolution.y, Camera.nearClipPlane, 1000f);
        Matrix4x4 viewProjection = perspective * view;

        MeshRenderer[] renderers = FindObjectsByType<MeshRenderer>(FindObjectsSortMode.InstanceID);
        for (int i = 0; i < renderers.Length; ++i)
        {
            MeshRenderer renderer = renderers[i];
            if (!renderer.enabled) continue;
            Bounds bounds = renderer.bounds;
            if (Geometry.IsBoxOffScreen(viewProjection, bounds.center, bounds.extents, Camera.nearClipPlane))
            {
                continue;
            }
            MeshEntry range = meshLibrary.GetOrAdd(GetMesh(renderer));
            RenderMesh(range, renderer.transform.localToWorldMatrix, viewProjection, framebuffer);
        }

        if (Keyboard.current.ctrlKey.isPressed)
        {
            for (int i = 0; i < pixels.Length; ++i)
            {
                byte grey = (byte)(math.saturate(depthBuffer[i] * 3f) * 255);
                pixels[i] = new Color32(grey, grey, grey, 255);
            }
        }

        colorBuffer.Apply(false, false);

        frameTimeStatistics.FinishMeasuringFrame();
        if (Time.realtimeSinceStartup % 1f < Time.deltaTime)
        {
            Debug.Log(frameTimeStatistics.GetDetailedStats());
        }
    }

    private void RenderMesh(MeshEntry meshEntry, Matrix4x4 modelMatrix, Matrix4x4 viewProjection, Framebuffer framebuffer)
    {
        float4x4 mvp = viewProjection * modelMatrix;

        float3 localCameraPos = math.mul(math.inverse(modelMatrix), new float4(Camera.transform.position, 1f)).xyz;

        clipVertices.Resize(meshEntry.VertexCount, NativeArrayOptions.UninitializedMemory);
        for (int v = 0; v < meshEntry.VertexCount; ++v)
        {
            clipVertices[v] = math.mul(mvp, new float4(meshLibrary.VertexBuffer[meshEntry.FirstVertex + v], 1f));
        }

        float near = Camera.nearClipPlane;
        for (int t = meshEntry.FirstTriangle; t < meshEntry.FirstTriangle + meshEntry.TriangleCount; ++t)
        {
            int i0 = meshLibrary.IndexBuffer[t * 3 + 0];
            int i1 = meshLibrary.IndexBuffer[t * 3 + 1];
            int i2 = meshLibrary.IndexBuffer[t * 3 + 2];
            if (math.dot(meshLibrary.FaceNormals[t], meshLibrary.VertexBuffer[meshEntry.FirstVertex + i0] - localCameraPos) > 0f) continue;

            FixedList512Bytes<float4> polygon = Geometry.ClipTriangle(clipVertices[i0], clipVertices[i1], clipVertices[i2], near, Rasterizer.GUARD_BAND_SCALE);
            Rasterizer.DrawPolygon(framebuffer, polygon, StripeColor(1 + t));
        }
    }

    private static Mesh GetMesh(MeshRenderer renderer)
    {
        renderer.TryGetComponent(out MeshFilter filter);
        Debug.Assert(filter != null && filter.sharedMesh != null, $"{renderer.name} has a MeshRenderer but no mesh.", renderer);
        Debug.Assert(filter.sharedMesh.isReadable, $"{filter.sharedMesh.name} needs Read/Write enabled.", renderer);
        Debug.Assert(!renderer.isPartOfStaticBatch, $"{renderer.name} was static-batched. Turn off Static Batching in Player Settings.", renderer);
        return filter.sharedMesh;
    }


    private static Color32 StripeColor(int i)
    {
        uint hash = (uint)i * 2654435761u;
        return new Color32((byte)(hash >> 24), (byte)(hash >> 16), (byte)(hash >> 8), 255);
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
        if (clipVertices.IsCreated)
        {
            clipVertices.Dispose();
        }
        meshLibrary?.Dispose();
    }

    private void OnValidate()
    {
        RenderResolution.x = Mathf.Round(RenderResolution.x);
        RenderResolution.y = Mathf.Round(RenderResolution.y);
    }
}
