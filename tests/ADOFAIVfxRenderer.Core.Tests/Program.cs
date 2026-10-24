using ADOFAIVfxRenderer.Core;
using ADOFAIVfxTimeline;

var tests = new (string Name, Action Run)[]
{
    ("Resolution presets", ResolutionPresets),
    ("Frame-rate presets", FrameRatePresets),
    ("Tail seconds to frames", TailSecondsToFrames),
    ("Tail frames to seconds", TailFramesToSeconds),
    ("120 FPS family shares moments", Fps120FamilySharesMoments),
    ("120 and 144 FPS union is exact", Fps120And144Union),
    ("Variant matrix", VariantMatrix),
    ("Output profile ownership", OutputProfileOwnership),
    ("Bitrate presets", BitratePresets),
    ("Render progress", RenderProgress),
    ("Invalid game path", InvalidGamePath),
    ("Selected chart version", SelectedChartVersion),
    ("Current local game probe when present", CurrentLocalGameProbe),
    ("Derived chart video offset", DerivedChartVideoOffset),
    ("Video sync follows first playable floor", VideoSyncFollowsFirstPlayableFloor),
    ("Timeline handles midspins and tempo", TimelineHandlesMidspinsAndTempo),
    ("Current target timeline when present", CurrentTargetTimeline),
    ("Derived Video VFX ownership", DerivedVideoVfxOwnership),
    ("Derived Video VFX Pro ownership", DerivedVideoVfxProOwnership),
    ("Derived chart retains selected decoration", DerivedChartRetainsSelectedDecoration),
    ("Decoration catalog uses first invocation", DecorationCatalogUsesFirstInvocation),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.WriteLine($"FAIL  {test.Name}\n      {exception}");
    }
}

Console.WriteLine();
Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} tests passed.");
if (failures.Count > 0)
{
    Environment.ExitCode = 1;
}

return;

static void ResolutionPresets()
{
    Equal((256, 144), (VideoPresets.P144.Width, VideoPresets.P144.Height));
    Equal((640, 360), (VideoPresets.P360.Width, VideoPresets.P360.Height));
    Equal((1280, 720), (VideoPresets.P720.Width, VideoPresets.P720.Height));
    Equal((1920, 1080), (VideoPresets.P1080.Width, VideoPresets.P1080.Height));
    Equal((3840, 2160), (VideoPresets.P2160.Width, VideoPresets.P2160.Height));
}

static void FrameRatePresets() =>
    SequenceEqual([15, 30, 60, 90, 120, 144], VideoPresets.FrameRates);

static void TailSecondsToFrames()
{
    var tail = TailExtension.FromSeconds(5m);
    Equal(75L, tail.GetFrameCount(15));
    Equal(300L, tail.GetFrameCount(60));
    Equal(720L, tail.GetFrameCount(144));
    Equal(1L, TailExtension.FromSeconds(0.001m).GetFrameCount(60));
}

static void TailFramesToSeconds()
{
    var tail = TailExtension.FromFrames(500, 120);
    Equal(500L, tail.GetFrameCount(120));
    Equal(125L, tail.GetFrameCount(30));
}

static void Fps120FamilySharesMoments()
{
    var variants = RenderBatchPlanner.BuildVariants(
        [OutputProfile.VideoVfx],
        [VideoPresets.P1080],
        [15, 30, 60, 120],
        VideoQualityPreset.Balanced);
    var plan = RenderBatchPlanner.Create(new RenderBatchRequest(
        1m,
        TailExtension.Zero,
        variants,
        ResolutionRenderStrategy.SharedHighestResolution));

    Equal(120L, plan.UniqueSceneMoments);
    Equal(120L, plan.Passes.Single().UniqueRenderedFrames);
    Equal(225L, plan.TotalEncodedFrames);
}

static void Fps120And144Union()
{
    var variants = RenderBatchPlanner.BuildVariants(
        [OutputProfile.Video],
        [VideoPresets.P720],
        [120, 144],
        VideoQualityPreset.Balanced);
    var plan = RenderBatchPlanner.Create(new RenderBatchRequest(
        1m,
        TailExtension.Zero,
        variants,
        ResolutionRenderStrategy.SharedHighestResolution));

    Equal(720, plan.TimelineTicksPerSecond);
    Equal(240L, plan.UniqueSceneMoments);
}

