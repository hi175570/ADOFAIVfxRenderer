using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ADOFAIVfxRenderer.Core;
using Microsoft.Win32;

namespace ADOFAIVfxRenderer.App;

public partial class MainWindow : Window
{
    private readonly List<CheckBox> _profileChecks = [];
    private readonly List<CheckBox> _resolutionChecks = [];
    private readonly List<CheckBox> _frameRateChecks = [];
    private readonly RenderWorkerClient _worker = new();
    private readonly AppSettings _settings = AppSettingsStore.Load();
    private readonly ObservableCollection<DecorationRetentionItem> _decorationChoices = [];
    private readonly ObservableCollection<FilterOwnershipItem> _filterChoices = [];
    private ICollectionView? _decorationChoicesView;
    private GameInstallation? _installation;
    private ChartMetadata? _chart;
    private bool _updatingTail;
    private bool _tailFramesWereEdited;
    private bool _viewReady;
    private decimal? _durationLimitSeconds;
    private readonly Queue<QueuedRenderTask> _renderQueue = new();
    private readonly ObservableCollection<RenderQueueDisplayItem> _queueItems = [];
    private readonly List<string> _completedBatchOutputs = [];
    private QueuedRenderTask? _activeQueueTask;
    private int _batchTotal;
    private int _batchCompleted;
    private bool _exitWhenQueueFinishes;
    private Exception? _lastQueueError;

    public MainWindow()
    {
        SourceInitialized += ApplyDarkTitleBar;
        InitializeComponent();
        _worker.StatusChanged += Worker_StatusChanged;
        _viewReady = true;
        PopulateOptions();
        PopulateDetectedGames();
        RestoreRememberedPaths();
        RefreshPreRenderCacheStatus();
        RecalculateTailFrames();
        OutputList.ItemsSource = _queueItems;
        Loaded += ApplyCommandLineSmokeTest;
    }

    private void ApplyCommandLineSmokeTest(object sender, RoutedEventArgs e)
    {
        Loaded -= ApplyCommandLineSmokeTest;
        var arguments = Environment.GetCommandLineArgs();
        _exitWhenQueueFinishes = arguments.Any(static argument =>
            argument.Equals("--exit-when-complete", StringComparison.OrdinalIgnoreCase));
        var queueManifest = GetArgumentValue(arguments, "--queue-manifest");
        if (queueManifest is not null)
        {
            ApplyQueueManifest(queueManifest);
            return;
        }
        var chartPath = GetArgumentValue(arguments, "--smoke-chart");
        if (chartPath is null)
        {
            return;
        }

        try
        {
            var gamePath = GetArgumentValue(arguments, "--game-path") ?? GamePathBox.Text;
            GamePathBox.Text = gamePath;
            InspectGame();

            LoadChart(chartPath);

            var fps = int.TryParse(GetArgumentValue(arguments, "--fps"), out var parsedFps) ? parsedFps : 15;
            var resolutionName = GetArgumentValue(arguments, "--resolution") ?? "144p";
            var profileName = GetArgumentValue(arguments, "--profile") ?? nameof(OutputProfile.VideoVfx);
            var smokeProfile = Enum.TryParse<OutputProfile>(profileName, ignoreCase: true, out var parsedProfile)
                ? parsedProfile
                : OutputProfile.VideoVfx;
            foreach (var check in _profileChecks)
            {
                check.IsChecked = (OutputProfile)check.Tag == smokeProfile;
            }

            foreach (var check in _resolutionChecks)
            {
                check.IsChecked = ((VideoResolution)check.Tag).Name.Equals(resolutionName, StringComparison.OrdinalIgnoreCase);
            }

            foreach (var check in _frameRateChecks)
            {
                check.IsChecked = (int)check.Tag == fps;
            }

            TailSecondsBox.Text = GetArgumentValue(arguments, "--tail-seconds") ?? "0";
            _durationLimitSeconds = TryParseNonNegativeDecimal(
                GetArgumentValue(arguments, "--duration-limit") ?? "",
                out var durationLimit)
                ? durationLimit
                : null;
            QualityBox.SelectedIndex = (GetArgumentValue(arguments, "--quality") ?? "")
                .Equals("smaller", StringComparison.OrdinalIgnoreCase)
                ? 2
                : 0;
            var backendName = GetArgumentValue(arguments, "--backend") ?? nameof(RenderBackend.Automatic);
            if (!Enum.TryParse<RenderBackend>(backendName, true, out var smokeBackend))
                throw new InvalidDataException($"未知渲染后端：{backendName}");
            RenderBackendBox.SelectedItem = RenderBackendBox.Items
                .OfType<Choice<RenderBackend>>()
                .First(value => value.Value == smokeBackend);
            if (TryParsePositiveDecimal(GetArgumentValue(arguments, "--texture-budget-gb") ?? "", out var memoryGb))
            {
                MemoryStrategyBox.SelectedItem = MemoryStrategyBox.Items
                    .OfType<Choice<MemoryStrategy>>()
                    .First(static item => item.Value == MemoryStrategy.Limited);
                MemoryLimitGbBox.Text = memoryGb.ToString(CultureInfo.InvariantCulture);
            }
            if (arguments.Any(static argument => argument.Equals("--unlimited-memory", StringComparison.OrdinalIgnoreCase)))
            {
                MemoryStrategyBox.SelectedItem = MemoryStrategyBox.Items
                    .OfType<Choice<MemoryStrategy>>()
                    .First(static item => item.Value == MemoryStrategy.Unlimited);
            }
            var excludedIndices = (GetArgumentValue(arguments, "--exclude-decoration-indices") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static value => int.TryParse(value, out var index) ? index : -1)
                .Where(static index => index >= 0)
                .ToHashSet();
            foreach (var choice in _decorationChoices)
                choice.IsExcluded = excludedIndices.Contains(choice.Item.Index);
            AddToQueue_Click(this, new RoutedEventArgs());
            if (_lastQueueError is not null) throw _lastQueueError;
            StartQueue_Click(this, new RoutedEventArgs());
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            if (_exitWhenQueueFinishes)
            {
                WriteAutomationError(exception);
                Close();
                return;
            }
            DarkDialog.Show(this, exception.Message, "冒烟测试无法启动");
        }
    }

