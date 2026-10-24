using System.Text.Json.Nodes;
using ADOFAIVfxTimeline;

namespace ADOFAIVfxRenderer.Core;

public sealed record DecorationCatalogItem(
    int Index,
    int Floor,
    int ReferenceFloor,
    int ReferenceOrder,
    double TimeSeconds,
    int Depth,
    string ImageName,
    string ImagePath,
    string Tag,
    string MaskingType,
    bool InitiallyVisible);

public static class DecorationCatalogReader
{
    public static IReadOnlyList<DecorationCatalogItem> Read(string chartPath)
    {
        chartPath = Path.GetFullPath(chartPath);
        var root = JsonNode.Parse(File.ReadAllText(chartPath)) as JsonObject
                   ?? throw new InvalidDataException("谱面根节点不是 JSON 对象。");
        var decorations = root["decorations"] as JsonArray ?? [];
        var actions = (root["actions"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var directory = Path.GetDirectoryName(chartPath)!;
        var timeline = BuildTimeline(root);
        var result = new List<DecorationCatalogItem>();
        for (var index = 0; index < decorations.Count; index++)
        {
            if (decorations[index] is not JsonObject decoration ||
                decoration["eventType"]?.GetValue<string>() != "AddDecoration")
            {
                continue;
            }

            var imageName = decoration["decorationImage"]?.GetValue<string>() ?? "";
            var floor = decoration["floor"]?.GetValue<int>() ?? 0;
            var initiallyVisible = decoration["visible"]?.GetValue<bool>() ?? true;
            var tag = decoration["tag"]?.GetValue<string>() ?? "";
            var reference = FindFirstReference(actions, tag, floor, index, initiallyVisible);
            result.Add(new DecorationCatalogItem(
                index,
                floor,
                reference.Floor,
                reference.Order,
                timeline.TimeAtFloorAngleOffset(reference.Floor, reference.AngleOffset),
                decoration["depth"]?.GetValue<int>() ?? 0,
                imageName,
                string.IsNullOrWhiteSpace(imageName) ? "" : Path.Combine(directory, imageName),
                tag,
                decoration["maskingType"]?.GetValue<string>() ?? "None",
                initiallyVisible));
        }

        return result
            .OrderBy(static item => item.ReferenceFloor)
            .ThenByDescending(static item => item.Depth)
            .ThenBy(static item => item.ReferenceOrder)
            .ThenBy(static item => item.Index)
            .ToArray();
    }

    private static (int Floor, int Order, double AngleOffset) FindFirstReference(
        IReadOnlyList<JsonObject> actions,
        string decorationTags,
        int declarationFloor,
        int decorationIndex,
        bool initiallyVisible)
    {
        if (initiallyVisible || string.IsNullOrWhiteSpace(decorationTags))
            return (declarationFloor, -1, 0d);

        var tags = decorationTags.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        for (var order = 0; order < actions.Count; order++)
        {
            var action = actions[order];
            if (action["eventType"]?.GetValue<string>() != "MoveDecorations") continue;
            var actionTags = (action["tag"]?.GetValue<string>() ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!actionTags.Any(tags.Contains)) continue;

            // A MoveDecorations image swap is a real first reference even when the event omits
            // `visible`; an explicit visible=false event is not an invocation.
            if (action["visible"]?.GetValue<bool>() == false && action["decorationImage"] is null) continue;
            var floor = action["floor"]?.GetValue<int>() ?? declarationFloor;
            return (floor, order, action["angleOffset"]?.GetValue<double>() ?? 0d);
        }

        return (declarationFloor, int.MaxValue - decorationIndex, 0d);
    }

    private static AdoTimelineResult BuildTimeline(JsonObject root)
    {
        var settings = root["settings"] as JsonObject ?? new JsonObject();
        var initialBpm = settings["bpm"]?.GetValue<double>() ?? 100d;
        var pitch = Math.Max(0.01d, (settings["pitch"]?.GetValue<double>() ?? 100d) / 100d);
        var angles = ReadAngles(root);
        var timelineActions = new List<TimelineActionData>();
        var order = 0;
        foreach (var action in (root["actions"] as JsonArray ?? []).OfType<JsonObject>())
        {
            timelineActions.Add(new TimelineActionData
            {
                Order = order++,
                EventType = action["eventType"]?.GetValue<string>() ?? "",
                Floor = action["floor"]?.GetValue<int>() ?? 0,
                AngleOffset = action["angleOffset"]?.GetValue<double>() ?? 0d,
                SpeedType = action["speedType"]?.GetValue<string>() ?? "",
                BeatsPerMinute = action["beatsPerMinute"]?.GetValue<double>() ?? initialBpm,
                BpmMultiplier = action["bpmMultiplier"]?.GetValue<double>() ?? 1d,
                Duration = action["duration"]?.GetValue<double>() ?? 0d,
                Planets = action["planets"]?.GetValue<string>() ?? "",
            });
        }
        return AdoTimelineCalculator.Calculate(angles, timelineActions, initialBpm, pitch);
    }

    private static IReadOnlyList<double> ReadAngles(JsonObject root)
    {
        if (root["angleData"] is JsonArray angleData && angleData.Count > 0)
            return angleData.Select(static token => token?.GetValue<double>() ?? 0d).ToArray();

        var map = new Dictionary<char, double>
        {
            ['p'] = 15d, ['J'] = 30d, ['E'] = 45d, ['T'] = 60d, ['o'] = 75d,
            ['U'] = 90d, ['q'] = 105d, ['G'] = 120d, ['Q'] = 135d, ['H'] = 150d,
            ['W'] = 165d, ['L'] = 180d, ['x'] = 195d, ['N'] = 210d, ['Z'] = 225d,
            ['F'] = 240d, ['V'] = 255d, ['D'] = 270d, ['Y'] = 285d, ['B'] = 300d,
            ['C'] = 315d, ['M'] = 330d, ['A'] = 345d, ['R'] = 360d, ['!'] = 999d,
        };
        var pathData = root["pathData"]?.GetValue<string>() ?? "";
        var result = pathData.Where(map.ContainsKey).Select(symbol => map[symbol]).ToArray();
        if (result.Length == 0) throw new InvalidDataException("谱面没有可读取的 angleData 或 pathData。");
        return result;
    }
}
