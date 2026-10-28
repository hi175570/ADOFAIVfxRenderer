using System.Text.Json.Nodes;

namespace ADOFAIVfxRenderer.Core;

public enum VisualRiskKind
{
    MissingAsset,
    MaskComposition,
    SplitFilter,
    TargetedFilter,
    NegativeFilterParameter,
    HistoryFeedback,
    DynamicImageSwap,
    CameraComposition,
    ParticleComposition,
    CreatedObject,
    SourceBackgroundVideo,
    MemoryPressure,
}

public enum VisualRiskSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record VisualRiskIssue(
    string Title,
    string Detail,
    int? FirstFloor = null,
    string Key = "",
    VisualRiskKind Kind = VisualRiskKind.SplitFilter,
    VisualRiskSeverity Severity = VisualRiskSeverity.Warning,
    double? TimeSeconds = null,
    bool RequiresComparison = true,
    bool RequiresContinuousFrames = false,
    bool AffectsVideoPro = false,
    string FilterKey = "",
    int? DecorationIndex = null,
    string SuggestedAction = "生成原版／拆层闭环对照并人工确认。",
    IReadOnlyList<string>? RelatedFilterKeys = null,
    IReadOnlyList<int>? RelatedDecorationIndices = null);

public static class VisualRiskAnalyzer
{
    public static double TimeAtFloor(string chartPath, int floor, double angleOffset = 0d)
    {
        var root = AdoJson.ParseObject(File.ReadAllText(Path.GetFullPath(chartPath)));
        return DecorationCatalogReader.BuildTimeline(root).TimeAtFloorAngleOffset(floor, angleOffset);
    }