    private void ApplyQueueManifest(string manifestPath)
    {
        try
        {
            manifestPath = Path.GetFullPath(manifestPath);
            var manifest = JsonSerializer.Deserialize<QueueManifest>(
                               File.ReadAllText(manifestPath),
                               new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                           ?? throw new InvalidDataException("队列清单为空。");
            var gamePath = string.IsNullOrWhiteSpace(manifest.GamePath)
                ? GamePathBox.Text
                : manifest.GamePath;
            GamePathBox.Text = gamePath;
            InspectGame();
            if (_installation?.CanRender != true)
                throw new InvalidOperationException("队列清单指定的游戏目录没有通过兼容性检测。");
            if (manifest.Tasks.Length == 0)
                throw new InvalidDataException("队列清单没有任务。");

            foreach (var item in manifest.Tasks)
            {
                LoadChart(item.ChartPath);
                if (!Enum.TryParse<OutputProfile>(item.Profile, true, out var profile))
                    throw new InvalidDataException($"未知输出版本：{item.Profile}");
                var resolution = VideoPresets.Resolutions.FirstOrDefault(value =>
                    value.Name.Equals(item.Resolution, StringComparison.OrdinalIgnoreCase));
                if (resolution == default)
                    throw new InvalidDataException($"未知分辨率：{item.Resolution}");
                if (!VideoPresets.FrameRates.Contains(item.FramesPerSecond))
                    throw new InvalidDataException($"不支持的 FPS：{item.FramesPerSecond}");
                foreach (var check in _profileChecks)
                    check.IsChecked = (OutputProfile)check.Tag == profile;
                foreach (var check in _resolutionChecks)
                    check.IsChecked = ((VideoResolution)check.Tag).Equals(resolution);
                foreach (var check in _frameRateChecks)
                    check.IsChecked = (int)check.Tag == item.FramesPerSecond;
                TailSecondsBox.Text = item.TailSeconds.ToString(CultureInfo.InvariantCulture);
                _durationLimitSeconds = item.DurationLimitSeconds > 0 ? item.DurationLimitSeconds : null;
                QualityBox.SelectedIndex = item.Quality.Equals("smaller", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
                if (!Enum.TryParse<RenderBackend>(item.Backend, true, out var backend))
                    throw new InvalidDataException($"未知渲染后端：{item.Backend}");
                RenderBackendBox.SelectedItem = RenderBackendBox.Items
                    .OfType<Choice<RenderBackend>>()
                    .First(value => value.Value == backend);
                if (item.TextureBudgetGb > 0)
                {
                    MemoryStrategyBox.SelectedItem = MemoryStrategyBox.Items
                        .OfType<Choice<MemoryStrategy>>()
                        .First(static value => value.Value == MemoryStrategy.Limited);
                    MemoryLimitGbBox.Text = item.TextureBudgetGb.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    MemoryStrategyBox.SelectedItem = MemoryStrategyBox.Items
                        .OfType<Choice<MemoryStrategy>>()
                        .First(static value => value.Value == MemoryStrategy.Unlimited);
                }
                AddToQueue_Click(this, new RoutedEventArgs());
                if (_lastQueueError is not null) throw _lastQueueError;
            }
            StartQueue_Click(this, new RoutedEventArgs());
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or InvalidOperationException)
        {
            if (_exitWhenQueueFinishes)
            {
                WriteAutomationError(exception);
                Close();
                return;
            }
            DarkDialog.Show(this, exception.Message, "队列清单无法启动");
        }
    }

    private static string? GetArgumentValue(string[] arguments, string name)
    {
        for (var index = 0; index < arguments.Length - 1; index++)
        {
            if (arguments[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private void ApplyDarkTitleBar(object? sender, EventArgs e)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var enabled = 1;
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }

        // Windows 11 caption colors use COLORREF (0x00BBGGRR).
        var captionColor = 0x001A1411;
        var textColor = 0x00FAF5F2;
        _ = DwmSetWindowAttribute(handle, 35, ref captionColor, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 36, ref textColor, sizeof(int));
    }

    private void PopulateOptions()
    {
        foreach (var definition in OutputProfiles.All.OrderBy(static definition => definition.Profile))
        {
            var check = new CheckBox
            {
                Content = definition.DisplayName,
                Tag = definition.Profile,
                ToolTip = definition.Description,
                IsChecked = definition.Profile == OutputProfile.VideoVfx,
            };
            check.Checked += SelectionChanged;
            check.Unchecked += SelectionChanged;
            _profileChecks.Add(check);
            ProfilesPanel.Children.Add(check);
        }

        foreach (var resolution in VideoPresets.Resolutions)
        {
            var check = new CheckBox
            {
                Content = resolution.Name,
                Tag = resolution,
                ToolTip = $"{resolution.Width} × {resolution.Height}",
                IsChecked = resolution == VideoPresets.P720,
            };
            check.Checked += SelectionChanged;
            check.Unchecked += SelectionChanged;
            _resolutionChecks.Add(check);
            ResolutionsPanel.Children.Add(check);
        }

        foreach (var frameRate in VideoPresets.FrameRates)
        {
            var check = new CheckBox
            {
                Content = frameRate.ToString(CultureInfo.InvariantCulture),
                Tag = frameRate,
                IsChecked = frameRate == 30,
            };
            check.Checked += FrameRateSelectionChanged;
            check.Unchecked += FrameRateSelectionChanged;
            _frameRateChecks.Add(check);
            FrameRatesPanel.Children.Add(check);
        }

        ResolutionStrategyBox.ItemsSource = new[]
        {
            new Choice<ResolutionRenderStrategy>("共享最高分辨率母帧（推荐）", ResolutionRenderStrategy.SharedHighestResolution),
            new Choice<ResolutionRenderStrategy>("每个分辨率原生渲染", ResolutionRenderStrategy.NativePerResolution),
        };
        ResolutionStrategyBox.SelectedIndex = 0;

        RenderBackendBox.ItemsSource = new[]
        {
            new Choice<RenderBackend>("自动（优先硬件编码，不可用时 CPU，推荐）", RenderBackend.Automatic),
            new Choice<RenderBackend>("CPU 兼容（WARP 软件光栅 + CPU 编码）", RenderBackend.CpuCompatibility),
            new Choice<RenderBackend>("普通 GPU（自动硬件 H.264 编码）", RenderBackend.HardwareGpu),
            new Choice<RenderBackend>("NVIDIA / RTX（NVENC 高质量）", RenderBackend.NvidiaRtx),
        };
        RenderBackendBox.SelectedIndex = 0;

        QualityBox.ItemsSource = new[]
        {
            new Choice<VideoQualityPreset>("平衡（推荐）", VideoQualityPreset.Balanced),
            new Choice<VideoQualityPreset>("高质量", VideoQualityPreset.HighQuality),
            new Choice<VideoQualityPreset>("较小文件", VideoQualityPreset.SmallerFile),
            new Choice<VideoQualityPreset>("自定义", VideoQualityPreset.Custom),
        };
        QualityBox.SelectedIndex = 0;
        MemoryStrategyBox.ItemsSource = new[]
        {
            new Choice<MemoryStrategy>("限制贴图预算（推荐）", MemoryStrategy.Limited),
            new Choice<MemoryStrategy>("不限制（原版完整贴图）", MemoryStrategy.Unlimited),
        };
        MemoryStrategyBox.SelectedIndex = _settings.LimitMemory ? 0 : 1;
        MemoryLimitGbBox.Text = Math.Max(1, _settings.MemoryLimitMb / 1024m)
            .ToString("0.###", CultureInfo.InvariantCulture);
        MemoryLimitGbBox.IsEnabled = _settings.LimitMemory;
        RetentionModeBox.ItemsSource = new[]
        {
            new Choice<DecorationRetentionMode>("白名单：勾选要保留的贴图", DecorationRetentionMode.Whitelist),
            new Choice<DecorationRetentionMode>("黑名单：勾选不要保留的贴图", DecorationRetentionMode.Blacklist),
        };
        RetentionModeBox.SelectedIndex = 0;
        RefreshContentChoices();
    }

    private void RestoreRememberedPaths()
    {
        if (Directory.Exists(_settings.GameDirectory))
        {
            GamePathBox.Text = _settings.GameDirectory;
            InspectGame();
        }

        if (File.Exists(_settings.LastChartPath))
        {
            LoadChart(_settings.LastChartPath);
        }
    }

    private void PopulateDetectedGames()
    {
        var candidates = GameInstallationProbe.FindDefaultCandidates();
        DetectedGamesBox.ItemsSource = candidates;
        DetectedGamesBox.IsEnabled = candidates.Count > 0;
        DetectedGamesBox.Text = candidates.Count > 0 ? "选择自动检测结果" : "未自动发现 Steam 安装";
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择包含 A Dance of Fire and Ice.exe 的目录",
            Multiselect = false,
            InitialDirectory = Directory.Exists(GamePathBox.Text) ? GamePathBox.Text : null,
        };

        if (dialog.ShowDialog(this) == true)
        {
            GamePathBox.Text = dialog.FolderName;
            InspectGame();
        }
    }

    private void DetectGame_Click(object sender, RoutedEventArgs e) => InspectGame();

    private void DetectedGamesBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DetectedGamesBox.SelectedItem is string selected)
        {
            GamePathBox.Text = selected;
            InspectGame();
        }
    }

    private void InspectGame()
    {
        _installation = GameInstallationProbe.Inspect(GamePathBox.Text);
        GameStatusText.Text = _installation.Status switch
        {
            GameInstallationStatus.Supported =>
                $"✓ 已支持：{_installation.Adapter!.DisplayName}\n" +
                $"Unity {_installation.UnityVersion} · 高级滤镜 {_installation.Capabilities.CameraFilterComponentCount} 个组件",
            GameInstallationStatus.CompatibleButUnverified =>
                $"⚠ 找到完整游戏，但该程序集版本尚无已验证适配器。\nUnity {_installation.UnityVersion}",
            _ => "✗ " + string.Join("\n", _installation.Problems),
        };
        if (_installation.CanRender && _installation.RootDirectory is not null)
        {
            SaveRememberedPaths(gameDirectory: _installation.RootDirectory);
        }
        UpdateChartCompatibilityText();
    }

    private void BrowseChart_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 ADOFAI 谱面",
            Filter = "ADOFAI 谱面 (*.adofai)|*.adofai|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = GetRememberedChartDirectory(),
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            LoadChart(dialog.FileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _chart = null;
            ChartStatusText.Text = $"✗ {exception.Message}";
        }
    }

