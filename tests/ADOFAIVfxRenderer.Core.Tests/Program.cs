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
    ("Duplicate JSON fields use the last value", DuplicateJsonFieldsUseLastValue),
    ("Legacy JSON spellings remain readable", LegacyJsonSpellingsRemainReadable),
    ("Timeline handles midspins and tempo", TimelineHandlesMidspinsAndTempo),
    ("Floor geometry uses ADOFAI axis convention", FloorGeometryUsesAdoAxisConvention),
    ("Current target timeline when present", CurrentTargetTimeline),
    ("Derived Video VFX ownership", DerivedVideoVfxOwnership),
    ("Derived Video VFX Pro ownership", DerivedVideoVfxProOwnership),
    ("Derived Video ownership", DerivedVideoOwnership),
    ("Derived Video Pro ownership", DerivedVideoProOwnership),
    ("Derived chart retains selected decoration", DerivedChartRetainsSelectedDecoration),
    ("Decoration catalog uses first invocation", DecorationCatalogUsesFirstInvocation),
    ("Filter catalog groups by target", FilterCatalogGroupsByTarget),
    ("Texture catalog finds unique referenced files", TextureCatalogFindsUniqueReferencedFiles),
    ("Derived chart disables selected filters", DerivedChartDisablesSelectedFilters),
    ("Canonical render output layout", CanonicalRenderOutputLayout),
    ("Derived chart replaces canonical profile output", DerivedChartReplacesCanonicalOutput),
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

static void CanonicalRenderOutputLayout()
{
    var chartPath = Path.Combine(Path.GetTempPath(), "level", "main.adofai");
    var first = RenderOutputLayout.Create(chartPath, OutputProfile.VideoVfx);
    var second = RenderOutputLayout.Create(chartPath, OutputProfile.VideoVfx);
    Equal(first, second);
    Equal(Path.Combine(Path.GetTempPath(), "level", "Video VFX"), first.OutputDirectory);
    Equal(Path.Combine(first.OutputDirectory, "Video VFX.mp4"), first.VideoPath);
    Equal(Path.Combine(Path.GetTempPath(), "level", "main - Video VFX.adofai"), first.DerivedChartPath);

    var pro = RenderOutputLayout.Create(chartPath, OutputProfile.VideoPro);
    Equal(Path.Combine(first.OutputDirectory, "Video Pro.mp4"), pro.VideoPath);
    True(first.VideoPath != pro.VideoPath);
}

