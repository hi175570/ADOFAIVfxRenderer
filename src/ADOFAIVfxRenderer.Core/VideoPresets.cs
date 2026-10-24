namespace ADOFAIVfxRenderer.Core;

public readonly record struct VideoResolution(string Name, int Width, int Height)
{
    public long PixelCount => (long)Width * Height;

    public override string ToString() => $"{Name} ({Width}×{Height})";
}

public static class VideoPresets
{
    public static readonly VideoResolution P144 = new("144p", 256, 144);
    public static readonly VideoResolution P360 = new("360p", 640, 360);
    public static readonly VideoResolution P720 = new("720p", 1280, 720);
    public static readonly VideoResolution P1080 = new("1080p", 1920, 1080);
    public static readonly VideoResolution P2160 = new("2160p", 3840, 2160);

    public static IReadOnlyList<VideoResolution> Resolutions { get; } =
        [P144, P360, P720, P1080, P2160];

    public static IReadOnlyList<int> FrameRates { get; } = [15, 30, 60, 90, 120, 144];

    public static bool IsSupportedFrameRate(int value) => FrameRates.Contains(value);
}

public enum VideoQualityPreset
{
    SmallerFile,
    Balanced,
    HighQuality,
    Custom,
}

public enum ResolutionRenderStrategy
{
    SharedHighestResolution,
    NativePerResolution,
}
