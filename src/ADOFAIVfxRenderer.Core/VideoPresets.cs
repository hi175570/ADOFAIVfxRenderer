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

public static class VideoBitrateCalculator
{
    public static int GetKilobitsPerSecond(
        VideoResolution resolution,
        int framesPerSecond,
        VideoQualityPreset quality,
        int? customKilobitsPerSecond = null)
    {
        if (quality == VideoQualityPreset.Custom)
        {
            if (customKilobitsPerSecond is null or <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(customKilobitsPerSecond), "自定义码率必须大于零。");
            }

            return customKilobitsPerSecond.Value;
        }

        var bitsPerPixelPerFrame = quality switch
        {
            VideoQualityPreset.SmallerFile => 0.04m,
            VideoQualityPreset.Balanced => 0.08m,
            VideoQualityPreset.HighQuality => 0.14m,
            _ => throw new ArgumentOutOfRangeException(nameof(quality)),
        };
        var calculated = decimal.Ceiling(
            resolution.PixelCount * framesPerSecond * bitsPerPixelPerFrame / 1000m);
        var minimum = quality switch
        {
            VideoQualityPreset.SmallerFile => 96,
            VideoQualityPreset.Balanced => 160,
            VideoQualityPreset.HighQuality => 256,
            _ => 96,
        };
        return Math.Max(minimum, checked((int)calculated));
    }
}

public enum ResolutionRenderStrategy
{
    SharedHighestResolution,
    NativePerResolution,
}
