namespace ADOFAIVfxRenderer.Core;

public sealed record RenderProgressSnapshot(
    long CompletedFrames,
    long TotalFrames,
    TimeSpan Elapsed,
    TimeSpan CurrentFrameElapsed)
{
    public double Percent => TotalFrames <= 0
        ? 0
        : Math.Clamp(CompletedFrames * 100d / TotalFrames, 0d, 100d);

    public double RenderedFramesPerSecond => Elapsed.TotalSeconds <= 0
        ? 0
        : CompletedFrames / Elapsed.TotalSeconds;

    public double SecondsPerFrame => RenderedFramesPerSecond <= 0
        ? 0
        : 1d / RenderedFramesPerSecond;

    public TimeSpan? EstimatedRemaining
    {
        get
        {
            if (CompletedFrames <= 0 || TotalFrames <= CompletedFrames || Elapsed <= TimeSpan.Zero)
            {
                return TotalFrames <= CompletedFrames ? TimeSpan.Zero : null;
            }

            var seconds = (TotalFrames - CompletedFrames) / RenderedFramesPerSecond;
            return TimeSpan.FromSeconds(seconds);
        }
    }

    public string SpeedText => RenderedFramesPerSecond >= 1d
        ? $"{RenderedFramesPerSecond:0.00} 帧/秒 · {SecondsPerFrame:0.000} 秒/帧"
        : $"{RenderedFramesPerSecond:0.000} 帧/秒 · {SecondsPerFrame:0.00} 秒/帧";
}