    private string? GetRememberedChartDirectory()
    {
        if (Directory.Exists(_settings.LastChartDirectory)) return _settings.LastChartDirectory;
        if (File.Exists(_settings.LastChartPath)) return Path.GetDirectoryName(_settings.LastChartPath);
        return null;
    }

    private void LoadChart(string path)
    {
        path = Path.GetFullPath(path);
        ChartPathBox.Text = path;
        _chart = ChartMetadataReader.Read(path);
        LoadDecorationCatalog(path);
        LoadFilterCatalog(path);
        LoadVisualRisks(path);
        SaveRememberedPaths(chartPath: path);
        UpdateChartCompatibilityText();
    }

    private void SaveRememberedPaths(string? gameDirectory = null, string? chartPath = null)
    {
        var effectiveGame = gameDirectory ?? (_installation?.CanRender == true ? _installation.RootDirectory : _settings.GameDirectory) ?? "";
        var effectiveChart = chartPath ?? (_chart?.Path ?? _settings.LastChartPath);
        var chartDirectory = string.IsNullOrWhiteSpace(effectiveChart)
            ? _settings.LastChartDirectory
            : Path.GetDirectoryName(effectiveChart) ?? "";
        var memoryStrategy = MemoryStrategyBox.SelectedItem is Choice<MemoryStrategy> choice
            ? choice.Value
            : MemoryStrategy.Limited;
        var memoryLimitMb = TryParsePositiveDecimal(MemoryLimitGbBox.Text, out var memoryGb)
            ? checked((int)Math.Round(memoryGb * 1024m, MidpointRounding.AwayFromZero))
            : Math.Max(1024, _settings.MemoryLimitMb);
        AppSettingsStore.Save(new AppSettings(
            effectiveGame,
            effectiveChart,
            chartDirectory,
            memoryStrategy == MemoryStrategy.Limited,
            memoryLimitMb));
    }

    private void UpdateChartCompatibilityText()
    {
        if (_chart is null)
        {
            ChartStatusText.Text = "尚未选择 .adofai。";
            return;
        }

        if (_installation is null)
        {
            ChartStatusText.Text = $"谱面格式版本：{_chart.Version}。选择游戏后检查兼容性。";
            return;
        }

        var problem = ChartMetadataReader.CheckCompatibility(_chart, _installation);
        ChartStatusText.Text = problem is null
            ? $"✓ 谱面格式版本 {_chart.Version}，所选游戏可读取。"
            : $"✗ {problem}";
    }

    private void LoadDecorationCatalog(string chartPath)
    {
        _decorationChoices.Clear();
        foreach (var item in DecorationCatalogReader.Read(chartPath))
        {
            _decorationChoices.Add(new DecorationRetentionItem(item));
        }

        _decorationChoicesView = CollectionViewSource.GetDefaultView(_decorationChoices);
        _decorationChoicesView.Filter = item => item is DecorationRetentionItem choice &&
            (HideBackgroundDecorationsCheckBox.IsChecked != true || choice.Item.Depth <= 0);
        DecorationRetentionList.ItemsSource = _decorationChoicesView;
        DecorationComparisonList.ItemsSource = null;
        DiagnosticComparisonPanel.Visibility = Visibility.Collapsed;
        DecorationLargePreview.Source = null;
        DecorationPreviewTitle.Text = "从左侧选择一张贴图";
        DecorationPreviewDetails.Text = "棋盘格可以同时看清透明、白色、灰色和黑色内容。";
        RefreshRetentionSummary();
    }