    public static IReadOnlyList<VisualRiskIssue> Analyze(string chartPath)
    {
        chartPath = Path.GetFullPath(chartPath);
        var root = AdoJson.ParseObject(File.ReadAllText(chartPath));
        var actions = (root["actions"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var decorations = (root["decorations"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var directory = Path.GetDirectoryName(chartPath)!;
        var timeline = DecorationCatalogReader.BuildTimeline(root);
        var result = new List<VisualRiskIssue>();

        var settings = root["settings"] as JsonObject ?? new JsonObject();
        var backgroundVideo = settings["bgVideo"]?.GetValue<string>() ?? "";
        if (!string.IsNullOrWhiteSpace(backgroundVideo))
        {
            var videoPath = Path.IsPathRooted(backgroundVideo)
                ? backgroundVideo
                : Path.GetFullPath(Path.Combine(directory, backgroundVideo.Replace('/', Path.DirectorySeparatorChar)));
            var exists = File.Exists(videoPath);
            result.Add(new VisualRiskIssue(
                exists ? "源谱包含背景视频" : "源谱背景视频缺失",
                exists
                    ? $"背景视频 {backgroundVideo} 会按 vidOffset、倒计时与循环设置逐帧定位；拆层输出必须确认视频没有错帧或重复叠加。"
                    : $"谱面引用的背景视频 {backgroundVideo} 不存在，任何输出版本都无法还原它。",
                0,
                $"background-video:{backgroundVideo.ToLowerInvariant()}",
                VisualRiskKind.SourceBackgroundVideo,
                exists ? VisualRiskSeverity.Warning : VisualRiskSeverity.Error,
                0d,
                RequiresComparison: exists,
                RequiresContinuousFrames: true,
                AffectsVideoPro: !exists,
                SuggestedAction: exists
                    ? "生成连续闭环对照，确认背景视频时间、画幅和滤镜作用顺序。"
                    : "补齐背景视频后重新载入谱面；若原包本来就缺失，只能明确接受此风险。"));
        }

        double TimeAt(JsonObject item)
        {
            var floor = item["floor"]?.GetValue<int>() ?? 0;
            var angleOffset = item["angleOffset"]?.GetValue<double>() ?? 0d;
            return timeline.TimeAtFloorAngleOffset(floor, angleOffset);
        }

        var references = decorations.Select((item, index) => new { Item = item, Index = index })
            .Concat(actions.Select((item, index) => new { Item = item, Index = -index - 1 }))
            .Where(static entry => entry.Item["decorationImage"] is not null)
            .Select(entry => new
            {
                Name = entry.Item["decorationImage"]?.GetValue<string>() ?? "",
                Floor = entry.Item["floor"]?.GetValue<int>() ?? 0,
                Time = TimeAt(entry.Item),
                DecorationIndex = entry.Index >= 0 ? entry.Index : (int?)null,
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.Name) &&
                                  !item.Name.StartsWith("prefab:", StringComparison.OrdinalIgnoreCase))
            .GroupBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderBy(item => item.Time).First())
            .ToArray();
        foreach (var missing in references
                     .Where(item => !File.Exists(Path.Combine(directory, item.Name.Replace('/', Path.DirectorySeparatorChar))))
                     .OrderBy(static item => item.Time)
                     .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(new VisualRiskIssue(
                "缺少被引用素材",
                $"{missing.Name} 不存在；包含它的画面一定无法还原。",
                missing.Floor,
                $"missing:{missing.Name.ToLowerInvariant()}",
                VisualRiskKind.MissingAsset,
                VisualRiskSeverity.Error,
                missing.Time,
                RequiresComparison: false,
                AffectsVideoPro: true,
                DecorationIndex: missing.DecorationIndex,
                SuggestedAction: "补齐素材后重新载入谱面；若确认原包本来就缺失，可明确接受此风险。"));
        }

        var masks = decorations.Select((item, index) => new { Item = item, Index = index })
            .Where(static entry =>
            {
                var masking = entry.Item["maskingType"]?.GetValue<string>() ?? "None";
                return !masking.Equals("None", StringComparison.OrdinalIgnoreCase) && masking.Length > 0;
            })
            .OrderBy(entry => TimeAt(entry.Item))
            .ToArray();
        foreach (var maskSegment in masks.GroupBy(entry => entry.Item["floor"]?.GetValue<int>() ?? 0))
        {
            var segmentMasks = maskSegment.ToArray();
            var first = segmentMasks[0];
            var floor = maskSegment.Key;
            var maskDependencyIndices = segmentMasks.Select(static item => item.Index).ToHashSet();
            var dependencyTags = segmentMasks
                .SelectMany(static entry =>
                    SplitTags(entry.Item["tag"]?.GetValue<string>())
                        .Concat(SplitTags(entry.Item["maskingTarget"]?.GetValue<string>())))
                .Where(static tag => !tag.Equals("NO TAG", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var mask in segmentMasks.Where(static entry =>
                         entry.Item["useMaskingDepth"]?.GetValue<bool>() == true))
            {
                var front = mask.Item["maskingFrontDepth"]?.GetValue<int>() ?? -1;
                var back = mask.Item["maskingBackDepth"]?.GetValue<int>() ?? -1;
                if (front < 0 || back < 0) continue;
                var minimum = Math.Min(front, back);
                var maximum = Math.Max(front, back);
                foreach (var entry in decorations.Select((decoration, index) => new { decoration, index }))
                {
                    var depth = entry.decoration["depth"]?.GetValue<int>() ?? 0;
                    if (depth >= minimum && depth <= maximum) maskDependencyIndices.Add(entry.index);
                }
            }
            var expanded = true;
            while (expanded)
            {
                expanded = false;
                foreach (var entry in decorations.Select((decoration, index) => new { decoration, index }))
                {
                    var entryTags = SplitTags(entry.decoration["tag"]?.GetValue<string>())
                        .Concat(SplitTags(entry.decoration["maskingTarget"]?.GetValue<string>()))
                        .Where(static tag => !tag.Equals("NO TAG", StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    if (!maskDependencyIndices.Contains(entry.index) && !entryTags.Any(dependencyTags.Contains))
                        continue;
                    if (maskDependencyIndices.Add(entry.index)) expanded = true;
                    foreach (var tag in entryTags)
                        if (dependencyTags.Add(tag)) expanded = true;
                }
            }
            var nearbyFilterKeys = FilterCatalogReader.Read(chartPath)
                .Where(filter => filter.FirstFloor >= floor && filter.FirstFloor <= floor + 1)
                .Select(static filter => filter.Key)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            result.Add(new VisualRiskIssue(
                "遮罩参与拆层合成",
                $"格 {floor} 有 {segmentMasks.Length:N0} 个遮罩相关装饰；分析出的标签组、显式目标和深度范围依赖闭包共有 {maskDependencyIndices.Count:N0} 个元素。",
                floor,
                $"mask-composition:{floor}",
                VisualRiskKind.MaskComposition,
                VisualRiskSeverity.Error,
                TimeAt(first.Item),
                RequiresContinuousFrames: true,
                DecorationIndex: first.Index,
                SuggestedAction: "检查原版与派生谱最终合成；若遮罩必须留在游戏层，把对应装饰设为实时保留。",
                RelatedFilterKeys: nearbyFilterKeys,
                RelatedDecorationIndices: maskDependencyIndices.Order().ToArray()));
        }

        var filterGroups = FilterCatalogReader.Read(chartPath)
            .Where(static item => !string.IsNullOrWhiteSpace(item.RiskHint))
            .ToArray();
        var filterRiskFloors = filterGroups.Select(static filter => filter.FirstFloor).ToHashSet();
        foreach (var segment in filterGroups.GroupBy(static filter => filter.FirstFloor))
        {
            var filters = segment.ToArray();
            var filterKeys = filters.Select(static filter => filter.Key).ToHashSet(StringComparer.Ordinal);
            var action = actions.FirstOrDefault(item =>
                (item["floor"]?.GetValue<int>() ?? 0) == segment.Key &&
                FilterCatalogReader.CreateKey(item) is { } key && filterKeys.Contains(key));
            var time = action is null ? (double?)null : TimeAt(action);
            var targeted = filters.Any(static filter =>
                !filter.TargetType.Equals("Camera", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(filter.TargetTag));
            var hall = filters.Any(static filter => filter.EventType == "HallOfMirrors");
            var unsupported = filters.Any(static filter => !filter.OfflineSupported);
            var kind = targeted ? VisualRiskKind.TargetedFilter :
                hall ? VisualRiskKind.HistoryFeedback : VisualRiskKind.SplitFilter;
            var names = string.Join("、", filters.Select(static filter => filter.DisplayName).Distinct(StringComparer.Ordinal).Take(8));
            if (filters.Select(static filter => filter.DisplayName).Distinct(StringComparer.Ordinal).Count() > 8)
                names += "……";
            var hints = string.Join("；", filters.Select(static filter => filter.RiskHint)
                .Where(static hint => !string.IsNullOrWhiteSpace(hint))
                .Distinct(StringComparer.Ordinal));
            var eventCount = filters.Sum(static filter => filter.InvocationCount);
            var targetTags = filters.SelectMany(static filter => SplitTags(filter.TargetTag))
                .ToHashSet(StringComparer.Ordinal);
            var targetedDecorationIndices = decorations.Select((decoration, index) => new { decoration, index })
                .Where(entry => SplitTags(entry.decoration["tag"]?.GetValue<string>()).Any(targetTags.Contains))
                .Select(static entry => entry.index)
                .ToArray();
            var hasNegativeParameters = actions.Any(item =>
                (item["floor"]?.GetValue<int>() ?? 0) == segment.Key &&
                FilterCatalogReader.CreateKey(item) is { } key && filterKeys.Contains(key) &&
                ContainsNegativeNumber(item));
            result.Add(new VisualRiskIssue(
                targeted ? "局部或标签目标滤镜组合" : hall ? "历史帧反馈滤镜组合" : "前景/背景滤镜组合参与拆层",
                $"同一风险段含 {filters.Length:N0} 组滤镜（{names}），共 {eventCount:N0} 个事件。{hints}" +
                (hasNegativeParameters ? "；含负数参数，必须保留符号语义" : ""),
                segment.Key,
                $"filter-segment:{segment.Key}",
                kind,
                unsupported ? VisualRiskSeverity.Error : VisualRiskSeverity.Warning,
                time,
                RequiresContinuousFrames: hall || filters.Length > 1,
                FilterKey: filters[0].Key,
                SuggestedAction: !unsupported
                    ? "逐个实测适用候选，再由同时间点闭环对照决定处理方式。"
                    : "当前选择性 Worker 没有这组滤镜的独立实现；仍须实测最终合成实时、完整历史预热和可安全共层的候选，不能直接猜结论。",
                RelatedFilterKeys: filters.Select(static filter => filter.Key).ToArray(),
                RelatedDecorationIndices: targetedDecorationIndices));
        }

        var negativeFilters = actions.Select((action, index) => new { Action = action, Index = index })
            .Where(static entry =>
            {
                var eventType = entry.Action["eventType"]?.GetValue<string>() ?? "";
                return eventType is "SetFilter" or "SetFilterAdvanced" or "Bloom" or "ScreenTile" or "ScreenScroll";
            })
            .Where(entry => ContainsNegativeNumber(entry.Action))
            .Where(entry => !filterRiskFloors.Contains(entry.Action["floor"]?.GetValue<int>() ?? 0))
            .GroupBy(static entry => entry.Action["floor"]?.GetValue<int>() ?? 0);
        foreach (var segment in negativeFilters)
        {
            var entries = segment.ToArray();
            var first = entries[0].Action;
            var eventTypes = string.Join("、", entries.Select(static entry =>
                    entry.Action["eventType"]?.GetValue<string>() ?? "")
                .Distinct(StringComparer.Ordinal));
            result.Add(new VisualRiskIssue(
                "滤镜含负数参数",
                $"{eventTypes} 在格 {segment.Key} 有 {entries.Length:N0} 个事件使用负强度或负属性；必须保留原版数值语义，不能夹到零或取绝对值。",
                segment.Key,
                $"negative-filter:{segment.Key}",
                VisualRiskKind.NegativeFilterParameter,
                VisualRiskSeverity.Warning,
                TimeAt(first),
                FilterKey: FilterCatalogReader.CreateKey(first) ?? "",
                SuggestedAction: "生成闭环对照，重点检查方向、反色、缩放与强度符号。",
                RelatedFilterKeys: entries.Select(entry => FilterCatalogReader.CreateKey(entry.Action))
                    .Where(static key => !string.IsNullOrWhiteSpace(key))
                    .Cast<string>()
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()));
        }

        var swaps = actions.Where(static item =>
            item["eventType"]?.GetValue<string>() == "MoveDecorations" &&
            item["decorationImage"] is not null).ToArray();
        if (swaps.Length > 0)
        {
            var first = swaps.OrderBy(TimeAt).First();
            var floor = first["floor"]?.GetValue<int>() ?? 0;
            var swapTags = swaps.SelectMany(static action => SplitTags(action["tag"]?.GetValue<string>()))
                .ToHashSet(StringComparer.Ordinal);
            var relatedDecorations = decorations.Select((decoration, index) => new { decoration, index })
                .Where(entry => SplitTags(entry.decoration["tag"]?.GetValue<string>()).Any(swapTags.Contains))
                .Select(static entry => entry.index)
                .ToArray();
            result.Add(new VisualRiskIssue(
                "运行中更换装饰贴图",
                $"共 {swaps.Length:N0} 个 MoveDecorations 会换图；初始空贴图或稍后换入的素材容易被错误当作已烤入而从两边同时删除。",
                floor,
                $"image-swaps:{floor}",
                VisualRiskKind.DynamicImageSwap,
                VisualRiskSeverity.Warning,
                TimeAt(first),
                RequiresContinuousFrames: true,
                SuggestedAction: "检查换图前后连续画面；缺失时将对应装饰保留实时或修正烘焙归属。",
                RelatedDecorationIndices: relatedDecorations));
        }

        var cameraMoves = actions.Where(static item =>
            item["eventType"]?.GetValue<string>() == "MoveCamera").ToArray();
        if (cameraMoves.Length > 0)
        {
            var first = cameraMoves.OrderBy(TimeAt).First();
            var floor = first["floor"]?.GetValue<int>() ?? 0;
            result.Add(new VisualRiskIssue(
                "视频层与实时层共享运镜",
                $"谱面含 {cameraMoves.Length:N0} 个 MoveCamera；视频层与实时轨道必须使用同一绝对时间、缓动、旋转方向和缩放，否则会出现滑动或转反。",
                floor,
                $"camera-composition:{floor}",
                VisualRiskKind.CameraComposition,
                VisualRiskSeverity.Warning,
                TimeAt(first),
                RequiresContinuousFrames: true,
                SuggestedAction: "生成连续闭环对照，检查固定特征点与轨道是否始终锁定；不一致时优先修正时间轴或改用 Video Pro。"));
        }

        var particles = decorations.Select((item, index) => new { Item = item, Index = index })
            .Where(static entry => entry.Item["eventType"]?.GetValue<string>() == "AddParticle")
            .ToArray();
        if (particles.Length > 0)
        {
            var first = particles.OrderBy(entry => TimeAt(entry.Item)).First();
            var floor = first.Item["floor"]?.GetValue<int>() ?? 0;
            result.Add(new VisualRiskIssue(
                "粒子参与拆层合成",
                $"谱面含 {particles.Length:N0} 个粒子装饰；结束尾帧、遮罩与前后景滤镜可能依赖持续模拟。",
                floor,
                $"particles:{floor}",
                VisualRiskKind.ParticleComposition,
                VisualRiskSeverity.Warning,
                TimeAt(first.Item),
                RequiresContinuousFrames: true,
                SuggestedAction: "检查连续片段和关卡结束后的尾帧，必要时增加结束延长时间。",
                RelatedDecorationIndices: particles.Select(static entry => entry.Index).ToArray()));
        }

        var addObjects = decorations.Where(static item =>
            item["eventType"]?.GetValue<string>() == "AddObject").ToArray();
        if (addObjects.Length > 0)
        {
            var first = addObjects.OrderBy(TimeAt).First();
            var floor = first["floor"]?.GetValue<int>() ?? 0;
            result.Add(new VisualRiskIssue(
                "创建对象参与拆层",
                $"谱面含 {addObjects.Length:N0} 个 AddObject；Video Pro 由原版运行时处理，Video VFX 需确认对象应该进视频还是留在谱面。",
                floor,
                $"add-object:{floor}",
                VisualRiskKind.CreatedObject,
                VisualRiskSeverity.Information,
                TimeAt(first),
                RequiresComparison: false,
                SuggestedAction: "Video VFX 派生谱默认保留 AddObject 实时渲染；只有手动改变归属时才需要额外对照。"));
        }

        if (decorations.Length >= 1000)
        {
            result.Add(new VisualRiskIssue(
                "高对象数量",
                $"装饰数组含 {decorations.Length:N0} 项；贴图可动态回收，但原版场景对象本身仍会占用内存。",
                Key: "high-object-count",
                Kind: VisualRiskKind.MemoryPressure,
                Severity: VisualRiskSeverity.Information,
                RequiresComparison: false,
                SuggestedAction: "选择贴图预算并留意实际进程峰值；这是性能确认项，不是画质对照项。"));
        }

        return result
            .GroupBy(static issue => issue.Key, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderByDescending(static issue => issue.Severity)
            .ThenBy(static issue => issue.TimeSeconds ?? double.MaxValue)
            .ThenBy(static issue => issue.Title, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool ContainsNegativeNumber(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var number)) return number < 0d;
            return false;
        }
        if (node is JsonObject obj)
            return obj.Any(pair => pair.Key is not ("floor" or "angleOffset" or "duration") && ContainsNegativeNumber(pair.Value));
        return node is JsonArray array && array.Any(ContainsNegativeNumber);
    }

    private static IEnumerable<string> SplitTags(string? value) =>
        (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
