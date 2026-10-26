namespace ADOFAIVfxRenderer.Core;

[Flags]
public enum RenderContent
{
    None = 0,
    BackgroundDecorations = 1 << 0,
    ForegroundDecorations = 1 << 1,
    Track = 1 << 2,
    Planets = 1 << 3,
    Filters = 1 << 4,
}

public enum OutputProfile
{
    Video,
    VideoPro,
    VideoVfx,
    VideoVfxPro,
}

public sealed record OutputProfileDefinition(
    OutputProfile Profile,
    string DisplayName,
    RenderContent VideoContent,
    RenderContent LiveLevelContent,
    string Description)
{
    public bool RendersFiltersOffline => VideoContent.HasFlag(RenderContent.Filters);
}

public static class OutputProfiles
{
    private static readonly IReadOnlyDictionary<OutputProfile, OutputProfileDefinition> Definitions =
        new Dictionary<OutputProfile, OutputProfileDefinition>
        {
            [OutputProfile.Video] = new(
                OutputProfile.Video,
                "Video",
                RenderContent.BackgroundDecorations | RenderContent.ForegroundDecorations | RenderContent.Filters,
                RenderContent.Track | RenderContent.Planets,
                "视频包含滤镜与全部装饰；关卡实时保留轨道与星球。"),
            [OutputProfile.VideoPro] = new(
                OutputProfile.VideoPro,
                "Video Pro",
                RenderContent.BackgroundDecorations | RenderContent.ForegroundDecorations | RenderContent.Track |
                RenderContent.Filters,
                RenderContent.None,
                "视频包含装饰、轨道与滤镜；星球默认不录制，派生关卡只保留不可见的游玩逻辑。"),
            [OutputProfile.VideoVfx] = new(
                OutputProfile.VideoVfx,
                "Video VFX",
                RenderContent.BackgroundDecorations | RenderContent.ForegroundDecorations,
                RenderContent.Track | RenderContent.Planets | RenderContent.Filters,
                "视频只含装饰；轨道、星球和滤镜由游戏实时渲染。"),
            [OutputProfile.VideoVfxPro] = new(
                OutputProfile.VideoVfxPro,
                "Video VFX Pro",
                RenderContent.BackgroundDecorations,
                RenderContent.ForegroundDecorations | RenderContent.Track | RenderContent.Planets | RenderContent.Filters,
                "视频只含深度大于等于零的背景装饰；前景装饰留在关卡中。"),
        };

    public static IReadOnlyCollection<OutputProfileDefinition> All { get; } = Definitions.Values.ToArray();

    public static OutputProfileDefinition Get(OutputProfile profile) => Definitions[profile];
}
