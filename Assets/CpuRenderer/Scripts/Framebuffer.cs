using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public struct Framebuffer
{
    public NativeArray<Color32> Color;
    public NativeArray<float> InvDepth;
    public int BufferWidth;
    public int2 RenderSize;
}
