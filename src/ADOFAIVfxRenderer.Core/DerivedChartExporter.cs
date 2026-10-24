using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        var videoOffset = VideoSynchronizationCalculator.Calculate(root, renderStartSeconds).VideoOffsetMilliseconds;

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
        var root = JsonNode.Parse(File.ReadAllText(sourceChartPath, Encoding.UTF8)) as JsonObject
                   ?? throw new InvalidDataException("谱面根节点不是 JSON 对象。");
        var settings = root["settings"] as JsonObject
                       ?? throw new InvalidDataException("谱面缺少 settings。");
        return 0m;
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
