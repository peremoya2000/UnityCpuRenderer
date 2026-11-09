using Unity.Burst;
using Unity.Jobs;
using UnityEngine;

[BurstCompile(CompileSynchronously = true)]
public struct ClearJob : IJob
{
    public Framebuffer Framebuffer;
    public Color32 ClearColor;

    public void Execute()
    {
        for (int i = 0; i < Framebuffer.Color.Length; ++i)
        {
            Framebuffer.Color[i] = ClearColor;
            Framebuffer.InvDepth[i] = 0f;
        }
    }
}
