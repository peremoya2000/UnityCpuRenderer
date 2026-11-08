using System;
using System.Collections.Generic;
using System.Diagnostics;

public class FrameTimeStatistics
{
    private const double ROLLING_WINDOW_TIME = 1.0;
    private const int INITIAL_BUFFER_SIZE = (int)(ROLLING_WINDOW_TIME * 240);

    private struct FrameSample
    {
        public double Timestamp;
        public double DeltaTime;
    }

    private readonly Stopwatch stopwatch = new Stopwatch();
    private readonly Queue<FrameSample> frameSamples = new Queue<FrameSample>(INITIAL_BUFFER_SIZE);
    private double currentFrameStartTime;
    private bool isMeasuringFrame = false;
    private double sessionTotalTime = 0;
    private long sessionFrameCount = 0;

    public double RollingAverageFrameTime { get; private set; }
    public double RollingAverageFPS => RollingAverageFrameTime > 0 ? 1.0 / RollingAverageFrameTime : 0;
    public double RollingMinFrameTime { get; private set; } = 0;
    public double RollingMaxFrameTime { get; private set; } = 0;
    public double SessionAverageFrameTime => sessionFrameCount > 0 ? sessionTotalTime / sessionFrameCount : 0;
    public double SessionAverageFPS => SessionAverageFrameTime > 0 ? 1.0 / SessionAverageFrameTime : 0;
    public double SessionMinFrameTime { get; private set; }
    public double SessionMaxFrameTime { get; private set; }

    public void StartMeasuringFrame()
    {
        UnityEngine.Debug.Assert(!isMeasuringFrame, "Already measuring a frame. Call FinishMeasuringFrame() before starting a new measurement.");
        if (!stopwatch.IsRunning) stopwatch.Start();

        currentFrameStartTime = stopwatch.Elapsed.TotalSeconds;
        isMeasuringFrame = true;
    }

    public void FinishMeasuringFrame()
    {
        UnityEngine.Debug.Assert(isMeasuringFrame, "Not currently measuring a frame. Call StartMeasuringFrame() before finishing a measurement.");

        double currentFrameEndTime = stopwatch.Elapsed.TotalSeconds;
        double deltaTime = currentFrameEndTime - currentFrameStartTime;
        isMeasuringFrame = false;
        AddFrameTime(deltaTime, currentFrameEndTime);
    }

    /// <summary>
    /// Adds a frame time to the buffer. Old frames are automatically removed
    /// </summary>
    public void AddFrameTime(double deltaTime, double timeStamp)
    {
        frameSamples.Enqueue(new FrameSample { Timestamp = timeStamp, DeltaTime = deltaTime });
        while (frameSamples.Count > 0 && (timeStamp - frameSamples.Peek().Timestamp) > ROLLING_WINDOW_TIME)
        {
            frameSamples.Dequeue();
        }

        UpdateRollingStats();
        UpdateAllSessionStats(deltaTime);
    }

    public void Reset()
    {
        UnityEngine.Debug.Assert(!isMeasuringFrame, "Shouldn't reset while measuring a frame. Call FinishMeasuringFrame() before resetting.");

        stopwatch.Stop();
        stopwatch.Reset();
        frameSamples.Clear();

        currentFrameStartTime = 0;

        sessionTotalTime = 0;
        sessionFrameCount = 0;

        RollingAverageFrameTime = 0;
        RollingMinFrameTime = 0;
        RollingMaxFrameTime = 0;

        SessionMinFrameTime = 0;
        SessionMaxFrameTime = 0;
    }

    /// <summary>
    /// Returns a formatted string with frame time stats.
    /// </summary>
    public string GetBasicStats()
    {
        return $"FrameTime: {RollingAverageFrameTime * 1000:F2}ms | FPS: {RollingAverageFPS:F1}";
    }

    public string GetDetailedStats()
    {
        return $"Rolling Stats - Avg: {RollingAverageFrameTime * 1000:F2}ms | Min: {RollingMinFrameTime * 1000:F2}ms | Max: {RollingMaxFrameTime * 1000:F2}ms | FPS: {RollingAverageFPS:F1}\n" +
               GetAllSessionStats();
    }

    public string GetAllSessionStats()
    {
        return $"Session Stats - Avg: {SessionAverageFrameTime * 1000:F2}ms | Min: {SessionMinFrameTime * 1000:F2}ms | Max: {SessionMaxFrameTime * 1000:F2}ms | FPS: {SessionAverageFPS:F1}";
    }

    private void UpdateRollingStats()
    {
        double min = double.MaxValue;
        double max = double.MinValue;
        double totalTime = 0;

        foreach (FrameSample sample in frameSamples)
        {
            min = Math.Min(min, sample.DeltaTime);
            max = Math.Max(max, sample.DeltaTime);
            totalTime += sample.DeltaTime;
        }

        RollingMinFrameTime = min;
        RollingMaxFrameTime = max;
        RollingAverageFrameTime = frameSamples.Count > 0 ? totalTime / frameSamples.Count : 0;
    }

    private void UpdateAllSessionStats(double deltaTime)
    {
        if (sessionFrameCount == 0)
        {
            SessionMinFrameTime = deltaTime;
            SessionMaxFrameTime = deltaTime;
        }
        else
        {
            SessionMinFrameTime = Math.Min(SessionMinFrameTime, deltaTime);
            SessionMaxFrameTime = Math.Max(SessionMaxFrameTime, deltaTime);
        }
        sessionTotalTime += deltaTime;
        sessionFrameCount++;
    }
}