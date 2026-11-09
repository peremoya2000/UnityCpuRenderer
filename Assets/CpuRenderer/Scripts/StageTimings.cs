using System;
using System.Text;
using Unity.Profiling;

public class StageTimings : IDisposable
{
    private const string MARKER_PREFIX = "CpuRenderer.";
    private const int FRAME_WINDOW = 120;
    private const double NANOSECONDS_PER_MILLISECOND = 1e6;

    private static readonly string[] stageNames = { "Clear", "Gather", "WorldToClip", "CullClip", "Setup", "Raster", "Apply" };

    private readonly ProfilerRecorder[] recorders = new ProfilerRecorder[stageNames.Length];
    private readonly StringBuilder builder = new StringBuilder();

    public StageTimings()
    {
        for (int i = 0; i < stageNames.Length; ++i)
        {
            recorders[i] = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, MARKER_PREFIX + stageNames[i], FRAME_WINDOW);
        }
    }

    public string GetStats()
    {
        builder.Clear();
        builder.Append($"Stage times (ms, average of last {FRAME_WINDOW} frames):");
        for (int i = 0; i < recorders.Length; ++i)
        {
            builder.Append(i == 0 ? " " : " | ").Append(stageNames[i]).Append(' ');
            builder.Append(recorders[i].Count > 0 ? AverageMilliseconds(recorders[i]).ToString("F2") : "no samples");
        }
        return builder.ToString();
    }

    public void Dispose()
    {
        for (int i = 0; i < recorders.Length; ++i)
        {
            recorders[i].Dispose();
        }
    }

    private static double AverageMilliseconds(ProfilerRecorder recorder)
    {
        long totalNanoseconds = 0;
        for (int i = 0; i < recorder.Count; ++i)
        {
            totalNanoseconds += recorder.GetSample(i).Value;
        }
        return totalNanoseconds / (double)recorder.Count / NANOSECONDS_PER_MILLISECOND;
    }
}
