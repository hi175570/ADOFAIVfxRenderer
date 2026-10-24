namespace ADOFAIVfxRenderer.Core;

public sealed record RenderVariant(
    OutputProfile Profile,
    VideoResolution Resolution,
    int FramesPerSecond,
    VideoQualityPreset Quality,
    bool IncludePlanets = true)
{
    public string DisplayName =>
        $"{OutputProfiles.Get(Profile).DisplayName} · {Resolution.Name} · {FramesPerSecond} FPS · {Quality}";
}

public sealed record RenderBatchRequest(
    decimal NaturalDurationSeconds,
    TailExtension Tail,
    IReadOnlyCollection<RenderVariant> Variants,
    ResolutionRenderStrategy ResolutionStrategy);

public sealed record RenderPassPlan(
    OutputProfile Profile,
    bool IncludePlanets,
    VideoResolution MasterResolution,
    IReadOnlyList<int> FrameRates,
    IReadOnlyList<RenderVariant> Outputs,
    long UniqueRenderedFrames);

public sealed record RenderBatchPlan(
    decimal TotalDurationSeconds,
    int TimelineTicksPerSecond,
    long UniqueSceneMoments,
    long TotalEncodedFrames,
    IReadOnlyList<RenderPassPlan> Passes,
    IReadOnlyList<RenderVariant> Outputs);

public static class RenderBatchPlanner
{
    public static RenderBatchPlan Create(RenderBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.NaturalDurationSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "谱面自然时长不能为负数。");
        }

        if (request.Variants.Count == 0)
        {
            throw new ArgumentException("至少需要一个输出版本。", nameof(request));
        }

        var variants = request.Variants.Distinct().ToArray();
        foreach (var variant in variants)
        {
            if (!VideoPresets.IsSupportedFrameRate(variant.FramesPerSecond))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    $"不支持的输出帧率：{variant.FramesPerSecond} FPS。");
            }

            if (variant.Resolution.Width <= 0 || variant.Resolution.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "输出分辨率必须大于零。");
            }
        }

        var totalDuration = request.NaturalDurationSeconds + request.Tail.Seconds;
        var allFrameRates = variants.Select(static variant => variant.FramesPerSecond).Distinct().ToArray();
        var timebase = LeastCommonMultiple(allFrameRates);
        var uniqueSceneMoments = CountUniqueMoments(totalDuration, allFrameRates, timebase);
        var totalEncodedFrames = variants.Sum(variant => FrameCount(totalDuration, variant.FramesPerSecond));

        IEnumerable<IGrouping<object, RenderVariant>> grouped = request.ResolutionStrategy switch
        {
            ResolutionRenderStrategy.SharedHighestResolution => variants.GroupBy(
                static variant => (object)new SharedPassKey(variant.Profile, variant.IncludePlanets)),
            ResolutionRenderStrategy.NativePerResolution => variants.GroupBy(
                static variant => (object)new NativePassKey(variant.Profile, variant.IncludePlanets, variant.Resolution)),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };

        var passes = grouped
            .Select(group =>
            {
                var outputs = group.ToArray();
                var frameRates = outputs.Select(static output => output.FramesPerSecond).Distinct().Order().ToArray();
                var masterResolution = outputs.OrderByDescending(static output => output.Resolution.PixelCount).First().Resolution;
                return new RenderPassPlan(
                    outputs[0].Profile,
                    outputs[0].IncludePlanets,
                    masterResolution,
                    frameRates,
                    outputs,
                    CountUniqueMoments(totalDuration, frameRates, LeastCommonMultiple(frameRates)));
            })
            .OrderBy(static pass => pass.Profile)
            .ThenByDescending(static pass => pass.MasterResolution.PixelCount)
            .ToArray();

        return new RenderBatchPlan(
            totalDuration,
            timebase,
            uniqueSceneMoments,
            totalEncodedFrames,
            passes,
            variants);
    }

    public static IReadOnlyList<RenderVariant> BuildVariants(
        IEnumerable<OutputProfile> profiles,
        IEnumerable<VideoResolution> resolutions,
        IEnumerable<int> frameRates,
        VideoQualityPreset quality)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(resolutions);
        ArgumentNullException.ThrowIfNull(frameRates);

        return (
            from profile in profiles.Distinct()
            from resolution in resolutions.Distinct()
            from frameRate in frameRates.Distinct()
            select new RenderVariant(profile, resolution, frameRate, quality))
            .ToArray();
    }

    public static long FrameCount(decimal durationSeconds, int framesPerSecond)
    {
        if (durationSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }

        if (framesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        return checked((long)decimal.Ceiling(durationSeconds * framesPerSecond));
    }

    public static long CountUniqueMoments(decimal durationSeconds, IReadOnlyCollection<int> frameRates, int timebase)
    {
        if (frameRates.Count == 0 || durationSeconds == 0)
        {
            return 0;
        }

        var moments = new HashSet<long>();
        foreach (var frameRate in frameRates)
        {
            var frames = FrameCount(durationSeconds, frameRate);
            var tickStep = timebase / frameRate;
            for (long frame = 0; frame < frames; frame++)
            {
                moments.Add(checked(frame * tickStep));
            }
        }

        return moments.Count;
    }

    public static int LeastCommonMultiple(IEnumerable<int> values)
    {
        var result = 1;
        var any = false;
        foreach (var value in values.Distinct())
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(values), "帧率必须大于零。");
            }

            any = true;
            result = checked(result / GreatestCommonDivisor(result, value) * value);
        }

        if (!any)
        {
            throw new ArgumentException("至少需要一个帧率。", nameof(values));
        }

        return result;
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return Math.Abs(left);
    }

    private sealed record SharedPassKey(OutputProfile Profile, bool IncludePlanets);

    private sealed record NativePassKey(OutputProfile Profile, bool IncludePlanets, VideoResolution Resolution);
}
