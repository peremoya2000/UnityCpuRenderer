using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using static Rasterizer;

public class CpuRenderer : MonoBehaviour
{
    public struct MeshInstance
    {
        public MeshEntry Mesh;
        public float4x4 ModelViewProjection;
        public float3 LocalCameraPosition;
        public int FirstClipVertex;
    }

    private const int TILE_SIZE = 32;
    private const int CACHE_LINE_PADDING = 16;

    private static readonly ProfilerMarker clearMarker = new ProfilerMarker("CpuRenderer.Clear");
    private static readonly ProfilerMarker gatherMarker = new ProfilerMarker("CpuRenderer.Gather");
    private static readonly ProfilerMarker worldToClipMarker = new ProfilerMarker("CpuRenderer.WorldToClip");
    private static readonly ProfilerMarker cullClipMarker = new ProfilerMarker("CpuRenderer.CullClip");
    private static readonly ProfilerMarker setupMarker = new ProfilerMarker("CpuRenderer.Setup");
    private static readonly ProfilerMarker rasterMarker = new ProfilerMarker("CpuRenderer.Raster");
    private static readonly ProfilerMarker applyMarker = new ProfilerMarker("CpuRenderer.Apply");

    public Vector2 RenderResolution = new Vector2(640, 360);
    public RawImage RawImage;
    public Camera Camera;

    private Vector2Int paddedResolution;
    private Texture2D colorBuffer;
    private NativeArray<float> depthBuffer;
    private Vector2Int lastScreenSize;
    private FrameTimeStatistics frameTimeStatistics;
    private StageTimings stageTimings;
    private NativeList<MeshInstance> meshInstances;
    private MeshLibrary meshLibrary;
    private NativeList<float4> clipVertices;
    private NativeList<Geometry.ClippedTriangle> clippedTriangles;
    private NativeList<TriangleSetup> triangleSetups;


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
        meshInstances = new NativeList<MeshInstance>(Allocator.Persistent);
        triangleSetups = new NativeList<TriangleSetup>(Allocator.Persistent);
        clippedTriangles = new NativeList<Geometry.ClippedTriangle>(Allocator.Persistent);
        depthBuffer = new NativeArray<float>(paddedResolution.x * paddedResolution.y, Allocator.Persistent);

        Debug.Assert(Camera != null, "Camera is not assigned.", this);
        Camera.enabled = false;

