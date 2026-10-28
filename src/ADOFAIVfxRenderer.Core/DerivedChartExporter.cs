using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ADOFAIVfxRenderer.Core;

public sealed record DerivedChartExportResult(
    string ChartPath,
    string VideoRelativePath,
    int VideoOffsetMilliseconds,
    int RemovedDecorations,
    int RemovedBackgroundEvents,
    int DisabledFilterEvents = 0);

public static class DerivedChartExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static DerivedChartExportResult Export(
        string sourceChartPath,
        string renderedVideoPath,
        OutputProfile profile,
        decimal renderStartSeconds = 0m,
        IReadOnlySet<int>? retainedDecorationIndices = null,
        IReadOnlySet<int>? excludedDecorationIndices = null,
        IReadOnlySet<string>? disabledFilterKeys = null,
        string? outputChartPath = null)
    {
        sourceChartPath = Path.GetFullPath(sourceChartPath);
        renderedVideoPath = Path.GetFullPath(renderedVideoPath);
        if (!File.Exists(sourceChartPath)) throw new FileNotFoundException("找不到源谱面。", sourceChartPath);
        if (!File.Exists(renderedVideoPath)) throw new FileNotFoundException("找不到已渲染的视频。", renderedVideoPath);

        var root = AdoJson.ParseObject(File.ReadAllText(sourceChartPath, Encoding.UTF8));
        var settings = root["settings"] as JsonObject
                       ?? throw new InvalidDataException("谱面缺少 settings。");

        var sourceDirectory = Path.GetDirectoryName(sourceChartPath)!;
        var relativeVideoPath = Path.GetRelativePath(sourceDirectory, renderedVideoPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        var videoOffset = VideoSynchronizationCalculator.Calculate(root, renderStartSeconds).VideoOffsetMilliseconds;

        settings["bgVideo"] = relativeVideoPath;
        settings["vidOffset"] = videoOffset;
        settings["loopVideo"] = false;
        settings["showDefaultBGIfNoImage"] = false;

        var removedDecorations = RemoveBakedDecorations(
            root,
            profile,
            retainedDecorationIndices,
            excludedDecorationIndices);
        var removedBackgroundEvents = RemoveBakedBackgroundEvents(root);
        var disabledFilterEvents = DisableBakedFilters(root, profile, disabledFilterKeys);
        if (profile == OutputProfile.VideoPro) HideLiveTrackAndPlanets(root);

        var outputPath = string.IsNullOrWhiteSpace(outputChartPath)
            ? RenderOutputLayout.Create(sourceChartPath, profile).DerivedChartPath
            : Path.GetFullPath(outputChartPath);
        WriteTextAtomically(
            outputPath,
            root.ToJsonString(JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return new DerivedChartExportResult(
            outputPath,
            relativeVideoPath,
            videoOffset,
            removedDecorations,
            removedBackgroundEvents,
            disabledFilterEvents);
    }

    public static int CalculateVideoOffsetMilliseconds(JsonObject chartOrSettings, decimal renderStartSeconds = 0m)
    {
        if (chartOrSettings["settings"] is JsonObject)
            return VideoSynchronizationCalculator.Calculate(chartOrSettings, renderStartSeconds).VideoOffsetMilliseconds;
        return checked((int)Math.Round(
            -renderStartSeconds * 1000m,
            MidpointRounding.AwayFromZero));
    }

    public static decimal CalculateRecommendedRenderStartSeconds(string sourceChartPath)
    {
        sourceChartPath = Path.GetFullPath(sourceChartPath);
        var root = AdoJson.ParseObject(File.ReadAllText(sourceChartPath, Encoding.UTF8));
        return VideoSynchronizationCalculator.Calculate(root).FirstPlayableFloorSeconds;
    }

    private static int RemoveBakedDecorations(
        JsonObject root,
        OutputProfile profile,
        IReadOnlySet<int>? retainedDecorationIndices,
        IReadOnlySet<int>? excludedDecorationIndices)
    {
        if (root["decorations"] is not JsonArray decorations) return 0;
        if (profile is OutputProfile.Video or OutputProfile.VideoPro)
        {
            var count = decorations.Count;
            decorations.Clear();
            return count;
        }
        var removed = 0;
        for (var index = decorations.Count - 1; index >= 0; index--)
        {
            if (excludedDecorationIndices?.Contains(index) == true)
            {
                decorations.RemoveAt(index);
                removed++;
                continue;
            }
            if (decorations[index] is not JsonObject decoration ||
                decoration["eventType"]?.GetValue<string>() is not ("AddDecoration" or "AddParticle"))
            {
                continue;
            }

            var depth = decoration["depth"]?.GetValue<int>() ?? 0;
            var baked = profile == OutputProfile.VideoVfx || depth >= 0;
            if (!baked || retainedDecorationIndices?.Contains(index) == true) continue;
            decorations.RemoveAt(index);
            removed++;
        }
        return removed;
    }

    private static int RemoveBakedBackgroundEvents(JsonObject root)
    {
        if (root["actions"] is not JsonArray actions) return 0;
        var removed = 0;
        for (var index = actions.Count - 1; index >= 0; index--)
        {
            if (actions[index] is JsonObject action &&
                action["eventType"]?.GetValue<string>() == "CustomBackground")
            {
                actions.RemoveAt(index);
                removed++;
            }
        }
        return removed;
    }

    private static int DisableSelectedFilters(JsonObject root, IReadOnlySet<string>? disabledFilterKeys)
    {
        if (disabledFilterKeys is null || disabledFilterKeys.Count == 0 || root["actions"] is not JsonArray actions)
            return 0;

        var changed = 0;
        foreach (var action in actions.OfType<JsonObject>())
        {
            var key = FilterCatalogReader.CreateKey(action);
            if (key is null || !disabledFilterKeys.Contains(key)) continue;
            switch (action["eventType"]?.GetValue<string>())
            {
                case "SetFilter":
                case "SetFilterAdvanced":
                case "Bloom":
                case "HallOfMirrors":
                    action["enabled"] = false;
                    break;
                case "ScreenTile":
                    action["tile"] = new JsonArray(1, 1);
                    break;
                case "ScreenScroll":
                    action["scroll"] = new JsonArray(0, 0);
                    break;
            }
            changed++;
        }
        return changed;
    }

    private static int DisableBakedFilters(
        JsonObject root,
        OutputProfile profile,
        IReadOnlySet<string>? disabledFilterKeys)
    {
        if (profile is not (OutputProfile.Video or OutputProfile.VideoPro))
            return DisableSelectedFilters(root, disabledFilterKeys);
        if (root["actions"] is not JsonArray actions) return 0;
        var allFilterKeys = actions.OfType<JsonObject>()
            .Select(FilterCatalogReader.CreateKey)
            .Where(static key => key is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        if (disabledFilterKeys != null) allFilterKeys.UnionWith(disabledFilterKeys);
        return DisableSelectedFilters(root, allFilterKeys);
    }

    private static void HideLiveTrackAndPlanets(JsonObject root)
    {
        if (root["settings"] is JsonObject settings)
        {
            settings["trackColor"] = WithZeroAlpha(settings["trackColor"]?.GetValue<string>() ?? "ffffff");
            settings["secondaryTrackColor"] = WithZeroAlpha(
                settings["secondaryTrackColor"]?.GetValue<string>() ?? "ffffff");
        }
        if (root["actions"] is not JsonArray actions) return;
        foreach (var action in actions.OfType<JsonObject>())
        {
            switch (action["eventType"]?.GetValue<string>())
            {
                case "RecolorTrack":
                    if (action["trackColor"] is JsonValue trackColor)
                        action["trackColor"] = WithZeroAlpha(trackColor.GetValue<string>());
                    if (action["secondaryTrackColor"] is JsonValue secondaryTrackColor)
                        action["secondaryTrackColor"] = WithZeroAlpha(secondaryTrackColor.GetValue<string>());
                    break;
                case "MoveTrack":
                    action["opacity"] = 0;
                    break;
                case "ScalePlanets":
                    action["scale"] = 0;
                    break;
            }
        }
    }

    private static string WithZeroAlpha(string color)
    {
        var normalized = color.Trim().TrimStart('#');
        if (normalized.Length < 6) normalized = normalized.PadRight(6, 'f');
        return normalized[..6] + "00";
    }

    private static void WriteTextAtomically(string path, string contents, Encoding encoding)
    {
        var temporaryPath = path + ".writing";
        File.WriteAllText(temporaryPath, contents, encoding);
        try
        {
            if (File.Exists(path))
                File.Replace(temporaryPath, path, null);
            else
                File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
