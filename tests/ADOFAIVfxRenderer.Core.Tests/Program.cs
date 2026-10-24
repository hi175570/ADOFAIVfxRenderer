using ADOFAIVfxRenderer.Core;

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
    ("Derived Video VFX ownership", DerivedVideoVfxOwnership),
    ("Derived Video VFX Pro ownership", DerivedVideoVfxProOwnership),
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
    Equal(1778, DerivedChartExporter.CalculateVideoOffsetMilliseconds(settings));
    Equal(1278, DerivedChartExporter.CalculateVideoOffsetMilliseconds(settings, 0.5m));
    settings["separateCountdownTime"] = false;
    Equal(0, DerivedChartExporter.CalculateVideoOffsetMilliseconds(settings));
}

static void DerivedVideoVfxOwnership() => TestDerivedOwnership(
    OutputProfile.VideoVfx,
    expectedDecorationCount: 2,
    expectedRemainingDepth: null);

static void DerivedVideoVfxProOwnership() => TestDerivedOwnership(
    OutputProfile.VideoVfxPro,
    expectedDecorationCount: 1,
    expectedRemainingDepth: -10);

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
        Equal(2000, result.VideoOffsetMilliseconds);
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