static void VariantMatrix()
{
    var variants = RenderBatchPlanner.BuildVariants(
        [OutputProfile.Video, OutputProfile.VideoPro],
        [VideoPresets.P360, VideoPresets.P1080],
        [30, 60],
        VideoQualityPreset.HighQuality);

    Equal(8, variants.Count);
    var plan = RenderBatchPlanner.Create(new RenderBatchRequest(
        1m,
        TailExtension.Zero,
        variants,
        ResolutionRenderStrategy.SharedHighestResolution));
    Equal(2, plan.Passes.Count);
    True(plan.Passes.All(static pass => pass.MasterResolution == VideoPresets.P1080));
}

static void OutputProfileOwnership()
{
    True(OutputProfiles.Get(OutputProfile.Video).RendersFiltersOffline);
    True(OutputProfiles.Get(OutputProfile.VideoPro).RendersFiltersOffline);
    False(OutputProfiles.Get(OutputProfile.VideoVfx).RendersFiltersOffline);
    False(OutputProfiles.Get(OutputProfile.VideoVfxPro).RendersFiltersOffline);
    Equal(
        RenderContent.BackgroundDecorations,
        OutputProfiles.Get(OutputProfile.VideoVfxPro).VideoContent);
    False(new RenderVariant(
        OutputProfile.VideoVfx,
        VideoPresets.P144,
        15,
        VideoQualityPreset.Balanced).Content.HasFlag(RenderContent.Planets));
    True(new RenderVariant(
        OutputProfile.VideoPro,
        VideoPresets.P144,
        15,
        VideoQualityPreset.Balanced).Content.HasFlag(RenderContent.Planets));
}

static void RenderProgress()
{
    var progress = new RenderProgressSnapshot(25, 100, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(2));
    Equal(25d, progress.Percent);
    Equal(0.5d, progress.RenderedFramesPerSecond);
    Equal(2d, progress.SecondsPerFrame);
    Equal(TimeSpan.FromSeconds(150), progress.EstimatedRemaining);
}

static void BitratePresets()
{
    Equal(96, VideoBitrateCalculator.GetKilobitsPerSecond(
        VideoPresets.P144,
        15,
        VideoQualityPreset.SmallerFile));
    Equal(1234, VideoBitrateCalculator.GetKilobitsPerSecond(
        VideoPresets.P1080,
        60,
        VideoQualityPreset.Custom,
        1234));
}

static void InvalidGamePath()
{
    var installation = GameInstallationProbe.Inspect(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
    Equal(GameInstallationStatus.Invalid, installation.Status);
    False(installation.CanRender);
}

static void SelectedChartVersion()
{
    var path = Path.Combine(Path.GetTempPath(), $"adofai-vfx-{Guid.NewGuid():N}.adofai");
    try
    {
        File.WriteAllText(path, "{\"angleData\":[0],\"settings\":{\"version\":19},\"actions\":[],\"decorations\":[]}");
        var metadata = ChartMetadataReader.Read(path);
        Equal(19, metadata.Version);
    }
    finally
    {
        File.Delete(path);
    }
}

static void CurrentLocalGameProbe()
{
    const string path = @"C:\D\games\ADOFAI\A Dance of Fire and Ice";
    if (!Directory.Exists(path))
    {
        Console.WriteLine("      SKIP: current local installation is absent.");
        return;
    }

    var installation = GameInstallationProbe.Inspect(path);
    Equal(GameInstallationStatus.Supported, installation.Status);
    True(installation.CanRender);
    Equal(19, installation.Adapter?.MaximumChartVersion);
    Equal(276, installation.Capabilities.CameraFilterComponentCount);
}

static void DerivedChartVideoOffset()
{
    var settings = new System.Text.Json.Nodes.JsonObject
    {
        ["separateCountdownTime"] = true,
        ["countdownTicks"] = 4,
        ["bpm"] = 135,
    };
    Equal(0, DerivedChartExporter.CalculateVideoOffsetMilliseconds(settings));
    Equal(-500, DerivedChartExporter.CalculateVideoOffsetMilliseconds(settings, 0.5m));
    Equal(-1778, DerivedChartExporter.CalculateVideoOffsetMilliseconds(settings, 4m * 60m / 135m));
    settings["separateCountdownTime"] = false;
    Equal(0, DerivedChartExporter.CalculateVideoOffsetMilliseconds(settings));
}

static void VideoSyncFollowsFirstPlayableFloor()
{
    static System.Text.Json.Nodes.JsonObject Chart(double firstAngle) => new()
    {
        ["angleData"] = new System.Text.Json.Nodes.JsonArray(firstAngle, 0d),
        ["settings"] = new System.Text.Json.Nodes.JsonObject
        {
            ["bpm"] = 120,
            ["countdownTicks"] = 4,
            ["separateCountdownTime"] = true,
        },
        ["actions"] = new System.Text.Json.Nodes.JsonArray(),
    };

    Equal(0, VideoSynchronizationCalculator.Calculate(Chart(0d)).VideoOffsetMilliseconds);
    Equal(-250, VideoSynchronizationCalculator.Calculate(Chart(270d)).VideoOffsetMilliseconds);
    Equal(250, VideoSynchronizationCalculator.Calculate(Chart(90d)).VideoOffsetMilliseconds);
    Equal(-500, VideoSynchronizationCalculator.Calculate(Chart(0d), 0.5m).VideoOffsetMilliseconds);

    const string target = @"C:\D\games\ADOFAI\adofai\[------OH_MY_PCCCCCCCC------]\THE STAR SYSTEM'S CALLING\main.adofai";
    if (File.Exists(target))
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(target))!.AsObject();
        Equal(0, VideoSynchronizationCalculator.Calculate(root).VideoOffsetMilliseconds);
    }
}