        frameTimeStatistics = new FrameTimeStatistics();
        stageTimings = new StageTimings();

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
            ScreenCapture.CaptureScreenshot($"Screenshot_{Screen.width}x{Screen.height}_{DateTime.Now.ToString("dd_HH-mm-ss")}.png");
        }

        frameTimeStatistics.StartMeasuringFrame();
        NativeArray<Color32> pixels = colorBuffer.GetRawTextureData<Color32>();

        Framebuffer framebuffer = new Framebuffer
        {
            Color = pixels,
            InvDepth = depthBuffer,
            BufferWidth = paddedResolution.x,
            RenderSize = new int2((int)RenderResolution.x, (int)RenderResolution.y),
        };
        using (clearMarker.Auto())
        {
            new ClearJob { Framebuffer = framebuffer, ClearColor = new Color32(32, 32, 32, 255) }.Run();
        }

        //Unity uses forward = Z, but Matrix4x4.Perspective expects forward = -Z, so we need to flip the Z axis in the view matrix.
        Matrix4x4 view = Matrix4x4.Scale(new Vector3(1, 1, -1)) * Matrix4x4.TRS(Camera.transform.position, Camera.transform.rotation, Vector3.one).inverse;
        Matrix4x4 perspective = Matrix4x4.Perspective(Camera.fieldOfView, RenderResolution.x / RenderResolution.y, Camera.nearClipPlane, 1000f);
        Matrix4x4 viewProjection = perspective * view;

        using (gatherMarker.Auto()) { GatherInstances(viewProjection); }

        using (worldToClipMarker.Auto())
        {
            new WorldToClipJob
            {
                Instances = meshInstances.AsArray(),
                VertexBuffer = meshLibrary.VertexBuffer.AsArray(),
                ClipVertices = clipVertices.AsArray(),
            }.Run();
        }

        using (cullClipMarker.Auto())
        {
            clippedTriangles.Clear();
            new CullClipJob
            {
                Instances = meshInstances.AsArray(),
                VertexBuffer = meshLibrary.VertexBuffer.AsArray(),
                IndexBuffer = meshLibrary.IndexBuffer.AsArray(),
                FaceNormals = meshLibrary.FaceNormals.AsArray(),
                ClipVertices = clipVertices.AsArray(),
                Near = Camera.nearClipPlane,
                ClippedTriangles = clippedTriangles,
            }.Run();
        }

        using (setupMarker.Auto())
        {
            triangleSetups.Resize(clippedTriangles.Length, NativeArrayOptions.UninitializedMemory);
            new SetupTrianglesJob
            {
                ClippedTriangles = clippedTriangles.AsArray(),
                Setups = triangleSetups.AsArray(),
                RenderSize = new int2((int)RenderResolution.x, (int)RenderResolution.y),
            }.Run();
        }

        using (rasterMarker.Auto())
        {
            new RasterJob
            {
                Setups = triangleSetups.AsArray(),
                Framebuffer = framebuffer,
            }.Run();
        }

        if (Keyboard.current.ctrlKey.isPressed)
        {
            for (int i = 0; i < pixels.Length; ++i)
            {
                byte grey = (byte)(math.saturate(depthBuffer[i] * 3f) * 255);
                pixels[i] = new Color32(grey, grey, grey, 255);
            }
        }

        using (applyMarker.Auto())
        {
            colorBuffer.Apply(false, false);
        }

        frameTimeStatistics.FinishMeasuringFrame();
        if (Time.realtimeSinceStartup % 1f < Time.deltaTime)
        {
            Debug.Log(frameTimeStatistics.GetDetailedStats());
            Debug.Log(stageTimings.GetStats());
        }
    }

    private void GatherInstances(Matrix4x4 viewProjection)
    {
        meshInstances.Clear();
        int clipVertexCount = 0;

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
            MeshInstance meshInstance = default;
            meshInstance.Mesh = meshLibrary.GetOrAdd(GetMesh(renderer));
            meshInstance.ModelViewProjection = viewProjection * renderer.transform.localToWorldMatrix;
            meshInstance.LocalCameraPosition = math.mul(math.inverse(renderer.transform.localToWorldMatrix), new float4(Camera.transform.position, 1f)).xyz;
            meshInstance.FirstClipVertex = clipVertexCount;
            meshInstances.Add(meshInstance);

            clipVertexCount += meshInstance.Mesh.VertexCount;
        }

        clipVertices.Resize(clipVertexCount, NativeArrayOptions.UninitializedMemory);
    }

    private static Mesh GetMesh(MeshRenderer renderer)
    {
        renderer.TryGetComponent(out MeshFilter filter);
        Debug.Assert(filter != null && filter.sharedMesh != null, $"{renderer.name} has a MeshRenderer but no mesh.", renderer);
        Debug.Assert(filter.sharedMesh.isReadable, $"{filter.sharedMesh.name} needs Read/Write enabled.", renderer);
        Debug.Assert(!renderer.isPartOfStaticBatch, $"{renderer.name} was static-batched. Turn off Static Batching in Player Settings.", renderer);
        return filter.sharedMesh;
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
        if (meshInstances.IsCreated)
        {
            meshInstances.Dispose();
        }
        if (clippedTriangles.IsCreated)
        {
            clippedTriangles.Dispose();
        }
        if (triangleSetups.IsCreated)
        {
            triangleSetups.Dispose();
        }
        meshLibrary?.Dispose();
        stageTimings?.Dispose();
    }

    private void OnValidate()
    {
        RenderResolution.x = Mathf.Round(RenderResolution.x);
        RenderResolution.y = Mathf.Round(RenderResolution.y);
    }
}
