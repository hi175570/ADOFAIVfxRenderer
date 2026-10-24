using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;

namespace ADOFAIVfxRenderer.Core;

public sealed record DerivedChartExportResult(
    string ChartPath,
    string VideoRelativePath,
    int VideoOffsetMilliseconds,
    int RemovedDecorations,
    int RemovedBackgroundEvents);

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
        decimal renderStartSeconds = 0m)
    {
        if (profile is not (OutputProfile.VideoVfx or OutputProfile.VideoVfxPro))
        {
            throw new NotSupportedException("派生谱面导出目前只支持 Video VFX 与 Video VFX Pro。");
        }

        sourceChartPath = Path.GetFullPath(sourceChartPath);
        renderedVideoPath = Path.GetFullPath(renderedVideoPath);
        if (!File.Exists(sourceChartPath)) throw new FileNotFoundException("找不到源谱面。", sourceChartPath);
        if (!File.Exists(renderedVideoPath)) throw new FileNotFoundException("找不到已渲染的视频。", renderedVideoPath);

        var root = JsonNode.Parse(File.ReadAllText(sourceChartPath, Encoding.UTF8)) as JsonObject
                   ?? throw new InvalidDataException("谱面根节点不是 JSON 对象。");
        var settings = root["settings"] as JsonObject
                       ?? throw new InvalidDataException("谱面缺少 settings。");

        var sourceDirectory = Path.GetDirectoryName(sourceChartPath)!;
        var relativeVideoPath = Path.GetRelativePath(sourceDirectory, renderedVideoPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        var videoOffset = CalculateVideoOffsetMilliseconds(settings, renderStartSeconds);

        settings["bgVideo"] = relativeVideoPath;
        settings["vidOffset"] = videoOffset;
        settings["loopVideo"] = false;
        settings["showDefaultBGIfNoImage"] = false;

        var removedDecorations = RemoveBakedDecorations(root, profile);
        var removedBackgroundEvents = RemoveBakedBackgroundEvents(root);

        var outputFolderName = Path.GetFileName(Path.GetDirectoryName(renderedVideoPath));
        var sourceName = Path.GetFileNameWithoutExtension(sourceChartPath);
        var videoName = Path.GetFileNameWithoutExtension(renderedVideoPath);
        var outputName = $"{sourceName}-{videoName}-{outputFolderName}.adofai";
        var outputPath = GetUniquePath(Path.Combine(sourceDirectory, outputName));
        File.WriteAllText(outputPath, root.ToJsonString(JsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return new DerivedChartExportResult(
            outputPath,
            relativeVideoPath,
            videoOffset,
            removedDecorations,
            removedBackgroundEvents);
    }

    public static int CalculateVideoOffsetMilliseconds(JsonObject settings, decimal renderStartSeconds = 0m)
    {
        var countdownSeconds = CalculateVideoStartSeconds(settings);
        return checked((int)Math.Round(
            (countdownSeconds - renderStartSeconds) * 1000m,
            MidpointRounding.AwayFromZero));
    }

    public static decimal CalculateRecommendedRenderStartSeconds(string sourceChartPath)
    {
        sourceChartPath = Path.GetFullPath(sourceChartPath);
        var root = JsonNode.Parse(File.ReadAllText(sourceChartPath, Encoding.UTF8)) as JsonObject
                   ?? throw new InvalidDataException("谱面根节点不是 JSON 对象。");
        var settings = root["settings"] as JsonObject
                       ?? throw new InvalidDataException("谱面缺少 settings。");
        return CalculateVideoStartSeconds(settings);
    }

    private static decimal CalculateVideoStartSeconds(JsonObject settings)
    {
        var separateCountdown = settings["separateCountdownTime"]?.GetValue<bool>() ?? false;
        var countdownTicks = settings["countdownTicks"]?.GetValue<int>() ?? 4;
        var bpm = ReadDecimal(settings["bpm"], 100m);
        if (bpm <= 0m) throw new InvalidDataException("settings.bpm 必须大于零。");

        return separateCountdown ? Math.Max(0, countdownTicks) * 60m / bpm : 0m;
    }

    private static decimal ReadDecimal(JsonNode? node, decimal fallback)
    {
        if (node == null) return fallback;
        return decimal.TryParse(
            node.ToString(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : fallback;
    }

    private static int RemoveBakedDecorations(JsonObject root, OutputProfile profile)
    {
        if (root["decorations"] is not JsonArray decorations) return 0;
        var removed = 0;
        for (var index = decorations.Count - 1; index >= 0; index--)
        {
            if (decorations[index] is not JsonObject decoration ||
                decoration["eventType"]?.GetValue<string>() != "AddDecoration")
            {
                continue;
            }

            var depth = decoration["depth"]?.GetValue<int>() ?? 0;
            var baked = profile == OutputProfile.VideoVfx || depth >= 0;
            if (!baked) continue;
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

    private static string GetUniquePath(string preferredPath)
    {
        if (!File.Exists(preferredPath)) return preferredPath;
        var directory = Path.GetDirectoryName(preferredPath)!;
        var name = Path.GetFileNameWithoutExtension(preferredPath);
        var extension = Path.GetExtension(preferredPath);
        for (var suffix = 2; ; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name}-{suffix}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