static void DerivedChartReplacesCanonicalOutput()
{
    var root = Path.Combine(Path.GetTempPath(), $"adofai-vfx-canonical-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var chartPath = Path.Combine(root, "main.adofai");
        var videoDirectory = Path.Combine(root, RenderOutputLayout.FolderName);
        Directory.CreateDirectory(videoDirectory);
        var videoPath = Path.Combine(videoDirectory, "Video VFX.mp4");
        File.WriteAllBytes(videoPath, [1]);
        File.WriteAllText(chartPath, """
        {
          "angleData": [0, 180],
          "settings": {"version": 19, "bpm": 120},
          "actions": [],
          "decorations": []
        }
        """);

        var first = DerivedChartExporter.Export(chartPath, videoPath, OutputProfile.VideoVfx);
        File.AppendAllText(first.ChartPath, "stale");
        var second = DerivedChartExporter.Export(chartPath, videoPath, OutputProfile.VideoVfx);

        Equal(first.ChartPath, second.ChartPath);
        Equal(Path.Combine(root, "main - Video VFX.adofai"), second.ChartPath);
        False(File.Exists(Path.Combine(root, "main - Video VFX-2.adofai")));
        False(File.ReadAllText(second.ChartPath).EndsWith("stale", StringComparison.Ordinal));
        var derived = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(second.ChartPath))!.AsObject();
        Equal("Video VFX/Video VFX.mp4", derived["settings"]!["bgVideo"]!.GetValue<string>());
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
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

    var recommendedRoot = Chart(0d);
    var recommendedPath = Path.Combine(Path.GetTempPath(), $"adofai-vfx-render-start-{Guid.NewGuid():N}.adofai");
    try
    {
        File.WriteAllText(recommendedPath, recommendedRoot.ToJsonString());
        Equal(2.0m, DerivedChartExporter.CalculateRecommendedRenderStartSeconds(recommendedPath));
    }
    finally
    {
        if (File.Exists(recommendedPath)) File.Delete(recommendedPath);
    }

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

static void DuplicateJsonFieldsUseLastValue()
{
    var path = Path.Combine(Path.GetTempPath(), $"adofai-vfx-duplicate-json-{Guid.NewGuid():N}.adofai");
    try
    {
        File.WriteAllText(path, """
        {
          "angleData": [0, 180],
          "settings": {
            "bpm": 60,
            "bpm": 120,
            "countdownTicks": 4,
            "separateCountdownTime": true
          },
          "actions": [],
          "decorations": [
            {"floor": 0, "eventType": "AddDecoration", "visible": false, "visible": true}
          ]
        }
        """);
        Equal(2.0m, DerivedChartExporter.CalculateRecommendedRenderStartSeconds(path));
        Equal(1, DecorationCatalogReader.Read(path).Count);
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

static void LegacyJsonSpellingsRemainReadable()
{
    var directory = Path.Combine(Path.GetTempPath(), $"adofai-vfx-legacy-json-{Guid.NewGuid():N}");
    var path = Path.Combine(directory, "level.adofai");
    var video = Path.Combine(directory, "video.mp4");
    try
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, """
        {
          "angleData": [0, 180],
          "settings": {
            "bpm": 120,
            "countdownTicks": 4,
            "separateCountdownTime": "Enabled",
            "requiredMods": [],,
          },
          "actions": [],,
          "decorations": []
        }
        """);
        File.WriteAllBytes(video, [0]);
        Equal(2.0m, DerivedChartExporter.CalculateRecommendedRenderStartSeconds(path));
        Equal(0, DerivedChartExporter.Export(path, video, OutputProfile.VideoVfx).VideoOffsetMilliseconds);
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

static void FloorGeometryUsesAdoAxisConvention()
{
    var positions = AdoGeometryCalculator.BuildFloorPositions([0d, 90d, 999d], 1.5d);
    Near(1.5d, positions[1].X, 0.000001d);
    Near(0d, positions[1].Y, 0.000001d);
    Near(1.5d, positions[2].X, 0.000001d);
    Near(1.5d, positions[2].Y, 0.000001d);
    Near(1.5d, positions[3].X, 0.000001d);
    Near(0d, positions[3].Y, 0.000001d);
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

static void DerivedVideoOwnership() => TestDerivedOwnership(
    OutputProfile.Video,
    expectedDecorationCount: 4,
    expectedRemainingDepth: null,
    expectFilterDisabled: true,
    expectText: false,
    expectObject: false);

static void DerivedVideoProOwnership() => TestDerivedOwnership(
    OutputProfile.VideoPro,
    expectedDecorationCount: 4,
    expectedRemainingDepth: null,
    expectFilterDisabled: true,
    expectHiddenGameplayVisuals: true,
    expectText: false,
    expectObject: false);

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

static void FilterCatalogGroupsByTarget()
{
    var root = Path.Combine(Path.GetTempPath(), $"adofai-vfx-filters-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var chartPath = Path.Combine(root, "main.adofai");
        File.WriteAllText(chartPath, """
        {
          "angleData": [0, 180],
          "settings": {"version": 19, "bpm": 120},
          "actions": [
            {"floor": 4, "eventType": "SetFilter", "filter": "Blur"},
            {"floor": 8, "eventType": "SetFilter", "filter": "Blur"},
            {"floor": 3, "eventType": "SetFilterAdvanced", "plane": "Foreground", "filter": "CameraFilterPack_Glow_Glow", "targetType": "Decoration", "targetTag": "mask-a"},
            {"floor": 5, "eventType": "SetFilterAdvanced", "plane": "Foreground", "filter": "CameraFilterPack_Glow_Glow", "targetType": "Decoration", "targetTag": "mask-b"},
            {"floor": 6, "eventType": "HallOfMirrors", "enabled": true},
            {"floor": 0, "eventType": "SetFilterAdvanced", "plane": "Foreground", "filter": "Unused", "enabled": false}
          ],
          "decorations": []
        }
        """);

        var items = FilterCatalogReader.Read(chartPath);
        Equal(4, items.Count);
        var blur = items.Single(item => item.Key == "basic:Blur");
        Equal(2, blur.InvocationCount);
        Equal(4, blur.FirstFloor);
        True(items.Any(item => item.Key.EndsWith(":Decoration:mask-a", StringComparison.Ordinal)));
        True(items.Any(item => item.Key.EndsWith(":Decoration:mask-b", StringComparison.Ordinal)));
        False(items.Any(item => item.DisplayName == "Unused"));
        False(items.Single(item => item.Key.EndsWith(":Decoration:mask-a", StringComparison.Ordinal)).OfflineSupported);
        False(items.Single(item => item.Key == "builtin:HallOfMirrors").OfflineSupported);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void TextureCatalogFindsUniqueReferencedFiles()
{
    var root = Path.Combine(Path.GetTempPath(), $"adofai-vfx-textures-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var chartPath = Path.Combine(root, "main.adofai");
        var background = Path.Combine(root, "background.png");
        var decoration = Path.Combine(root, "decoration.png");
        var track = Path.Combine(root, "track.png");
        File.WriteAllBytes(background, [1]);
        File.WriteAllBytes(decoration, [2]);
        File.WriteAllBytes(track, [3]);
        File.WriteAllText(chartPath, """
        {
          "angleData": [0, 180],
          "settings": {"bgImage": "background.png", "trackTexture": "track.png"},
          "actions": [
            {"floor": 1, "eventType": "MoveDecorations", "decorationImage": "decoration.png"},
            {"floor": 1, "eventType": "MoveDecorations", "decorationImage": "decoration.png"}
          ],
          "decorations": [
            {"floor": 0, "eventType": "AddDecoration", "decorationImage": "decoration.png"},
            {"floor": 0, "eventType": "AddDecoration", "decorationImage": "prefab:built-in"}
          ]
        }
        """);

        var files = ChartTextureCatalogReader.ReadReferencedFiles(chartPath);
        Equal(3, files.Count);
        True(files.Contains(background, StringComparer.OrdinalIgnoreCase));
        True(files.Contains(decoration, StringComparer.OrdinalIgnoreCase));
        True(files.Contains(track, StringComparer.OrdinalIgnoreCase));
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void DerivedChartDisablesSelectedFilters()
{
    var root = Path.Combine(Path.GetTempPath(), $"adofai-vfx-filter-export-{Guid.NewGuid():N}");
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
          "actions": [
            {"floor": 0, "eventType": "SetFilter", "filter": "Blur", "enabled": true},
            {"floor": 0, "eventType": "SetFilter", "filter": "Sepia", "enabled": true},
            {"floor": 0, "eventType": "ScreenTile", "tile": [3, 4]}
          ],
          "decorations": []
        }
        """);

        var disabled = new HashSet<string>(StringComparer.Ordinal) { "basic:Blur", "builtin:ScreenTile" };
        var result = DerivedChartExporter.Export(chartPath, videoPath, OutputProfile.VideoVfx, disabledFilterKeys: disabled);
        Equal(2, result.DisabledFilterEvents);
        var actions = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(result.ChartPath))!["actions"]!.AsArray();
        False(actions.Single(node => node!["filter"]?.GetValue<string>() == "Blur")!["enabled"]!.GetValue<bool>());
        True(actions.Single(node => node!["filter"]?.GetValue<string>() == "Sepia")!["enabled"]!.GetValue<bool>());
        var tile = actions.Single(node => node!["eventType"]!.GetValue<string>() == "ScreenTile")!["tile"]!.AsArray();
        Equal(1, tile[0]!.GetValue<int>());
        Equal(1, tile[1]!.GetValue<int>());
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void TestDerivedOwnership(
    OutputProfile profile,
    int expectedDecorationCount,
    int? expectedRemainingDepth,
    bool expectFilterDisabled = false,
    bool expectHiddenGameplayVisuals = false,
    bool expectText = true,
    bool expectObject = true)
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
          "settings": {"version": 19, "bpm": 120, "countdownTicks": 4, "separateCountdownTime": true, "trackColor": "abcdef", "secondaryTrackColor": "123456ff"},
          "actions": [
            {"floor": 0, "eventType": "CustomBackground", "color": "000000"},
            {"floor": 0, "eventType": "SetFilter", "filter": "Blur", "enabled": true},
            {"floor": 0, "eventType": "RecolorTrack", "trackColor": "ffffff", "secondaryTrackColor": "010203ff"},
            {"floor": 0, "eventType": "MoveTrack", "opacity": 100},
            {"floor": 0, "eventType": "ScalePlanets", "scale": 100}
          ],
          "decorations": [
            {"floor": 0, "eventType": "AddDecoration", "decorationImage": "bg.png", "depth": 10},
            {"floor": 0, "eventType": "AddDecoration", "decorationImage": "fg.png", "depth": -10},
            {"floor": 0, "eventType": "AddText", "decText": "keep me", "depth": 0},
            {"floor": 0, "eventType": "AddObject", "objectType": "Planet", "depth": 0}
          ]
        }
        """);

        var result = DerivedChartExporter.Export(chartPath, videoPath, profile);
        Equal(expectedDecorationCount, result.RemovedDecorations);
        Equal(1, result.RemovedBackgroundEvents);
        Equal(0, result.VideoOffsetMilliseconds);
        var derived = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(result.ChartPath))!.AsObject();
        Equal("output/render.mp4", derived["settings"]!["bgVideo"]!.GetValue<string>());
        var actions = derived["actions"]!.AsArray();
        var filter = actions.Single(node => node!["eventType"]!.GetValue<string>() == "SetFilter")!;
        Equal(!expectFilterDisabled, filter["enabled"]!.GetValue<bool>());
        var remaining = derived["decorations"]!.AsArray();
        Equal(expectText, remaining.Any(node => node!["eventType"]!.GetValue<string>() == "AddText"));
        Equal(expectObject, remaining.Any(node => node!["eventType"]!.GetValue<string>() == "AddObject"));
        if (expectedRemainingDepth.HasValue)
        {
            True(remaining.Any(node => node!["eventType"]!.GetValue<string>() == "AddDecoration" &&
                                       node["depth"]!.GetValue<int>() == expectedRemainingDepth.Value));
        }
        else
        {
            False(remaining.Any(node => node!["eventType"]!.GetValue<string>() == "AddDecoration"));
        }
        if (expectHiddenGameplayVisuals)
        {
            Equal("abcdef00", derived["settings"]!["trackColor"]!.GetValue<string>());
            Equal("12345600", derived["settings"]!["secondaryTrackColor"]!.GetValue<string>());
            Equal("ffffff00", actions.Single(node => node!["eventType"]!.GetValue<string>() == "RecolorTrack")!["trackColor"]!.GetValue<string>());
            Equal(0, actions.Single(node => node!["eventType"]!.GetValue<string>() == "MoveTrack")!["opacity"]!.GetValue<int>());
            Equal(0, actions.Single(node => node!["eventType"]!.GetValue<string>() == "ScalePlanets")!["scale"]!.GetValue<int>());
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