static void TimelineHandlesMidspinsAndTempo()
{
    var result = AdoTimelineCalculator.Calculate(
        [360d, 999d, 180d, 360d],
        [
            new TimelineActionData { Order = 0, EventType = "SetSpeed", Floor = 0, SpeedType = "Bpm", BeatsPerMinute = 120d },
            new TimelineActionData { Order = 1, EventType = "Pause", Floor = 3, Duration = 1d },
        ],
        initialBpm: 60d);

    Equal(5, result.FloorTimesSeconds.Length);
    Equal(999d, result.RotationAngles[1]);
    Near(2d, result.DurationSeconds, 0.000001d);
}

static void CurrentTargetTimeline()
{
    const string path = @"C:\D\games\ADOFAI\adofai\[------OH_MY_PCCCCCCCC------]\THE STAR SYSTEM'S CALLING\main.adofai";
    if (!File.Exists(path))
    {
        Console.WriteLine("      SKIP: current target chart is absent.");
        return;
    }

    var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    var settings = root["settings"]!.AsObject();
    var angles = root["angleData"]!.AsArray().Select(node => node!.GetValue<double>()).ToArray();
    var actions = root["actions"]!.AsArray().Select((node, order) =>
    {
        var action = node!.AsObject();
        return new TimelineActionData
        {
            Order = order,
            EventType = action["eventType"]?.GetValue<string>() ?? "",
            Floor = action["floor"]?.GetValue<int>() ?? 0,
            AngleOffset = action["angleOffset"]?.GetValue<double>() ?? 0d,
            SpeedType = action["speedType"]?.GetValue<string>() ?? "",
            BeatsPerMinute = action["beatsPerMinute"]?.GetValue<double>() ?? 0d,
            BpmMultiplier = action["bpmMultiplier"]?.GetValue<double>() ?? 1d,
            Duration = action["duration"]?.GetValue<double>() ?? 0d,
            Planets = action["planets"]?.GetValue<string>() ?? "",
        };
    }).ToArray();

    var result = AdoTimelineCalculator.Calculate(
        angles,
        actions,
        settings["bpm"]!.GetValue<double>(),
        (settings["pitch"]?.GetValue<double>() ?? 100d) / 100d);
    Near(292.977799140286d, result.DurationSeconds, 0.00001d);
}

static void Near(double expected, double actual, double tolerance)
{
    if (Math.Abs(expected - actual) > tolerance)
        throw new InvalidOperationException($"Expected {expected} ± {tolerance}, got {actual}.");
}

static void DerivedVideoVfxOwnership() => TestDerivedOwnership(
    OutputProfile.VideoVfx,
    expectedDecorationCount: 2,
    expectedRemainingDepth: null);

static void DerivedVideoVfxProOwnership() => TestDerivedOwnership(
    OutputProfile.VideoVfxPro,
    expectedDecorationCount: 1,
    expectedRemainingDepth: -10);