    private void LoadFilterCatalog(string chartPath)
    {
        _filterChoices.Clear();
        foreach (var item in FilterCatalogReader.Read(chartPath))
        {
            var choice = new FilterOwnershipItem(item);
            choice.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(FilterOwnershipItem.Ownership)) RefreshFilterOwnershipSummary();
            };
            _filterChoices.Add(choice);
        }
        FilterOwnershipGrid.ItemsSource = _filterChoices;
        RefreshFilterOwnershipSummary();
    }

    private void LoadVisualRisks(string chartPath)
    {
        var issues = VisualRiskAnalyzer.Analyze(chartPath);
        VisualRiskSummaryText.Text = issues.Count == 0
            ? "未发现明显的拆层风险；仍建议用参考视频抽查。"
            : "可能需要人工对照：\n" + string.Join("\n", issues.Select(static issue =>
                $"• {(issue.FirstFloor.HasValue ? $"格 {issue.FirstFloor.Value} · " : "")}{issue.Title}：{issue.Detail}"));
    }

    private IReadOnlySet<string> GetBakedFilterKeys() => _filterChoices
        .Where(static item => item.Ownership == FilterOwnership.BakeIntoVideo)
        .Select(static item => item.Item.Key)
        .ToHashSet(StringComparer.Ordinal);

    private IReadOnlySet<string> GetDisabledFilterKeys() => _filterChoices
        .Where(static item => item.Ownership is FilterOwnership.BakeIntoVideo or FilterOwnership.Ignore)
        .Select(static item => item.Item.Key)
        .ToHashSet(StringComparer.Ordinal);

    private void RefreshFilterOwnershipSummary()
    {
        if (FilterOwnershipSummaryText is null) return;
        var realtime = _filterChoices.Count(static item => item.Ownership == FilterOwnership.Realtime);
        var baked = _filterChoices.Count(static item => item.Ownership == FilterOwnership.BakeIntoVideo);
        var ignored = _filterChoices.Count(static item => item.Ownership == FilterOwnership.Ignore);
        var risky = _filterChoices.Count(static item => !string.IsNullOrWhiteSpace(item.RiskHint));
        var unsupportedBake = _filterChoices.Count(static item =>
            item.Ownership == FilterOwnership.BakeIntoVideo && !item.Item.OfflineSupported);
        FilterOwnershipSummaryText.Text = _chart is null
            ? "尚未载入谱面。"
            : $"共 {_filterChoices.Count:N0} 组 · 可能需人工对照 {risky:N0} 组 · 保留实时 {realtime:N0} · 烤入视频 {baked:N0} · 忽略 {ignored:N0}" +
              (unsupportedBake > 0 ? $" · 有 {unsupportedBake:N0} 组尚无离线实现，开始渲染时会阻止并指出。" : "");
    }

    private void RetentionModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_viewReady) return;
        RefreshRetentionSummary();
    }

    private void RetentionFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_viewReady) return;
        _decorationChoicesView?.Refresh();
    }

    private void RetentionItem_Checked(object sender, RoutedEventArgs e)
    {
        if (!_viewReady) return;
        RefreshRetentionSummary();
    }

    private DecorationRetentionMode CurrentRetentionMode =>
        RetentionModeBox.SelectedItem is Choice<DecorationRetentionMode> choice
            ? choice.Value
            : DecorationRetentionMode.Whitelist;

    private IReadOnlySet<int> GetRetainedDecorationIndices()
    {
        IEnumerable<DecorationRetentionItem> retained = CurrentRetentionMode == DecorationRetentionMode.Whitelist
            ? _decorationChoices.Where(static item => item.IsListed && !item.IsExcluded)
            : _decorationChoices.Where(static item => !item.IsListed && !item.IsExcluded);
        return retained.Select(static item => item.Item.Index).ToHashSet();
    }

    private IReadOnlySet<int> GetExcludedDecorationIndices() => _decorationChoices
        .Where(static item => item.IsExcluded)
        .Select(static item => item.Item.Index)
        .ToHashSet();

    private void RefreshRetentionSummary()
    {
        if (RetentionExplanationText is null || RetentionCountText is null) return;
        var retainedCount = GetRetainedDecorationIndices().Count;
        var excludedCount = GetExcludedDecorationIndices().Count;
        RetentionExplanationText.Text = CurrentRetentionMode == DecorationRetentionMode.Whitelist
            ? "勾选的贴图不烘焙进 Video VFX，而是在派生谱面中继续实时显示。"
            : "勾选的贴图仍烘焙进视频；未勾选的贴图留在派生谱面中实时显示。";
        RetentionCountText.Text = _chart is null
            ? "尚未载入谱面"
            : $"实时保留 {retainedCount:N0} · 完全隐藏 {excludedCount:N0} / {_decorationChoices.Count:N0} 个";
    }

    private void DecorationRetentionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DecorationRetentionList.SelectedItem is not DecorationRetentionItem selected) return;
        DiagnosticComparisonPanel.Visibility = Visibility.Collapsed;
        DecorationLargePreview.Source = selected.FullImage;
        DecorationPreviewTitle.Text = selected.DisplayName;
        DecorationPreviewDetails.Text = selected.FullDetail;
        DecorationDiagnosticStatusText.Text =
            "可从首次调用时刻 +1 ms 开始，每隔 1 秒生成一对，共 20 张；当前只对照离线装饰层。";
    }

    private async void GenerateDecorationComparison_Click(object sender, RoutedEventArgs e)
    {
        if (_installation is null || !_installation.CanRender || _chart is null ||
            DecorationRetentionList.SelectedItem is not DecorationRetentionItem selected)
        {
            DarkDialog.Show(this, "请先选择有效游戏、谱面和一张装饰贴图。", "无法生成对照");
            return;
        }

        GenerateDecorationComparisonButton.IsEnabled = false;
        DecorationComparisonList.ItemsSource = null;
        DiagnosticComparisonPanel.Visibility = Visibility.Collapsed;
        try
        {
            var progress = new Progress<string>(message => DecorationDiagnosticStatusText.Text = message);
            var files = await DecorationDiagnosticClient.GenerateAsync(
                _installation,
                _chart,
                selected.Item,
                DiagnosticTrackCheckBox.IsChecked == true,
                DiagnosticPlanetsCheckBox.IsChecked == true,
                DiagnosticFiltersCheckBox.IsChecked == true,
                progress);
            var pairs = files.Select(static pair => new DecorationComparisonViewItem(pair)).ToArray();
            DecorationComparisonList.ItemsSource = pairs;
            DecorationComparisonList.SelectedIndex = pairs.Length > 0 ? 0 : -1;
            DecorationDiagnosticStatusText.Text = $"完成：{pairs.Length} 组同一时间点对照，共 {pairs.Length * 2} 张。";
            RefreshPreRenderCacheStatus();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DecorationDiagnosticStatusText.Text = "生成失败：" + exception.Message;
            DarkDialog.Show(this, exception.Message, "装饰对照生成失败");
        }
        finally
        {
            GenerateDecorationComparisonButton.IsEnabled = true;
        }
    }

    private void DecorationComparisonList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DecorationComparisonList.SelectedItem is not DecorationComparisonViewItem selected) return;
        DiagnosticWithImage.Source = selected.WithImage;
        DiagnosticWithoutImage.Source = selected.WithoutImage;
        DiagnosticComparisonPanel.Visibility = Visibility.Visible;
        DecorationPreviewTitle.Text = selected.TimestampLabel;
        DecorationPreviewDetails.Text = "左：包含此贴图；右：不包含此贴图。两张来自完全相同的谱面时间点。";
    }

    private void RefreshPreRenderCacheStatus()
    {
        if (PreRenderCacheStatusText is null) return;
        var roots = PreRenderCacheRegistry.Load().Where(Directory.Exists).ToArray();
        var fileCount = roots.Sum(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count());
        PreRenderCacheStatusText.Text = roots.Length == 0
            ? "当前没有已登记的预渲染缓存。"
            : $"已登记 {roots.Length:N0} 组缓存，共 {fileCount:N0} 个文件。";
    }

    private void DeletePreRenderedImages_Click(object sender, RoutedEventArgs e)
    {
        var roots = PreRenderCacheRegistry.Load()
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var deleted = 0;
        foreach (var root in roots)
        {
            var parent = Directory.GetParent(root);
            if (parent is null || !parent.Name.Equals("ADOFAIVfxRenderer Diagnostics", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Directory.Exists(root)) continue;
            Directory.Delete(root, recursive: true);
            deleted++;
        }

        PreRenderCacheRegistry.Clear();
        DecorationComparisonList.ItemsSource = null;
        DiagnosticComparisonPanel.Visibility = Visibility.Collapsed;
        DiagnosticWithImage.Source = null;
        DiagnosticWithoutImage.Source = null;
        PreRenderCacheStatusText.Text = $"已删除 {deleted:N0} 组预渲染缓存；正式输出和源文件未改动。";
    }

    private void SelectionChanged(object sender, RoutedEventArgs e)
    {
        OutputList.ItemsSource = null;
        SummaryText.Text = "选择已改变，请重新生成批量渲染计划。";
        RefreshContentChoices();
    }

    private void UseProfileContent_Click(object sender, RoutedEventArgs e) => RefreshContentChoices();

    private void QualityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_viewReady || QualityBox.SelectedItem is not Choice<VideoQualityPreset> selected)
        {
            return;
        }

        BitrateBox.IsEnabled = selected.Value == VideoQualityPreset.Custom;
    }

    private void MemoryStrategyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_viewReady || MemoryStrategyBox.SelectedItem is not Choice<MemoryStrategy> choice) return;
        MemoryLimitGbBox.IsEnabled = choice.Value == MemoryStrategy.Limited;
        SaveRememberedPaths();
    }

    private void RefreshContentChoices()
    {
        if (!_viewReady)
        {
            return;
        }

        var usePreset = UseProfileContentCheckBox.IsChecked == true;
        ContentChecksPanel.IsEnabled = !usePreset;
        if (!usePreset)
        {
            return;
        }

        var content = _profileChecks
            .Where(static check => check.IsChecked == true)
            .Select(static check => OutputProfiles.Get((OutputProfile)check.Tag).VideoContent)
            .Aggregate(RenderContent.None, static (combined, item) => combined | item);
        BackgroundDecorationsCheckBox.IsChecked = content.HasFlag(RenderContent.BackgroundDecorations);
        ForegroundDecorationsCheckBox.IsChecked = content.HasFlag(RenderContent.ForegroundDecorations);
        TrackCheckBox.IsChecked = content.HasFlag(RenderContent.Track);
        PlanetsCheckBox.IsChecked = content.HasFlag(RenderContent.Planets);
        FiltersCheckBox.IsChecked = content.HasFlag(RenderContent.Filters);
    }

    private RenderContent? GetContentOverride()
    {
        if (UseProfileContentCheckBox.IsChecked == true)
        {
            return null;
        }

        var content = RenderContent.None;
        if (BackgroundDecorationsCheckBox.IsChecked == true) content |= RenderContent.BackgroundDecorations;
        if (ForegroundDecorationsCheckBox.IsChecked == true) content |= RenderContent.ForegroundDecorations;
        if (TrackCheckBox.IsChecked == true) content |= RenderContent.Track;
        if (PlanetsCheckBox.IsChecked == true) content |= RenderContent.Planets;
        if (FiltersCheckBox.IsChecked == true) content |= RenderContent.Filters;
        return content;
    }

    private void FrameRateSelectionChanged(object sender, RoutedEventArgs e)
    {
        SelectionChanged(sender, e);
        if (_tailFramesWereEdited)
        {
            RecalculateTailSeconds();
        }
        else
        {
            RecalculateTailFrames();
        }
    }

    private void TailSecondsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_viewReady || _updatingTail || !TryParseNonNegativeDecimal(TailSecondsBox.Text, out _))
        {
            return;
        }

        _tailFramesWereEdited = false;
        RecalculateTailFrames();
    }

    private void TailFramesBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_viewReady || _updatingTail || !long.TryParse(TailFramesBox.Text, out var frames) || frames < 0)
        {
            return;
        }

        _tailFramesWereEdited = true;
        RecalculateTailSeconds();
    }

    private void TailPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string seconds })
        {
            _tailFramesWereEdited = false;
            TailSecondsBox.Text = seconds;
        }
    }

    private void RecalculateTailFrames()
    {
        if (!TryParseNonNegativeDecimal(TailSecondsBox.Text, out var seconds))
        {
            return;
        }

        var referenceFps = GetReferenceFrameRate();
        _updatingTail = true;
        try
        {
            TailFramesBox.Text = TailExtension.FromSeconds(seconds)
                .GetFrameCount(referenceFps)
                .ToString(CultureInfo.InvariantCulture);
            TailReferenceText.Text = $"帧数按当前最高输出帧率 {referenceFps} FPS 换算；每个输出保持相同秒数。";
        }
        finally
        {
            _updatingTail = false;
        }
    }

    private void RecalculateTailSeconds()
    {
        if (!long.TryParse(TailFramesBox.Text, out var frames) || frames < 0)
        {
            return;
        }

        var referenceFps = GetReferenceFrameRate();
        _updatingTail = true;
        try
        {
            TailSecondsBox.Text = TailExtension.FromFrames(frames, referenceFps)
                .Seconds.ToString("0.######", CultureInfo.InvariantCulture);
            TailReferenceText.Text = $"帧数按当前最高输出帧率 {referenceFps} FPS 换算；每个输出保持相同秒数。";
        }
        finally
        {
            _updatingTail = false;
        }
    }

    private void BuildPlan_Click(object sender, RoutedEventArgs e)
    {
        var profiles = _profileChecks.Where(static check => check.IsChecked == true)
            .Select(static check => (OutputProfile)check.Tag)
            .ToArray();
        var resolutions = _resolutionChecks.Where(static check => check.IsChecked == true)
            .Select(static check => (VideoResolution)check.Tag)
            .ToArray();
        var frameRates = _frameRateChecks.Where(static check => check.IsChecked == true)
            .Select(static check => (int)check.Tag)
            .ToArray();

        if (profiles.Length == 0 || resolutions.Length == 0 || frameRates.Length == 0)
        {
            DarkDialog.Show(this, "输出版本、分辨率和 FPS 都至少选择一个。", "无法生成计划");
            return;
        }

        if (!TryParseNonNegativeDecimal(TailSecondsBox.Text, out var tailSeconds))
        {
            DarkDialog.Show(this, "结束延长秒数无效。", "无法生成计划");
            return;
        }

        var quality = ((Choice<VideoQualityPreset>)QualityBox.SelectedItem).Value;
        int? customBitrate;
        try
        {
            customBitrate = GetCustomBitrate(quality);
        }
        catch (InvalidOperationException exception)
        {
            DarkDialog.Show(this, exception.Message, "无法生成计划");
            return;
        }
        var strategy = ((Choice<ResolutionRenderStrategy>)ResolutionStrategyBox.SelectedItem).Value;
        var variants = RenderBatchPlanner.BuildVariants(profiles, resolutions, frameRates, quality);
        if (customBitrate.HasValue)
        {
            variants = variants.Select(variant => variant with { CustomBitrateKbps = customBitrate }).ToArray();
        }
        var contentOverride = GetContentOverride();
        if (contentOverride.HasValue)
        {
            variants = variants.Select(variant => variant with { ContentOverride = contentOverride }).ToArray();
        }

        // 第一阶段尚未连接原版 Worker 来计算整张谱的自然时长；用一秒窗口精确计算共享时间点密度。
        var oneSecondPlan = RenderBatchPlanner.Create(new RenderBatchRequest(
            1m,
            TailExtension.Zero,
            variants,
            strategy));

        SummaryText.Text =
            $"共 {variants.Count} 个顺序渲染任务 · {oneSecondPlan.Passes.Count} 组相同画面配置 · " +
            $"每秒 {oneSecondPlan.UniqueSceneMoments} 个唯一时间点\n" +
            $"结束后继续 {tailSeconds:0.######} 秒；不同 FPS 自动换算各自尾帧。";
    }

    private void AddToQueue_Click(object sender, RoutedEventArgs e)
    {
        _lastQueueError = null;
        try
        {
            if (_installation is null || !_installation.CanRender)
            {
                throw new InvalidOperationException("请先选择游戏目录并通过兼容性检测。");
            }

            if (_chart is null)
            {
                throw new InvalidOperationException("请先选择要渲染的 .adofai 谱面。");
            }

            var profiles = _profileChecks.Where(static check => check.IsChecked == true)
                .Select(static check => (OutputProfile)check.Tag).ToArray();
            var resolutions = _resolutionChecks.Where(static check => check.IsChecked == true)
                .Select(static check => (VideoResolution)check.Tag).ToArray();
            var frameRates = _frameRateChecks.Where(static check => check.IsChecked == true)
                .Select(static check => (int)check.Tag).ToArray();
            if (profiles.Length == 0 || resolutions.Length == 0 || frameRates.Length == 0)
            {
                throw new InvalidOperationException("输出版本、分辨率和 FPS 都至少选择一个。");
            }

            if (!TryParseNonNegativeDecimal(TailSecondsBox.Text, out var tailSeconds))
            {
                throw new InvalidOperationException("结束延长秒数无效。");
            }

            var quality = ((Choice<VideoQualityPreset>)QualityBox.SelectedItem).Value;
            var customBitrate = GetCustomBitrate(quality);
            var variants = RenderBatchPlanner.BuildVariants(profiles, resolutions, frameRates, quality);
            var contentOverride = GetContentOverride();
            variants = variants.Select(variant => variant with
            {
                ContentOverride = contentOverride,
                CustomBitrateKbps = customBitrate,
            }).ToArray();
            var duplicateProfiles = variants
                .GroupBy(static variant => variant.Profile)
                .Where(static group => group.Count() > 1)
                .Select(static group => OutputProfiles.Get(group.Key).DisplayName)
                .ToArray();
            if (duplicateProfiles.Length > 0)
            {
                throw new InvalidOperationException(
                    "同一谱面的每种输出版本只能保留一个规范视频；请为 " +
                    string.Join("、", duplicateProfiles) +
                    " 各选择一个分辨率和一个 FPS。");
            }

            var activeDuplicate = variants.FirstOrDefault(variant => _queueItems.Any(item =>
                (item.Status is "等待" or "渲染中") &&
                item.Task.Variant.Profile == variant.Profile &&
                Path.GetFullPath(item.Task.Chart.Path).Equals(
                    Path.GetFullPath(_chart.Path),
                    StringComparison.OrdinalIgnoreCase)));
            if (activeDuplicate is not null)
            {
                throw new InvalidOperationException(
                    $"这张谱的 {OutputProfiles.Get(activeDuplicate.Profile).DisplayName} 已在队列中；" +
                    "完成或取消后可以重新加入，程序不会生成带编号的重复视频。");
            }

            var retainedDecorationIndices = GetRetainedDecorationIndices();
            var excludedDecorationIndices = GetExcludedDecorationIndices();
            var bakedFilterKeys = GetBakedFilterKeys();
            var disabledFilterKeys = GetDisabledFilterKeys();
            var unsupportedBake = _filterChoices
                .Where(static item => item.Ownership == FilterOwnership.BakeIntoVideo && !item.Item.OfflineSupported)
                .Select(static item => item.DisplayName)
                .ToArray();
            if (unsupportedBake.Length > 0)
            {
                throw new NotSupportedException("这些滤镜尚不能离线烤入：" + string.Join("、", unsupportedBake));
            }
            var renderBackend = ((Choice<RenderBackend>)RenderBackendBox.SelectedItem).Value;
            var memoryStrategy = ((Choice<MemoryStrategy>)MemoryStrategyBox.SelectedItem).Value;
            var memoryLimitMb = 0;
            if (memoryStrategy == MemoryStrategy.Limited)
            {
                if (!TryParsePositiveDecimal(MemoryLimitGbBox.Text, out var memoryLimitGb) ||
                    memoryLimitGb < 0.5m || memoryLimitGb > 128m)
                    throw new InvalidOperationException("贴图预算必须介于 0.5 GB 与 128 GB。 ");
                memoryLimitMb = checked((int)Math.Round(memoryLimitGb * 1024m, MidpointRounding.AwayFromZero));
            }

            foreach (var variant in variants)
            {
                var task = new QueuedRenderTask(
                    Guid.NewGuid(),
                    _installation,
                    _chart,
                    variant,
                    TailExtension.FromSeconds(tailSeconds),
                    _durationLimitSeconds,
                    retainedDecorationIndices,
                    excludedDecorationIndices,
                    bakedFilterKeys,
                    disabledFilterKeys,
                    renderBackend,
                    memoryLimitMb);
                _renderQueue.Enqueue(task);
                _queueItems.Add(new RenderQueueDisplayItem(task));
            }
            _batchTotal = _queueItems.Count;
            StartRenderButton.IsEnabled = !_worker.IsRunning && _renderQueue.Count > 0;
            SaveRememberedPaths();
            SummaryText.Text =
                $"已加入 {variants.Count} 个任务；队列现有 {_renderQueue.Count} 个等待任务。\n" +
                $"当前谱面：{Path.GetFileName(_chart.Path)}；加入后可切换路径继续添加。";
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            _lastQueueError = exception;
            if (_exitWhenQueueFinishes) return;
            DarkDialog.Show(this, exception.Message, "无法加入队列");
        }
    }

    private void StartQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_worker.IsRunning) return;
        if (_renderQueue.Count == 0)
        {
            DarkDialog.Show(this, "渲染队列中没有等待任务。", "无法开始队列");
            return;
        }
        StartRenderButton.IsEnabled = false;
        CancelRenderButton.IsEnabled = true;
        RenderProgressBar.Value = 0;
        WorkerProgressText.Text = "0 / ? 帧 · 0%";
        WorkerSpeedText.Text = "正在启动";
        StartNextBatchRender();
    }

    private void CancelRender_Click(object sender, RoutedEventArgs e) => _worker.Cancel();

    private void StartNextBatchRender()
    {
        if (_renderQueue.Count == 0)
        {
            StartRenderButton.IsEnabled = false;
            CancelRenderButton.IsEnabled = false;
            SummaryText.Text = $"批量渲染完成：{_batchCompleted}/{_batchTotal}\n" + string.Join("\n", _completedBatchOutputs);
            if (_exitWhenQueueFinishes) _ = Dispatcher.BeginInvoke(Close);
            return;
        }

        var task = _renderQueue.Dequeue();
        _activeQueueTask = task;
        var displayItem = _queueItems.First(item => item.Task.Id == task.Id);
        displayItem.Status = "渲染中";
        var outputPath = _worker.Start(
            task.Installation,
            task.Chart,
            task.Variant,
            task.Tail,
            task.DurationLimitSeconds,
            task.RetainedDecorationIndices,
            task.ExcludedDecorationIndices,
            task.BakedFilterKeys,
            task.DisabledFilterKeys,
            task.RenderBackend,
            task.MemoryLimitMb);
        var current = _queueItems.IndexOf(displayItem) + 1;
        SummaryText.Text = $"队列任务 {current}/{_batchTotal}：{Path.GetFileName(task.Chart.Path)} · {task.Variant.DisplayName}\n" +
                           $"烤入滤镜 {task.BakedFilterKeys.Count:N0} 组；派生谱关闭 {task.DisabledFilterKeys.Count:N0} 组；" +
                           (task.MemoryLimitMb > 0 ? $"贴图预算 {task.MemoryLimitMb / 1024d:0.##} GB。\n" : "贴图预算不限制。\n") +
                           $"输出文件：{outputPath}";
    }

    private async void Worker_StatusChanged(WorkerStatus status)
    {
        WorkerStateText.Text = status.Message;
        RenderProgressBar.Value = Math.Clamp(status.Percent, 0f, 100f);
        WorkerProgressText.Text = $"{status.CurrentFrame:N0} / {status.TotalFrames:N0} 帧 · {status.Percent:0.0}%";
        var memoryText = status.WorkingSetBytes > 0
            ? $" · 内存 {status.WorkingSetBytes / 1073741824d:0.00} GB" +
              (status.MemoryLimitBytes > 0
                  ? $" · 源贴图 {status.EstimatedTextureBytes / 1073741824d:0.00} GB" +
                    $" · 驻留预算 {status.MemoryLimitBytes / 1073741824d:0.##} GB（{status.TextureCount:N0} 张，原尺寸按需载入）"
                  : " · 贴图不限制") +
              $" · 峰值 {status.PeakWorkingSetBytes / 1073741824d:0.00} GB"
            : "";
        WorkerSpeedText.Text = (status.RenderedFramesPerSecond > 0
            ? $"{status.RenderedFramesPerSecond:0.00} 帧/秒 · {status.SecondsPerFrame:0.000} 秒/帧"
            : status.State) + memoryText;

        if (!string.IsNullOrWhiteSpace(status.OutputPath))
        {
            SummaryText.Text = status.State == "failed"
                ? $"渲染失败：{status.Error}\n输出位置：{status.OutputPath}"
                : string.IsNullOrWhiteSpace(status.DerivedChartPath)
                    ? $"输出文件：{status.OutputPath}"
                    : $"视频：{status.OutputPath}\n派生谱面：{status.DerivedChartPath}\n视频偏移：{status.VideoOffsetMilliseconds} ms";
        }

        if (!string.IsNullOrWhiteSpace(status.PreviewPath) && File.Exists(status.PreviewPath))
        {
            try
            {
                using var stream = new FileStream(status.PreviewPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                PreviewImage.Source = image;
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch (IOException)
            {
                // Worker is replacing one of the alternating preview files; the next status will retry.
            }
        }

        if (status.State == "complete")
        {
            if (_activeQueueTask is not null)
                _queueItems.First(item => item.Task.Id == _activeQueueTask.Id).Status = "完成";
            _batchCompleted++;
            _completedBatchOutputs.Add(string.IsNullOrWhiteSpace(status.DerivedChartPath)
                ? status.OutputPath
                : $"{status.OutputPath} | {status.DerivedChartPath} | 偏移 {status.VideoOffsetMilliseconds} ms");
            await _worker.WaitForExitAsync();
            _activeQueueTask = null;
            try
            {
                StartNextBatchRender();
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                StartRenderButton.IsEnabled = _renderQueue.Count > 0;
                CancelRenderButton.IsEnabled = false;
                DarkDialog.Show(this, exception.Message, "无法继续批量渲染");
            }
        }
        else if (status.State is "failed" or "cancelled")
        {
            if (_activeQueueTask is not null)
                _queueItems.First(item => item.Task.Id == _activeQueueTask.Id).Status =
                    status.State == "failed" ? "失败" : "已取消";
            _activeQueueTask = null;
            StartRenderButton.IsEnabled = _renderQueue.Count > 0;
            CancelRenderButton.IsEnabled = false;
            if (_exitWhenQueueFinishes)
            {
                WriteAutomationError(new InvalidOperationException(
                    $"队列任务{(status.State == "failed" ? "失败" : "取消")}：{status.Message}\n{status.Error}"));
                _ = Dispatcher.BeginInvoke(Close);
            }
        }
    }

    private static void WriteAutomationError(Exception exception) => File.WriteAllText(
        Path.Combine(Path.GetTempPath(), "ADOFAIVfxRenderer-automation-error.txt"),
        exception.ToString());

    protected override void OnClosed(EventArgs e)
    {
        SaveRememberedPaths();
        _worker.StatusChanged -= Worker_StatusChanged;
        if (_worker.IsRunning)
        {
            _worker.Cancel();
        }

        _worker.Dispose();
        base.OnClosed(e);
    }

    private int GetReferenceFrameRate() =>
        _frameRateChecks.Where(static check => check.IsChecked == true)
            .Select(static check => (int)check.Tag)
            .DefaultIfEmpty(60)
            .Max();

    private int? GetCustomBitrate(VideoQualityPreset quality)
    {
        if (quality != VideoQualityPreset.Custom)
        {
            return null;
        }

        if (!int.TryParse(BitrateBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var bitrate) || bitrate <= 0)
        {
            throw new InvalidOperationException("自定义码率必须是大于零的 kbps 整数。");
        }

        return bitrate;
    }

    private static bool TryParseNonNegativeDecimal(string text, out decimal value)
    {
        var parsed = decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value) ||
                     decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
        return parsed && value >= 0;
    }

    private static bool TryParsePositiveDecimal(string text, out decimal value) =>
        TryParseNonNegativeDecimal(text, out value) && value > 0;

    private sealed record Choice<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }

    private enum DecorationRetentionMode
    {
        Whitelist,
        Blacklist,
    }

    private enum MemoryStrategy
    {
        Limited,
        Unlimited,
    }

    private sealed record QueueManifest(string GamePath, QueueManifestTask[] Tasks);

    private sealed record QueueManifestTask(
        string ChartPath,
        string Profile,
        string Resolution,
        int FramesPerSecond,
        decimal TailSeconds = 5m,
        decimal DurationLimitSeconds = 0m,
        decimal TextureBudgetGb = 4m,
        string Quality = "balanced",
        string Backend = "Automatic");

    private sealed record QueuedRenderTask(
        Guid Id,
        GameInstallation Installation,
        ChartMetadata Chart,
        RenderVariant Variant,
        TailExtension Tail,
        decimal? DurationLimitSeconds,
        IReadOnlySet<int> RetainedDecorationIndices,
        IReadOnlySet<int> ExcludedDecorationIndices,
        IReadOnlySet<string> BakedFilterKeys,
        IReadOnlySet<string> DisabledFilterKeys,
        RenderBackend RenderBackend,
        int MemoryLimitMb);

    private sealed class RenderQueueDisplayItem : INotifyPropertyChanged
    {
        private string _status = "等待";

        public RenderQueueDisplayItem(QueuedRenderTask task) => Task = task;

        public event PropertyChangedEventHandler? PropertyChanged;
        public QueuedRenderTask Task { get; }
        public string Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
            }
        }

        public string Display =>
            $"{Status} · {Path.GetFileNameWithoutExtension(Task.Chart.Path)} · {Task.Variant.DisplayName}" +
            (Task.MemoryLimitMb > 0 ? $" · 贴图≤{Task.MemoryLimitMb / 1024d:0.##} GB" : " · 贴图不限制");
    }

    private sealed class DecorationRetentionItem : INotifyPropertyChanged
    {
        private bool _isListed;
        private bool _isExcluded;
        private BitmapSource? _thumbnail;
        private BitmapSource? _fullImage;

        public DecorationRetentionItem(DecorationCatalogItem item) => Item = item;

        public event PropertyChangedEventHandler? PropertyChanged;
        public DecorationCatalogItem Item { get; }
        public string DisplayName => string.IsNullOrWhiteSpace(Item.ImageName)
            ? $"无贴图装饰 #{Item.Index}"
            : Path.GetFileName(Item.ImageName);
        public string Detail => $"首次引用格 {Item.ReferenceFloor} · 深度 {Item.Depth} · {Item.TimeSeconds:0.###} 秒";
        public string TagText => string.IsNullOrWhiteSpace(Item.Tag) ? "无标签" : "标签：" + Item.Tag;
        public string FullDetail =>
            $"源格 {Item.Floor} · 首次引用格 {Item.ReferenceFloor} · 深度 {Item.Depth} · " +
            $"首次引用 {Item.TimeSeconds:0.###} 秒 · 遮罩 {Item.MaskingType}\n{Item.ImagePath}";
        public BitmapSource? Thumbnail => _thumbnail ??= LoadBitmap(Item.ImagePath, 160);
        public BitmapSource? FullImage => _fullImage ??= LoadBitmap(Item.ImagePath, 1600);

        public bool IsListed
        {
            get => _isListed;
            set
            {
                if (_isListed == value) return;
                _isListed = value;
                if (value && _isExcluded)
                {
                    _isExcluded = false;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExcluded)));
                }
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsListed)));
            }
        }

        public bool IsExcluded
        {
            get => _isExcluded;
            set
            {
                if (_isExcluded == value) return;
                _isExcluded = value;
                if (value && _isListed)
                {
                    _isListed = false;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsListed)));
                }
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExcluded)));
            }
        }
    }

    private sealed record FilterOwnershipOption(string Label, FilterOwnership Value);

    private sealed class FilterOwnershipItem : INotifyPropertyChanged
    {
        private static readonly FilterOwnershipOption[] Options =
        [
            new("保留实时", FilterOwnership.Realtime),
            new("烤入视频", FilterOwnership.BakeIntoVideo),
            new("忽略/关闭", FilterOwnership.Ignore),
        ];
        private FilterOwnership _ownership = FilterOwnership.Realtime;

        public FilterOwnershipItem(FilterCatalogItem item) => Item = item;
        public event PropertyChangedEventHandler? PropertyChanged;
        public FilterCatalogItem Item { get; }
        public string DisplayName => Item.DisplayName;
        public string EventKind => Item.EventType switch
        {
            "SetFilter" => "基础",
            "SetFilterAdvanced" => "高级",
            _ => "内建",
        };
        public string Plane => Item.Plane;
        public string TargetDescription => string.IsNullOrWhiteSpace(Item.TargetTag)
            ? Item.TargetType
            : $"{Item.TargetType} · {Item.TargetTag}";
        public int FirstFloor => Item.FirstFloor;
        public int InvocationCount => Item.InvocationCount;
        public string RiskHint => Item.RiskHint;
        public string OfflineStatus => Item.OfflineSupported ? "可离线近似；建议对照确认" : "尚未实现，只能实时或忽略";
        public IReadOnlyList<FilterOwnershipOption> OwnershipChoices => Options;
        public FilterOwnership Ownership
        {
            get => _ownership;
            set
            {
                if (_ownership == value) return;
                _ownership = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Ownership)));
            }
        }
    }

    private sealed class DecorationComparisonViewItem
    {
        public DecorationComparisonViewItem(DecorationComparisonFilePair pair)
        {
            TimestampLabel = $"{pair.TimeSeconds:0.###} 秒";
            WithImage = LoadBitmap(pair.WithDecorationPath, 900);
            WithoutImage = LoadBitmap(pair.WithoutDecorationPath, 900);
        }

        public string TimestampLabel { get; }
        public BitmapSource? WithImage { get; }
        public BitmapSource? WithoutImage { get; }
    }

    private static BitmapSource? LoadBitmap(string path, int decodePixelWidth)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodePixelWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            return null;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}
