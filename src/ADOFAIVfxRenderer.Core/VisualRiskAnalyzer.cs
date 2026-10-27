using System.Text.Json.Nodes;

namespace ADOFAIVfxRenderer.Core;

public sealed record VisualRiskIssue(string Title, string Detail, int? FirstFloor = null);

public static class VisualRiskAnalyzer
{
    public static IReadOnlyList<VisualRiskIssue> Analyze(string chartPath)
    {
        chartPath = Path.GetFullPath(chartPath);
        var root = AdoJson.ParseObject(File.ReadAllText(chartPath));
        var actions = (root["actions"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var decorations = (root["decorations"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var directory = Path.GetDirectoryName(chartPath)!;
        var result = new List<VisualRiskIssue>();

        var imageReferences = decorations.Concat(actions)
            .Where(static item => item["decorationImage"] is not null)
            .Select(item => new
            {
                Name = item["decorationImage"]?.GetValue<string>() ?? "",
                Floor = item["floor"]?.GetValue<int>() ?? 0,
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.Name) &&
                                  !item.Name.StartsWith("prefab:", StringComparison.OrdinalIgnoreCase))
            .GroupBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderBy(item => item.Floor).First())
            .ToArray();
        var missing = imageReferences
            .Where(item => !File.Exists(Path.Combine(directory, item.Name.Replace('/', Path.DirectorySeparatorChar))))
            .OrderBy(static item => item.Floor)
            .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length > 0)
        {
            var examples = string.Join("、", missing.Take(5).Select(static item => item.Name));
            result.Add(new VisualRiskIssue(
                $"缺少 {missing.Length:N0} 个被引用素材",
                $"这些画面一定无法还原；示例：{examples}" + (missing.Length > 5 ? "……" : ""),
                missing[0].Floor));
        }

        var masks = decorations.Count(static item =>
        {
            var masking = item["maskingType"]?.GetValue<string>() ?? "None";
            return !masking.Equals("None", StringComparison.OrdinalIgnoreCase) && masking.Length > 0;
        });
        var advanced = actions.Where(static item =>
            item["eventType"]?.GetValue<string>() == "SetFilterAdvanced").ToArray();
        var backgroundFilters = advanced.Where(static item =>
            (item["plane"]?.GetValue<string>() ?? "Foreground").Equals("Background", StringComparison.OrdinalIgnoreCase)).ToArray();
        var foregroundFilters = advanced.Where(static item =>
            (item["plane"]?.GetValue<string>() ?? "Foreground").Equals("Foreground", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (masks > 0 && backgroundFilters.Length > 0 && foregroundFilters.Length > 0)
        {
            var firstFloor = backgroundFilters.Concat(foregroundFilters)
                .Min(static item => item["floor"]?.GetValue<int>() ?? 0);
            result.Add(new VisualRiskIssue(
                "遮罩与前景/背景滤镜混合",
                $"谱面含 {masks:N0} 个遮罩装饰、{backgroundFilters.Length:N0} 个背景高级滤镜事件和 {foregroundFilters.Length:N0} 个前景高级滤镜事件；Video VFX 拆层后应优先生成人工对照。",
                firstFloor));
        }

        var targetedFilters = advanced.Where(static item =>
        {
            var targetType = item["targetType"]?.GetValue<string>() ?? "Camera";
            var targetTag = item["targetTag"]?.GetValue<string>() ?? "";
            return !targetType.Equals("Camera", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(targetTag);
        }).ToArray();
        if (targetedFilters.Length > 0)
            result.Add(new VisualRiskIssue(
                "存在局部或标签目标滤镜",
                $"共 {targetedFilters.Length:N0} 个事件只作用于特定对象；不能直接当作全屏滤镜烤制。",
                targetedFilters.Min(static item => item["floor"]?.GetValue<int>() ?? 0)));

        var hall = actions.Where(static item =>
            item["eventType"]?.GetValue<string>() == "HallOfMirrors" &&
            AdoJson.ReadBoolean(item["enabled"], true)).ToArray();
        if (hall.Length > 0)
            result.Add(new VisualRiskIssue(
                "存在 Hall of Mirrors 历史帧反馈",
                $"共 {hall.Length:N0} 个启用事件；需用连续视频片段检查，单帧对照不足。",
                hall.Min(static item => item["floor"]?.GetValue<int>() ?? 0)));

        var addObjects = decorations.Count(static item =>
            item["eventType"]?.GetValue<string>() == "AddObject");
        if (addObjects > 0)
            result.Add(new VisualRiskIssue(
                "存在创建对象",
                $"共 {addObjects:N0} 个 AddObject；Video Pro 可交给原版运行时，Video VFX 拆层时需核对对象归属。"));

        if (decorations.Length >= 1000)
            result.Add(new VisualRiskIssue(
                "高对象数量",
                $"装饰数组含 {decorations.Length:N0} 项；即使贴图按需载入，原版场景对象本身仍会占用内存。"));

        return result;
    }
}
