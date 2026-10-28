using System.Text.Json.Nodes;

namespace ADOFAIVfxRenderer.Core;

public enum FilterOwnership
{
    Realtime,
    BakeIntoVideo,
    BakeAndRealtime,
    Ignore,
}

public sealed record FilterCatalogItem(
    string Key,
    string EventType,
    string DisplayName,
    string Plane,
    string TargetType,
    string TargetTag,
    int FirstFloor,
    int InvocationCount,
    bool OfflineSupported,
    string RiskHint);

public static class FilterCatalogReader
{
    private static readonly HashSet<string> OfflineSupportedEvents = new(StringComparer.Ordinal)
    {
        "SetFilter", "SetFilterAdvanced", "Bloom", "ScreenTile", "ScreenScroll", "HallOfMirrors",
    };

    public static IReadOnlyList<FilterCatalogItem> Read(string chartPath)
    {
        var root = AdoJson.ParseObject(File.ReadAllText(Path.GetFullPath(chartPath)));
        var hasMasking = (root["decorations"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Any(static decoration =>
            {
                var masking = Text(decoration, "maskingType", "None");
                return !masking.Equals("None", StringComparison.OrdinalIgnoreCase) && masking.Length > 0;
            });
        var groups = new Dictionary<string, MutableFilter>(StringComparer.Ordinal);
        foreach (var action in (root["actions"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var eventType = Text(action, "eventType");
            var key = CreateKey(action);
            if (key is null) continue;

            var displayName = eventType switch
            {
                "SetFilter" => Text(action, "filter", "未命名基础滤镜"),
                "SetFilterAdvanced" => FriendlyAdvancedName(Text(action, "filter", "未命名高级滤镜")),
                _ => eventType,
            };
            if (!groups.TryGetValue(key, out var group))
            {
                group = new MutableFilter
                {
                    Key = key,
                    EventType = eventType,
                    DisplayName = displayName,
                    Plane = Text(action, "plane", eventType == "SetFilterAdvanced" ? "Foreground" : "全画面"),
                    TargetType = Text(action, "targetType", "Camera"),
                    TargetTag = Text(action, "targetTag", Text(action, "tag")),
                    FirstFloor = int.MaxValue,
                    OfflineSupported = IsOfflineSupported(action, eventType),
                };
                groups.Add(key, group);
            }
            group.InvocationCount++;
            var active = eventType is "ScreenTile" or "ScreenScroll" ||
                         AdoJson.ReadBoolean(action["enabled"], true);
            group.EverActive |= active;
            if (active) group.FirstFloor = Math.Min(group.FirstFloor, action["floor"]?.GetValue<int>() ?? 0);
        }

        return groups.Values
            .Where(static group => group.EverActive)
            .OrderBy(static group => group.FirstFloor)
            .ThenBy(static group => group.EventType, StringComparer.Ordinal)
            .ThenBy(static group => group.DisplayName, StringComparer.Ordinal)
            .Select(group => new FilterCatalogItem(
                group.Key, group.EventType, group.DisplayName, group.Plane, group.TargetType,
                group.TargetTag, group.FirstFloor, group.InvocationCount, group.OfflineSupported,
                CreateRiskHint(group, hasMasking)))
            .ToArray();
    }

    public static string? CreateKey(JsonObject action)
    {
        var eventType = Text(action, "eventType");
        return eventType switch
        {
            "SetFilter" => $"basic:{Text(action, "filter")}",
            "SetFilterAdvanced" => $"advanced:{Text(action, "plane", "Foreground")}:{Text(action, "filter")}:{Text(action, "targetType", "Camera")}:{Text(action, "targetTag")}",
            "Bloom" => "builtin:Bloom",
            "ScreenTile" => "builtin:ScreenTile",
            "ScreenScroll" => "builtin:ScreenScroll",
            "HallOfMirrors" => "builtin:HallOfMirrors",
            _ => null,
        };
    }

    private static string FriendlyAdvancedName(string value) => value
        .Replace("CameraFilterPack_", "", StringComparison.Ordinal)
        .Replace('_', ' ');

    private static string Text(JsonObject action, string name, string fallback = "") =>
        action[name]?.GetValue<string>() ?? fallback;

    private static bool IsOfflineSupported(JsonObject action, string eventType)
    {
        if (!OfflineSupportedEvents.Contains(eventType)) return false;
        if (eventType != "SetFilterAdvanced") return true;
        return Text(action, "targetType", "Camera").Equals("Camera", StringComparison.OrdinalIgnoreCase) &&
               string.IsNullOrWhiteSpace(Text(action, "targetTag"));
    }

    private static string CreateRiskHint(MutableFilter filter, bool hasMasking)
    {
        var hints = new List<string>();
        if (filter.EventType == "HallOfMirrors")
            hints.Add("历史帧反馈");
        if (filter.EventType == "SetFilterAdvanced")
        {
            if (filter.Plane.Equals("Background", StringComparison.OrdinalIgnoreCase))
                hints.Add("背景层滤镜");
            else if (filter.Plane.Equals("Foreground", StringComparison.OrdinalIgnoreCase))
                hints.Add("前景层滤镜");
            if (!filter.TargetType.Equals("Camera", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(filter.TargetTag))
                hints.Add("局部/标签目标");
            if (hasMasking) hints.Add("谱面含遮罩，需检查合成顺序");
        }
        if (filter.EventType is "ScreenTile" or "ScreenScroll")
            hints.Add("画面坐标变换");
        if (!filter.OfflineSupported) hints.Add("当前选择性 Worker 尚无独立实现");
        return string.Join("；", hints.Distinct(StringComparer.Ordinal));
    }

    private sealed class MutableFilter
    {
        public string Key { get; init; } = "";
        public string EventType { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Plane { get; init; } = "";
        public string TargetType { get; init; } = "";
        public string TargetTag { get; init; } = "";
        public int FirstFloor { get; set; }
        public int InvocationCount { get; set; }
        public bool OfflineSupported { get; init; }
        public bool EverActive { get; set; }
    }
}
