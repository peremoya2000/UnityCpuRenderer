using Unity.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class RawImageRenderTest : MonoBehaviour
{
    private const int TILE_SIZE = 32;
    private const int CACHE_LINE_PADDING = 16;

    public Vector2 RenderResolution = new Vector2(640, 360);
    public RawImage RawImage;

    private Vector2Int paddedResolution;
    private Texture2D colorBuffer;
    private Vector2Int lastScreenSize;

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
    }

    void Update()
    {
        if(lastScreenSize.x != Screen.width || lastScreenSize.y != Screen.height)
        {
            UpdateRenderRectScale();
        }

        if (Keyboard.current.f12Key.wasPressedThisFrame)
        {
            ScreenCapture.CaptureScreenshot($"Screenshot_{Screen.width}x{Screen.height}.png");
        }

        NativeArray<Color32> pixels = colorBuffer.GetRawTextureData<Color32>();
        for (int y = 0; y < paddedResolution.y; ++y)
        {
            for (int x = 0; x < paddedResolution.x; ++x)
            {
                int index = y * paddedResolution.x + x;
                if (x >= RenderResolution.x || y >= RenderResolution.y)
                {
                    pixels[index] = new Color32(255, 255, 0, 255);
                }
                else if (x == 0 || x == (int)RenderResolution.x - 1)
                {
                    pixels[index] = new Color32(255, 0, 0, 255);
                }
                else if (y == 0 || y == (int)RenderResolution.y - 1)
                {
                    pixels[index] = new Color32(0, 255, 0, 255);
                }
                else if (x < (int)RenderResolution.x / 2)
                {
                    pixels[index] = StripeColor(x);
                }
                else
                {
                    pixels[index] = StripeColor(y);
                }
            }
        }

        colorBuffer.Apply(false, false);
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
    }

    private void OnValidate()
    {
        RenderResolution.x = Mathf.Round(RenderResolution.x);
        RenderResolution.y = Mathf.Round(RenderResolution.y);
    }
}