static void DerivedChartRetainsSelectedDecoration()
{
    var root = Path.Combine(Path.GetTempPath(), $"adofai-vfx-retained-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var chartPath = Path.Combine(root, "main.adofai");
        var videoPath = Path.Combine(root, "render.mp4");
        File.WriteAllBytes(videoPath, [1]);
        File.WriteAllText(chartPath, """
        {
          "angleData": [0, 180],
          "settings": {"version": 19, "bpm": 120},
          "actions": [],
          "decorations": [
            {"floor": 0, "eventType": "AddDecoration", "decorationImage": "keep.png", "depth": 5},
            {"floor": 0, "eventType": "AddText", "decText": "index spacer"},
            {"floor": 0, "eventType": "AddDecoration", "decorationImage": "bake.png", "depth": -5}
          ]
        }
        """);

        var result = DerivedChartExporter.Export(chartPath, videoPath, OutputProfile.VideoVfx, 0m, new HashSet<int> { 0 });
        Equal(1, result.RemovedDecorations);
        var derived = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(result.ChartPath))!.AsObject();
        var remaining = derived["decorations"]!.AsArray();
        True(remaining.Any(node => node?["decorationImage"]?.GetValue<string>() == "keep.png"));
        False(remaining.Any(node => node?["decorationImage"]?.GetValue<string>() == "bake.png"));
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void DecorationCatalogUsesFirstInvocation()
{
    var root = Path.Combine(Path.GetTempPath(), $"adofai-vfx-catalog-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var chartPath = Path.Combine(root, "main.adofai");
        File.WriteAllText(chartPath, """
        {
          "angleData": [0, 180, 0],
          "settings": {"version": 19, "bpm": 120},
          "actions": [
            {"floor": 1, "eventType": "MoveDecorations", "tag": "hidden", "angleOffset": 90, "visible": true}
          ],
          "decorations": [
            {"floor": 0, "eventType": "AddDecoration", "visible": false, "tag": "hidden", "decorationImage": "a.png", "depth": 1},
            {"floor": 0, "eventType": "AddDecoration", "visible": true, "tag": "shown", "decorationImage": "b.png", "depth": 2}
          ]
        }
        """);

        var items = DecorationCatalogReader.Read(chartPath);
        var hidden = items.Single(item => item.Tag == "hidden");
        Equal(1, hidden.ReferenceFloor);
        Near(0.25d, hidden.TimeSeconds, 0.000001d);
        var shown = items.Single(item => item.Tag == "shown");
        Equal(0, shown.ReferenceFloor);
        Near(0d, shown.TimeSeconds, 0.000001d);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void TestDerivedOwnership(OutputProfile profile, int expectedDecorationCount, int? expectedRemainingDepth)
{
    var root = Path.Combine(Path.GetTempPath(), $"adofai-vfx-export-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var chartPath = Path.Combine(root, "main.adofai");
        var videoDirectory = Path.Combine(root, "output");
        Directory.CreateDirectory(videoDirectory);
        var videoPath = Path.Combine(videoDirectory, "render.mp4");
        File.WriteAllBytes(videoPath, [1, 2, 3]);
        File.WriteAllText(chartPath, """
        {
          "angleData": [0, 180],
          "settings": {"version": 19, "bpm": 120, "countdownTicks": 4, "separateCountdownTime": true},
          "actions": [
            {"floor": 0, "eventType": "CustomBackground", "color": "000000"},
            {"floor": 0, "eventType": "SetFilter", "filter": "Blur"}
          ],
          "decorations": [
            {"floor": 0, "eventType": "AddDecoration", "decorationImage": "bg.png", "depth": 10},
            {"floor": 0, "eventType": "AddDecoration", "decorationImage": "fg.png", "depth": -10},
            {"floor": 0, "eventType": "AddText", "decText": "keep me", "depth": 0}
          ]
        }
        """);

        var result = DerivedChartExporter.Export(chartPath, videoPath, profile);
        Equal(expectedDecorationCount, result.RemovedDecorations);
        Equal(1, result.RemovedBackgroundEvents);
        Equal(0, result.VideoOffsetMilliseconds);
        var derived = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(result.ChartPath))!.AsObject();
        Equal("output/render.mp4", derived["settings"]!["bgVideo"]!.GetValue<string>());
        True(derived["actions"]!.AsArray().Any(node => node!["eventType"]!.GetValue<string>() == "SetFilter"));
        var remaining = derived["decorations"]!.AsArray();
        True(remaining.Any(node => node!["eventType"]!.GetValue<string>() == "AddText"));
        if (expectedRemainingDepth.HasValue)
        {
            True(remaining.Any(node => node!["eventType"]!.GetValue<string>() == "AddDecoration" &&
                                       node["depth"]!.GetValue<int>() == expectedRemainingDepth.Value));
        }
        else
        {
            False(remaining.Any(node => node!["eventType"]!.GetValue<string>() == "AddDecoration"));
        }
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}

static void True(bool value)
{
    if (!value)
    {
        throw new InvalidOperationException("Expected true, got false.");
    }
}

static void False(bool value) => True(!value);

static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException(
            $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
    }
}
