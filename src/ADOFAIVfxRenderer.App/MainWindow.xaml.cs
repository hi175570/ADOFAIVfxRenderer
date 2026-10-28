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
    private readonly ObservableCollection<VisualRiskReviewItem> _riskReviews = [];
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
    private bool _finalCompositeVerificationMode;
    private bool _suppressRiskInvalidation;
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

    private async void ApplyCommandLineSmokeTest(object sender, RoutedEventArgs e)
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
            var requestedRiskKey = GetArgumentValue(arguments, "--diagnose-risk-key");
            var hasRiskIndex = int.TryParse(GetArgumentValue(arguments, "--diagnose-risk-index"), out var riskIndex);
            if (hasRiskIndex || requestedRiskKey is not null)
            {
                VisualRiskReviewItem risk;
                if (requestedRiskKey is not null)
                {
                    risk = _riskReviews.FirstOrDefault(item =>
                               item.Issue.Key.Equals(requestedRiskKey, StringComparison.Ordinal))
                           ?? throw new InvalidDataException($"找不到风险键：{requestedRiskKey}");
                }
                else
                {
                    if (riskIndex < 0 || riskIndex >= _riskReviews.Count)
                        throw new InvalidDataException($"风险索引 {riskIndex} 超出范围；当前共 {_riskReviews.Count} 项。");
                    risk = _riskReviews[riskIndex];
                }
                RiskReviewGrid.SelectedItem = risk;
                var availableStrategies = BuildRiskStrategies(risk);
                var requestedStrategy = GetArgumentValue(arguments, "--risk-strategy");
                var strategy = requestedStrategy is null
                    ? availableStrategies[0]
                    : availableStrategies.FirstOrDefault(candidate =>
                        candidate.Kind.ToString().Equals(requestedStrategy, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidDataException(
                          $"风险候选 {requestedStrategy} 不适用于当前风险。可用：" +
                          string.Join("、", availableStrategies.Select(static candidate => candidate.Kind)));
                var progress = new Progress<string>(message => RiskDiagnosticStatusText.Text = message);
                var riskDuration = double.TryParse(
                    GetArgumentValue(arguments, "--risk-duration"),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsedRiskDuration)
                    ? parsedRiskDuration
                    : 10d;
                var diagnosticOptions = BuildRiskDiagnosticOptions(strategy) with
                {
                    DurationSeconds = riskDuration,
                };
                var result = await VisualRiskDiagnosticClient.GenerateAsync(
                    _installation ?? throw new InvalidOperationException("游戏安装未载入。"),
                    _chart ?? throw new InvalidOperationException("谱面未载入。"),
                    risk.Issue,
                    diagnosticOptions with
                    {
                        Profile = smokeProfile == OutputProfile.VideoPro
                            ? OutputProfile.VideoVfx
                            : diagnosticOptions.Profile,
                        DiagnosticLabel = strategy.Kind + "-automation",
                    },
                    progress);
                var resultPath = GetArgumentValue(arguments, "--risk-result");
                if (!string.IsNullOrWhiteSpace(resultPath))
                {
                    File.WriteAllText(
                        Path.GetFullPath(resultPath),
                        JsonSerializer.Serialize(new
                        {
                            risk = risk.Issue,
                            strategy = strategy.Kind.ToString(),
                            result.ReferenceVideoPath,
                            result.CandidateVideoPath,
                            result.StructuralSimilarity,
                            frames = result.Frames,
                        }, new JsonSerializerOptions { WriteIndented = true }));
                }
                if (_exitWhenQueueFinishes) Close();
                return;
            }
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

    private void UseVideoVfxDerived_Click(object sender, RoutedEventArgs e)
    {
        if (_chart is null)
        {
            DarkDialog.Show(this, "请先选择生成 Video VFX 时使用的源谱面。", "找不到派生谱");
            return;
        }
        var currentPath = Path.GetFullPath(_chart.Path);
        var derivedPath = Path.GetFileNameWithoutExtension(currentPath)
            .EndsWith(" - Video VFX", StringComparison.OrdinalIgnoreCase)
            ? currentPath
            : RenderOutputLayout.Create(currentPath, OutputProfile.VideoVfx).DerivedChartPath;
        if (!File.Exists(derivedPath))
        {
            DarkDialog.Show(
                this,
                "尚未找到对应的 Video VFX 派生谱：\n" + derivedPath +
                "\n请先完成 Video VFX 渲染。",
                "找不到派生谱");
            return;
        }
        LoadChart(derivedPath);
        foreach (var check in _profileChecks)
            check.IsChecked = (OutputProfile)check.Tag == OutputProfile.VideoPro;
        UseProfileContentCheckBox.IsChecked = true;
        RefreshContentChoices();
        SummaryText.Text =
            "已切换到刚生成的 Video VFX 派生谱，并选择 Video Pro。" +
            "这条输出会用原版场景播放背景视频，再合成谱面中保留的实时轨道、滤镜、遮罩与装饰。";
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
        _finalCompositeVerificationMode = Path.GetFileNameWithoutExtension(path)
            .EndsWith(" - Video VFX", StringComparison.OrdinalIgnoreCase);
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
                if (args.PropertyName != nameof(FilterOwnershipItem.Ownership)) return;
                RefreshFilterOwnershipSummary();
                if (!_suppressRiskInvalidation)
                {
                    RefreshBakedFilterRisks();
                    InvalidateRiskComparisons();
                }
            };
            _filterChoices.Add(choice);
        }
        FilterOwnershipGrid.ItemsSource = _filterChoices;
        RefreshFilterOwnershipSummary();
    }

    private void LoadVisualRisks(string chartPath)
    {
        var issues = VisualRiskAnalyzer.Analyze(chartPath);
        _riskReviews.Clear();
        foreach (var issue in issues)
        {
            var review = new VisualRiskReviewItem(issue);
            review.PropertyChanged += (_, _) => RefreshRiskReviewSummary();
            _riskReviews.Add(review);
        }
        RiskReviewGrid.ItemsSource = _riskReviews;
        RiskComparisonList.ItemsSource = null;
        RiskReferenceImage.Source = null;
        RiskCandidateImage.Source = null;
        RiskReviewGrid.SelectedIndex = _riskReviews.Count > 0 ? 0 : -1;
        RefreshBakedFilterRisks();
    }

    private void RefreshRiskReviewSummary()
    {
        if (RiskReviewSummaryText is null || RiskRenderGateText is null || VisualRiskSummaryText is null) return;
        var pending = _riskReviews.Count(static item => item.Status == VisualRiskReviewStatus.Pending);
        var adjustment = _riskReviews.Count(static item => item.Status == VisualRiskReviewStatus.NeedsAdjustment);
        var normal = _riskReviews.Count(static item => item.Status == VisualRiskReviewStatus.ConfirmedNormal);
        var accepted = _riskReviews.Count(static item => item.Status == VisualRiskReviewStatus.AcceptedRisk);
        RiskReviewSummaryText.Text = _riskReviews.Count == 0
            ? "未发现明显风险"
            : $"待确认 {pending:N0} · 需调整 {adjustment:N0} · 正常 {normal:N0} · 已接受 {accepted:N0}";
        RiskRenderGateText.Text = _riskReviews.Count == 0
            ? "自动分析没有发现明显拆层风险；这不替代最终成片抽查。"
            : pending + adjustment > 0
                ? $"正式拆层渲染暂不可加入队列：还有 {pending:N0} 项待确认、{adjustment:N0} 项需要调整。"
                : "所有自动发现的风险均已确认或明确接受；可以加入正式拆层渲染队列。";
        VisualRiskSummaryText.Text = _riskReviews.Count == 0
            ? "未发现明显拆层风险；仍建议抽查最终成片。"
            : $"已自动定位 {_riskReviews.Count:N0} 个可能出问题的位置；请到“风险确认”逐项生成闭环对照。";
    }

    private void RefreshBakedFilterRisks()
    {
        foreach (var stale in _riskReviews
                     .Where(static item => item.Issue.Key.StartsWith("baked-filter-segment:", StringComparison.Ordinal))
                     .ToArray())
            _riskReviews.Remove(stale);
        if (_chart is null) return;

        var existingFilterKeys = _riskReviews
            .Select(static item => item.Issue.FilterKey)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var segment in _filterChoices
                     .Where(static item => item.Ownership == FilterOwnership.BakeIntoVideo)
                     .Where(item => !existingFilterKeys.Contains(item.Item.Key))
                     .GroupBy(static item => item.Item.FirstFloor))
        {
            var filters = segment.ToArray();
            var names = string.Join("、", filters.Select(static item => item.DisplayName));
            var issue = new VisualRiskIssue(
                "选择烤入的滤镜需要闭环确认",
                $"格 {segment.Key} 的 {names} 将从派生谱实时层关闭，并改由视频层承担；必须确认没有改变遮挡与作用对象。",
                segment.Key,
                $"baked-filter-segment:{segment.Key}",
                VisualRiskKind.SplitFilter,
                VisualRiskSeverity.Warning,
                VisualRiskAnalyzer.TimeAtFloor(_chart.Path, segment.Key),
                RequiresContinuousFrames: true,
                FilterKey: filters[0].Item.Key,
                SuggestedAction: "生成原版／拆层闭环对照；若不一致，把滤镜改回保留实时或调整相关装饰归属。");
            var review = new VisualRiskReviewItem(issue);
            review.PropertyChanged += (_, _) => RefreshRiskReviewSummary();
            _riskReviews.Add(review);
        }
        RefreshRiskReviewSummary();
    }

    private void InvalidateRiskComparisons()
    {
        if (!_viewReady || _suppressRiskInvalidation) return;
        foreach (var review in _riskReviews.Where(static item => item.Issue.RequiresComparison))
            review.InvalidateComparison();
        RiskComparisonList.ItemsSource = null;
        RiskReferenceImage.Source = null;
        RiskCandidateImage.Source = null;
        RefreshRiskReviewSummary();
    }

    private IReadOnlySet<string> GetBakedFilterKeys() => _filterChoices
        .Where(static item => item.Ownership is FilterOwnership.BakeIntoVideo or FilterOwnership.BakeAndRealtime)
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
        var bakedAndRealtime = _filterChoices.Count(static item => item.Ownership == FilterOwnership.BakeAndRealtime);
        var ignored = _filterChoices.Count(static item => item.Ownership == FilterOwnership.Ignore);
        var risky = _filterChoices.Count(static item => !string.IsNullOrWhiteSpace(item.RiskHint));
        var unsupportedBake = _filterChoices.Count(static item =>
            (item.Ownership is FilterOwnership.BakeIntoVideo or FilterOwnership.BakeAndRealtime) &&
            !item.Item.OfflineSupported);
        FilterOwnershipSummaryText.Text = _chart is null
            ? "尚未载入谱面。"
            : $"共 {_filterChoices.Count:N0} 组 · 可能需人工对照 {risky:N0} 组 · 保留实时 {realtime:N0} · 烤入并关闭 {baked:N0} · 烤入且实时 {bakedAndRealtime:N0} · 忽略 {ignored:N0}" +
              (unsupportedBake > 0 ? $" · 有 {unsupportedBake:N0} 组尚无离线实现，开始渲染时会阻止并指出。" : "");
    }

    private void RetentionModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_viewReady) return;
        RefreshRetentionSummary();
        InvalidateRiskComparisons();
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
        InvalidateRiskComparisons();
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

    private void RiskReviewGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RiskReviewGrid.SelectedItem is not VisualRiskReviewItem selected)
        {
            RiskReviewTitleText.Text = "从左侧选择风险项";
            RiskReviewDetailText.Text = "";
            RiskReviewActionText.Text = "";
            return;
        }
        RiskReviewTitleText.Text = selected.Title;
        RiskReviewDetailText.Text = selected.Issue.Detail;
        RiskReviewActionText.Text = "分析线索（未经实测不能当结论）：" + selected.Issue.SuggestedAction;
        var strategies = BuildRiskStrategies(selected);
        RiskStrategyBox.ItemsSource = strategies;
        RiskStrategyBox.SelectedItem = strategies.FirstOrDefault(item => item.Kind == selected.ActiveStrategy) ?? strategies[0];
        ShowActiveRiskDiagnostic(selected);
    }

    private async void GenerateRiskComparison_Click(object sender, RoutedEventArgs e)
    {
        if (_installation?.CanRender != true || _chart is null ||
            RiskReviewGrid.SelectedItem is not VisualRiskReviewItem selected)
        {
            DarkDialog.Show(this, "请先选择有效游戏、谱面和一个风险项。", "无法生成风险对照");
            return;
        }

        if (RiskStrategyBox.SelectedItem is not VisualRiskStrategyOption strategy)
            return;
        if (strategy.Kind == VisualRiskStrategyKind.VideoProFallback)
        {
            DarkDialog.Show(this, "Video Pro 本身就是左侧原版参考，不需要重复生成拆层候选；点击“应用此方案”即可切换。", "无需对照");
            return;
        }
        var diagnosticOptions = BuildRiskDiagnosticOptions(strategy);
        var unsupportedBake = _filterChoices
            .Where(item => diagnosticOptions.BakedFilterKeys.Contains(item.Item.Key) && !item.Item.OfflineSupported)
            .Select(static item => item.DisplayName)
            .ToArray();
        if (unsupportedBake.Length > 0)
        {
            DarkDialog.Show(
                this,
                "这些滤镜不能离线烤入，请先改为保留实时或忽略：" + string.Join("、", unsupportedBake),
                "风险对照配置无效");
            return;
        }

        GenerateRiskComparisonButton.IsEnabled = false;
        RiskDiagnosticStatusText.Text = "正在准备风险闭环对照……";
        try
        {
            var progress = new Progress<string>(message => RiskDiagnosticStatusText.Text = message);
            var result = await VisualRiskDiagnosticClient.GenerateAsync(
                _installation,
                _chart,
                selected.Issue,
                diagnosticOptions,
                progress);
            selected.SetDiagnostic(strategy.Kind, result);
            ShowActiveRiskDiagnostic(selected);
            RefreshPreRenderCacheStatus();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            RiskDiagnosticStatusText.Text = "生成失败：" + exception.Message;
            DarkDialog.Show(this, exception.Message, "风险闭环对照生成失败");
        }
        finally
        {
            GenerateRiskComparisonButton.IsEnabled = selected.Issue.RequiresComparison &&
                                                     selected.Issue.TimeSeconds.HasValue &&
                                                     strategy.Kind != VisualRiskStrategyKind.VideoProFallback;
        }
    }

    private async void TestAllRiskCandidates_Click(object sender, RoutedEventArgs e)
    {
        if (_installation?.CanRender != true || _chart is null ||
            RiskReviewGrid.SelectedItem is not VisualRiskReviewItem selected)
        {
            DarkDialog.Show(this, "请先选择有效游戏、谱面和一个风险项。", "无法实测候选");
            return;
        }

        var candidates = BuildRiskStrategies(selected)
            .Where(static strategy => strategy.Kind != VisualRiskStrategyKind.VideoProFallback)
            .ToArray();
        if (candidates.Length == 0) return;
        GenerateRiskComparisonButton.IsEnabled = false;
        TestAllRiskCandidatesButton.IsEnabled = false;
        var succeeded = new List<(VisualRiskStrategyOption Strategy, double? Similarity)>();
        var failed = new List<string>();
        try
        {
            for (var index = 0; index < candidates.Length; index++)
            {
                var strategy = candidates[index];
                var diagnosticOptions = BuildRiskDiagnosticOptions(strategy);
                var unsupportedBake = _filterChoices
                    .Where(item => diagnosticOptions.BakedFilterKeys.Contains(item.Item.Key) && !item.Item.OfflineSupported)
                    .Select(static item => item.DisplayName)
                    .ToArray();
                if (unsupportedBake.Length > 0)
                {
                    var reason = "离线实现不存在：" + string.Join("、", unsupportedBake);
                    selected.SetDiagnosticFailure(strategy.Kind, reason);
                    failed.Add(strategy.Label + "（" + reason + "）");
                    continue;
                }

                RiskDiagnosticStatusText.Text =
                    $"正在实测候选 {index + 1:N0}/{candidates.Length:N0}：{strategy.Label}。每个候选都使用同一风险时间点与原版最终画面对照。";
                try
                {
                    var progress = new Progress<string>(message =>
                        RiskDiagnosticStatusText.Text = $"候选 {index + 1:N0}/{candidates.Length:N0} · {strategy.Label}：{message}");
                    var result = await VisualRiskDiagnosticClient.GenerateAsync(
                        _installation,
                        _chart,
                        selected.Issue,
                        diagnosticOptions,
                        progress);
                    selected.SetDiagnostic(strategy.Kind, result);
                    succeeded.Add((strategy, result.StructuralSimilarity));
                    RefreshPreRenderCacheStatus();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    selected.SetDiagnosticFailure(strategy.Kind, exception.Message);
                    failed.Add(strategy.Label + "（" + exception.Message + "）");
                }
            }

            var best = succeeded
                .Where(static result => result.Similarity.HasValue)
                .OrderByDescending(static result => result.Similarity)
                .FirstOrDefault();
            var strategies = BuildRiskStrategies(selected);
            RiskStrategyBox.ItemsSource = strategies;
            if (best.Strategy is not null)
            {
                selected.SelectStrategy(best.Strategy.Kind);
                RiskStrategyBox.SelectedItem = strategies.First(item => item.Kind == best.Strategy.Kind);
                ShowActiveRiskDiagnostic(selected);
            }
            RiskDiagnosticStatusText.Text =
                $"已实际渲染 {succeeded.Count:N0} 个候选，失败 {failed.Count:N0} 个。" +
                (best.Strategy is null
                    ? "没有候选得到可比较结果。"
                    : $"已切到数值差异最小的“{best.Strategy.Label}”（SSIM {best.Similarity:0.000000}）；这只是排序，仍必须逐帧人工确认，程序不会自动宣布解决。") +
                (failed.Count == 0 ? "" : " 失败项可在下拉框逐个查看原因。");
        }
        finally
        {
            TestAllRiskCandidatesButton.IsEnabled = true;
            GenerateRiskComparisonButton.IsEnabled = selected.Issue.RequiresComparison &&
                                                     selected.Issue.TimeSeconds.HasValue &&
                                                     selected.ActiveStrategy != VisualRiskStrategyKind.VideoProFallback;
        }
    }

    private void RiskStrategyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RiskReviewGrid.SelectedItem is not VisualRiskReviewItem review ||
            RiskStrategyBox.SelectedItem is not VisualRiskStrategyOption strategy)
            return;
        review.SelectStrategy(strategy.Kind);
        RiskStrategyDescriptionText.Text = strategy.Description;
        ShowActiveRiskDiagnostic(review);
    }

    private void ApplyRiskStrategy_Click(object sender, RoutedEventArgs e)
    {
        if (RiskReviewGrid.SelectedItem is not VisualRiskReviewItem review ||
            RiskStrategyBox.SelectedItem is not VisualRiskStrategyOption strategy)
            return;
        if (review.Issue.RequiresComparison &&
            strategy.Kind != VisualRiskStrategyKind.VideoProFallback &&
            !review.HasComparison)
        {
            DarkDialog.Show(
                this,
                "这个候选还没有在当前风险的实际时间点完成原版／拆层闭环对照，不能把猜测当作解决方案。请先生成对照，或点击“实测全部候选”。",
                "候选尚未实测");
            return;
        }
        _suppressRiskInvalidation = true;
        try
        {
            var filterKeys = review.Issue.RelatedFilterKeys ??
                             (string.IsNullOrWhiteSpace(review.Issue.FilterKey) ? [] : [review.Issue.FilterKey]);
            if (strategy.Kind is VisualRiskStrategyKind.FinalCompositeRealtime or
                VisualRiskStrategyKind.BakeBackgroundFilterChain or
                VisualRiskStrategyKind.BakeTargetedFilterWithTargets or
                VisualRiskStrategyKind.DisableFilterCompromise or
                VisualRiskStrategyKind.RetainTargetedFilterWithTargets)
            {
                var ownership = strategy.Kind switch
                {
                    VisualRiskStrategyKind.BakeBackgroundFilterChain or
                    VisualRiskStrategyKind.BakeTargetedFilterWithTargets => FilterOwnership.BakeIntoVideo,
                    VisualRiskStrategyKind.DisableFilterCompromise => FilterOwnership.Ignore,
                    _ => FilterOwnership.Realtime,
                };
                foreach (var item in _filterChoices.Where(item => filterKeys.Contains(item.Item.Key, StringComparer.Ordinal)))
                    item.Ownership = ownership;
            }
            if (strategy.Kind == VisualRiskStrategyKind.BakeBackgroundHistoryComposite)
            {
                foreach (var item in _filterChoices.Where(item => filterKeys.Contains(item.Item.Key, StringComparer.Ordinal)))
                    item.Ownership = item.Item.EventType == "HallOfMirrors" ||
                                     item.Item.Plane.Equals("Background", StringComparison.OrdinalIgnoreCase)
                        ? FilterOwnership.BakeIntoVideo
                        : FilterOwnership.Realtime;
            }
            else if (strategy.Kind == VisualRiskStrategyKind.BakeBackgroundAndGlobalKeepRealtimeFilters)
            {
                foreach (var item in _filterChoices.Where(item => filterKeys.Contains(item.Item.Key, StringComparer.Ordinal)))
                {
                    item.Ownership = item.Item.Plane.Equals("Background", StringComparison.OrdinalIgnoreCase)
                        ? FilterOwnership.BakeIntoVideo
                        : item.Item.Plane.Equals("Foreground", StringComparison.OrdinalIgnoreCase)
                            ? FilterOwnership.Realtime
                            : FilterOwnership.BakeAndRealtime;
                }
            }
            else if (strategy.Kind is VisualRiskStrategyKind.BakeBackgroundPlaneKeepForegroundRealtime or
                     VisualRiskStrategyKind.BakeForegroundPlaneKeepBackgroundRealtime)
            {
                var bakePlane = strategy.Kind == VisualRiskStrategyKind.BakeBackgroundPlaneKeepForegroundRealtime
                    ? "Background"
                    : "Foreground";
                foreach (var item in _filterChoices.Where(item => filterKeys.Contains(item.Item.Key, StringComparer.Ordinal)))
                    item.Ownership = item.Item.Plane.Equals(bakePlane, StringComparison.OrdinalIgnoreCase)
                        ? FilterOwnership.BakeIntoVideo
                        : FilterOwnership.Realtime;
            }
            var retainDependencies = strategy.Kind is
                VisualRiskStrategyKind.RetainTargetedFilterWithTargets or
                VisualRiskStrategyKind.RetainMaskDependencyClosure or
                VisualRiskStrategyKind.RetainAllMaskDependencyClosures or
                VisualRiskStrategyKind.RetainDynamicImageSequence or
                VisualRiskStrategyKind.RetainParticleSystems;
            var bakeDependencies = strategy.Kind is
                VisualRiskStrategyKind.BakeTargetedFilterWithTargets or
                VisualRiskStrategyKind.BakeMaskDependencyClosure or
                VisualRiskStrategyKind.BakeDynamicImageSequence or
                VisualRiskStrategyKind.BakeParticleSystems;
            if (retainDependencies || bakeDependencies)
            {
                var indices = strategy.Kind == VisualRiskStrategyKind.RetainAllMaskDependencyClosures
                    ? _riskReviews
                        .Where(static item => item.Issue.Kind == VisualRiskKind.MaskComposition)
                        .SelectMany(static item => item.Issue.RelatedDecorationIndices ?? [])
                        .ToHashSet()
                    : (review.Issue.RelatedDecorationIndices ?? []).ToHashSet();
                var whitelist = CurrentRetentionMode == DecorationRetentionMode.Whitelist;
                foreach (var item in _decorationChoices.Where(item => indices.Contains(item.Item.Index)))
                {
                    item.IsExcluded = false;
                    item.IsListed = retainDependencies ? whitelist : !whitelist;
                }
            }
            if (retainDependencies)
            {
                foreach (var item in _filterChoices.Where(item => filterKeys.Contains(item.Item.Key, StringComparer.Ordinal)))
                    item.Ownership = FilterOwnership.Realtime;
            }
            if (strategy.Kind == VisualRiskStrategyKind.ExtendParticleTail &&
                (!TryParseNonNegativeDecimal(TailSecondsBox.Text, out var currentTail) || currentTail < 10m))
                TailSecondsBox.Text = "10";
            if (strategy.Kind is VisualRiskStrategyKind.VideoProFallback or
                VisualRiskStrategyKind.SwitchToVideo or VisualRiskStrategyKind.SwitchToVideoVfxPro)
            {
                var selectedProfile = strategy.Kind switch
                {
                    VisualRiskStrategyKind.SwitchToVideo => OutputProfile.Video,
                    VisualRiskStrategyKind.SwitchToVideoVfxPro => OutputProfile.VideoVfxPro,
                    _ => OutputProfile.VideoPro,
                };
                foreach (var check in _profileChecks)
                    check.IsChecked = (OutputProfile)check.Tag == selectedProfile;
                UseProfileContentCheckBox.IsChecked = true;
                RefreshContentChoices();
            }
        }
        finally
        {
            _suppressRiskInvalidation = false;
        }
        review.Status = VisualRiskReviewStatus.Pending;
        RefreshFilterOwnershipSummary();
        RefreshBakedFilterRisks();
        RefreshRetentionSummary();
        review.MarkStrategyApplied(strategy.Kind);
        RiskDiagnosticStatusText.Text = "已应用经选择的候选：“" + strategy.Label + "”。请检查它在此风险位置生成的对应对照，再确认正常。";
    }

    private IReadOnlyList<VisualRiskStrategyOption> BuildRiskStrategies(VisualRiskReviewItem review)
    {
        var strategies = new List<VisualRiskStrategyOption>
        {
            new(VisualRiskStrategyKind.Current, "当前配置（基线候选）", "先实际渲染当前拆层结果，确认问题是否在这个时间点真实出现。"),
        };
        var filterKeys = review.Issue.RelatedFilterKeys ??
                         (string.IsNullOrWhiteSpace(review.Issue.FilterKey) ? [] : [review.Issue.FilterKey]);
        var decorationCount = review.Issue.RelatedDecorationIndices?.Count ?? 0;
        var matchingFilters = _filterChoices.Where(item => filterKeys.Contains(item.Item.Key, StringComparer.Ordinal)).ToArray();
        var canBakeFilters = filterKeys.Count > 0 && filterKeys.All(key => matchingFilters.Any(item =>
            item.Item.Key.Equals(key, StringComparison.Ordinal) && item.Item.OfflineSupported));
        var onlyBackgroundFilters = matchingFilters.Length > 0 && matchingFilters.All(static item =>
            item.Item.Plane.Equals("Background", StringComparison.OrdinalIgnoreCase));
        var hasBackgroundFilters = matchingFilters.Any(static item =>
            item.Item.Plane.Equals("Background", StringComparison.OrdinalIgnoreCase));
        var hasForegroundFilters = matchingFilters.Any(static item =>
            item.Item.Plane.Equals("Foreground", StringComparison.OrdinalIgnoreCase));
        var canBakeBackgroundPlane = hasBackgroundFilters && matchingFilters
            .Where(static item => item.Item.Plane.Equals("Background", StringComparison.OrdinalIgnoreCase))
            .All(static item => item.Item.OfflineSupported);

        switch (review.Issue.Kind)
        {
            case VisualRiskKind.MaskComposition:
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.BakeMaskDependencyClosure,
                    "候选：遮罩依赖闭包一起烤入",
                    $"把分析出的 {decorationCount:N0} 个遮罩依赖放在同一视频层，避免只删掉其中一边。"));
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.RetainMaskDependencyClosure,
                    "候选：遮罩依赖闭包一起实时",
                    $"把分析出的 {decorationCount:N0} 个遮罩依赖全部留在派生谱，让游戏维持原本深度与遮罩缓存。"));
                var allMaskDependencyCount = _riskReviews
                    .Where(static item => item.Issue.Kind == VisualRiskKind.MaskComposition)
                    .SelectMany(static item => item.Issue.RelatedDecorationIndices ?? [])
                    .Distinct()
                    .Count();
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.RetainAllMaskDependencyClosures,
                    "候选：全谱遮罩依赖闭包全部实时",
                    $"把所有遮罩风险段合并后的 {allMaskDependencyCount:N0} 个依赖元素留给游戏，保留全局排序和共享遮罩缓存；普通非遮罩装饰仍可烤入。"));
                if (canBakeFilters && hasBackgroundFilters && hasForegroundFilters)
                {
                    strategies.Add(new VisualRiskStrategyOption(
                        VisualRiskStrategyKind.BakeBackgroundFilterChain,
                        "候选：整组滤镜链与背景一起烤入并实时关闭",
                        "把同一风险段的背景、前景和全画面滤镜按原顺序全部处理视频层，并在派生谱关闭重复事件；轨道不再获得这组滤镜，必须用实际画面对照。"));
                    strategies.Add(new VisualRiskStrategyOption(
                        VisualRiskStrategyKind.BakeBackgroundPlaneKeepForegroundRealtime,
                        "候选：背景滤镜烤入，前景滤镜实时",
                        "把同一风险段的背景平面滤镜随背景装饰烤入；前景平面滤镜仍在最终合成处理轨道和实时前景。"));
                    strategies.Add(new VisualRiskStrategyOption(
                        VisualRiskStrategyKind.BakeBackgroundAndGlobalKeepRealtimeFilters,
                        "候选：背景与全画面滤镜烤入，全画面滤镜同时实时",
                        "背景平面滤镜与全画面滤镜先处理视频；全画面滤镜仍在派生谱处理轨道。用于原版滤镜能处理场景层、却处理不到静态 bgVideo 的情况。"));
                    strategies.Add(new VisualRiskStrategyOption(
                        VisualRiskStrategyKind.BakeForegroundPlaneKeepBackgroundRealtime,
                        "反向对照：前景滤镜烤入，背景滤镜实时",
                        "用于验证前后景归属方向；只有实际闭环更接近原版时才可采用。"));
                }
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.SwitchToVideoVfxPro,
                    "候选：改用 Video VFX Pro 分开前景",
                    "只烤深度大于等于零的背景装饰，把前景遮罩与遮挡留给游戏；适合跨轨道前景关系。"));
                break;
            case VisualRiskKind.HistoryFeedback:
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.BakeBackgroundFilterChain,
                    "候选：全部装饰层、前后景滤镜与 Hall 一起烤入",
                    "Video VFX 的全部装饰、粒子、Background/Foreground 滤镜和历史反馈在压平前共同计算；派生谱关闭这组滤镜，只保留实时轨道与星球。"));
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.BakeBackgroundHistoryComposite,
                    "候选：背景层与 Hall 历史反馈一起离线烤入",
                    "从正式视频起点保留历史颜色缓冲，让背景装饰、Background 滤镜和 Hall 在压平前共同计算；Foreground 滤镜与实时轨道不烤入。"));
                if (canBakeBackgroundPlane)
                    strategies.Add(new VisualRiskStrategyOption(
                        VisualRiskStrategyKind.BakeBackgroundPlaneKeepForegroundRealtime,
                        "候选：背景滤镜烤入，Hall 与前景滤镜实时",
                        "先让视频层经过同段 Background 滤镜链；Hall of Mirrors 和 Foreground 滤镜仍在最终合成处理视频、轨道、粒子与实时前景。"));
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.FinalCompositeRealtime,
                    "候选：反馈滤镜作用于最终合成",
                    "先播放视频并合成轨道、粒子和实时前景，再由游戏执行历史帧反馈；必须用实际风险片段验证。"));
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.PrewarmHistory,
                    "候选：从更早时间预热反馈缓冲",
                    "诊断时提前 10 秒开始计算历史帧，排除因为直接跳到风险格导致反馈缓冲为空。正式完整渲染会从关卡开头自然预热。"));
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.DisableFilterCompromise,
                    "诊断候选：关闭反馈滤镜（妥协）",
                    "只用来证明爆炸是否由反馈链造成；会失去主效果，不能默认采用。"));
                break;
            case VisualRiskKind.TargetedFilter:
                if (canBakeFilters)
                    strategies.Add(new VisualRiskStrategyOption(
                        VisualRiskStrategyKind.BakeTargetedFilterWithTargets,
                        "候选：目标滤镜与目标装饰一起烤入",
                        $"把滤镜和匹配目标标签的 {decorationCount:N0} 个装饰放在同一视频层，保留局部作用域。"));
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.RetainTargetedFilterWithTargets,
                    "候选：目标滤镜与目标装饰一起实时",
                    $"把滤镜和匹配目标标签的 {decorationCount:N0} 个装饰都留给游戏，避免滤镜失去目标。"));
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.SwitchToVideoVfxPro,
                    "候选：改用 Video VFX Pro 保留前景目标",
                    "背景装饰进入视频，深度小于零的目标装饰与局部滤镜留给游戏，减少跨层断裂。"));
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.DisableFilterCompromise,
                    "诊断候选：关闭目标滤镜（妥协）",
                    "只用于确认爆炸是否由该滤镜造成；会缺效果，不能默认采用。"));
                break;
            case VisualRiskKind.SplitFilter:
            case VisualRiskKind.NegativeFilterParameter:
                if (canBakeFilters)
                {
                    strategies.Add(new VisualRiskStrategyOption(
                        VisualRiskStrategyKind.BakeBackgroundFilterChain,
                        onlyBackgroundFilters
                            ? "候选：背景滤镜链与背景一起烤入"
                            : "候选：滤镜链与视频层一起烤入（高风险）",
                        onlyBackgroundFilters
                            ? "滤镜只声明作用背景平面；连同背景装饰预渲染，派生谱关闭重复滤镜。"
                            : "滤镜跨越多个平面；只作为真实对照候选，若影响轨道或实时前景就不能采用。"));
                }
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.FinalCompositeRealtime,
                    "候选：滤镜保留到最终合成",
                    "让滤镜在背景视频、轨道和前景合成后执行；适合会影响轨道或多层的效果。"));
                if (canBakeFilters && hasBackgroundFilters && hasForegroundFilters)
                {
                    strategies.Add(new VisualRiskStrategyOption(
                        VisualRiskStrategyKind.BakeBackgroundPlaneKeepForegroundRealtime,
                        "候选：只烤背景平面滤镜",
                        "背景滤镜随背景视频预渲染，前景滤镜留到最终合成继续处理轨道与实时前景。"));
                    strategies.Add(new VisualRiskStrategyOption(
                        VisualRiskStrategyKind.BakeBackgroundAndGlobalKeepRealtimeFilters,
                        "候选：背景与全画面滤镜烤入，全画面滤镜同时实时",
                        "全画面滤镜既预处理视频背景，也保留给实时轨道；背景专用滤镜只烤入，前景滤镜只实时。"));
                    strategies.Add(new VisualRiskStrategyOption(
                        VisualRiskStrategyKind.BakeForegroundPlaneKeepBackgroundRealtime,
                        "反向对照：只烤前景平面滤镜",
                        "前景滤镜进入视频、背景滤镜留实时；主要用于实际对照判断平面归属是否与预期相反。"));
                }
                if (onlyBackgroundFilters)
                    strategies.Add(new VisualRiskStrategyOption(
                        VisualRiskStrategyKind.SwitchToVideo,
                        "候选：Video 整体预渲染装饰与特效",
                        "把装饰和特效放入同一视频，轨道与星球仍实时；仍需确认滤镜确实不依赖轨道。"));
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.SwitchToVideoVfxPro,
                    "候选：Video VFX Pro 保留前景层",
                    "背景滤镜与背景装饰预渲染，深度小于零的前景仍实时，用对照确认跨层关系。"));
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.DisableFilterCompromise,
                    "诊断候选：关闭相关滤镜（妥协）",
                    "只用于定位爆炸来源；会缺效果，不能默认采用。"));
                break;
            case VisualRiskKind.DynamicImageSwap:
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.BakeDynamicImageSequence,
                    "候选：完整换图序列烤入视频",
                    $"让 {decorationCount:N0} 个换图依赖在视频层连续执行，不按初始空贴图误删。"));
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.RetainDynamicImageSequence,
                    "候选：换图装饰保留实时",
                    $"把 {decorationCount:N0} 个相关装饰留给游戏执行 MoveDecorations。"));
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.SwitchToVideoVfxPro,
                    "候选：Video VFX Pro 保留前景换图",
                    "只烤背景装饰，前景换图序列留在派生谱，适合换图对象会遮挡轨道的情况。"));
                break;
            case VisualRiskKind.ParticleComposition:
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.BakeParticleSystems,
                    "候选：完整粒子系统烤入视频",
                    "粒子创建、控制、预热和尾帧全部由离线时间线执行；用实际片段检查滤镜与遮罩依赖。"));
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.ExtendParticleTail,
                    "候选：粒子烤入并延长尾帧",
                    "保持粒子烤入视频，但正式输出至少多渲染 10 秒，避免关卡结束后粒子突然冻结。"));
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.RetainParticleSystems,
                    "候选：相关粒子保留实时",
                    $"把 {decorationCount:N0} 个粒子系统留给游戏；适合依赖实时滤镜或遮罩的粒子。"));
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.SwitchToVideoVfxPro,
                    "候选：Video VFX Pro 保留前景粒子",
                    "背景粒子进视频，前景粒子继续实时，避免前景粒子被压到轨道下面。"));
                break;
            case VisualRiskKind.SourceBackgroundVideo:
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.PreserveBackgroundVideo,
                    "候选：保留源背景视频并逐帧定位",
                    "沿用原谱 bgVideo、vidOffset、倒计时、循环和原版画幅模式，不把视频当静态贴图。"));
                break;
            case VisualRiskKind.CreatedObject:
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.KeepCreatedObjectsRealtime,
                    "候选：创建对象保留实时",
                    "AddObject 默认留在派生谱由原版运行时处理，不强行塞进选择性装饰渲染器。"));
                break;
            case VisualRiskKind.CameraComposition:
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.FinalCompositeRealtime,
                    "候选：运镜只在最终合成执行",
                    "背景视频不重复执行运镜，派生谱在轨道、前景和视频合成后按原版事件执行一次。"));
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.SwitchToVideo,
                    "候选：装饰与特效共用预渲染运镜",
                    "把装饰与特效放入同一预渲染相机，轨道实时；用对照检查运动视差是否仍一致。"));
                strategies.Add(new VisualRiskStrategyOption(
                    VisualRiskStrategyKind.SwitchToVideoVfxPro,
                    "候选：前后景分层运镜",
                    "背景层预渲染，前景层与轨道实时；用对照确认相机方向、整圈旋转和缩放。"));
                break;
        }
        if (review.Issue.Kind is not (VisualRiskKind.MemoryPressure or VisualRiskKind.MissingAsset))
            strategies.Add(new VisualRiskStrategyOption(
                VisualRiskStrategyKind.VideoProFallback,
                "确定性回退：Video Pro 完整压平",
                "直接用原版场景完整渲染所有视觉层；这是无法安全拆层时的完整还原方案，不是接受错误。"));
        return strategies.ToArray();
    }

    private VisualRiskDiagnosticOptions BuildRiskDiagnosticOptions(VisualRiskStrategyOption strategy)
    {
        var selectedReview = RiskReviewGrid.SelectedItem as VisualRiskReviewItem;
        var profile = _profileChecks
            .Where(static check => check.IsChecked == true)
            .Select(static check => (OutputProfile)check.Tag)
            .FirstOrDefault(static profile => profile != OutputProfile.VideoPro);
        if (profile == OutputProfile.VideoPro) profile = OutputProfile.VideoVfx;
        profile = strategy.Kind switch
        {
            VisualRiskStrategyKind.SwitchToVideo => OutputProfile.Video,
            VisualRiskStrategyKind.SwitchToVideoVfxPro => OutputProfile.VideoVfxPro,
            _ => profile,
        };
        var retained = new HashSet<int>(GetRetainedDecorationIndices());
        var excluded = new HashSet<int>(GetExcludedDecorationIndices());
        var baked = new HashSet<string>(GetBakedFilterKeys(), StringComparer.Ordinal);
        var disabled = new HashSet<string>(GetDisabledFilterKeys(), StringComparer.Ordinal);
        if (selectedReview is { } review)
        {
            var filterKeys = review.Issue.RelatedFilterKeys ??
                             (string.IsNullOrWhiteSpace(review.Issue.FilterKey) ? [] : [review.Issue.FilterKey]);
            switch (strategy.Kind)
            {
                case VisualRiskStrategyKind.FinalCompositeRealtime:
                case VisualRiskStrategyKind.RetainTargetedFilterWithTargets:
                case VisualRiskStrategyKind.RetainMaskDependencyClosure:
                case VisualRiskStrategyKind.RetainDynamicImageSequence:
                case VisualRiskStrategyKind.RetainParticleSystems:
                    baked.ExceptWith(filterKeys);
                    disabled.ExceptWith(filterKeys);
                    retained.UnionWith(review.Issue.RelatedDecorationIndices ?? []);
                    excluded.ExceptWith(review.Issue.RelatedDecorationIndices ?? []);
                    break;
                case VisualRiskStrategyKind.RetainAllMaskDependencyClosures:
                    baked.ExceptWith(filterKeys);
                    disabled.ExceptWith(filterKeys);
                    var allMaskIndices = _riskReviews
                        .Where(static item => item.Issue.Kind == VisualRiskKind.MaskComposition)
                        .SelectMany(static item => item.Issue.RelatedDecorationIndices ?? [])
                        .ToHashSet();
                    retained.UnionWith(allMaskIndices);
                    excluded.ExceptWith(allMaskIndices);
                    break;
                case VisualRiskStrategyKind.BakeBackgroundFilterChain:
                case VisualRiskStrategyKind.BakeTargetedFilterWithTargets:
                case VisualRiskStrategyKind.BakeMaskDependencyClosure:
                case VisualRiskStrategyKind.BakeDynamicImageSequence:
                case VisualRiskStrategyKind.BakeParticleSystems:
                    baked.UnionWith(filterKeys);
                    disabled.UnionWith(filterKeys);
                    retained.ExceptWith(review.Issue.RelatedDecorationIndices ?? []);
                    excluded.ExceptWith(review.Issue.RelatedDecorationIndices ?? []);
                    break;
                case VisualRiskStrategyKind.DisableFilterCompromise:
                    baked.ExceptWith(filterKeys);
                    disabled.UnionWith(filterKeys);
                    break;
                case VisualRiskStrategyKind.BakeBackgroundHistoryComposite:
                    var historyBakeKeys = _filterChoices
                        .Where(item => filterKeys.Contains(item.Item.Key, StringComparer.Ordinal) &&
                                       (item.Item.EventType == "HallOfMirrors" ||
                                        item.Item.Plane.Equals("Background", StringComparison.OrdinalIgnoreCase)))
                        .Select(static item => item.Item.Key)
                        .ToHashSet(StringComparer.Ordinal);
                    var historyRealtimeKeys = filterKeys.Where(key => !historyBakeKeys.Contains(key)).ToArray();
                    baked.UnionWith(historyBakeKeys);
                    disabled.UnionWith(historyBakeKeys);
                    baked.ExceptWith(historyRealtimeKeys);
                    disabled.ExceptWith(historyRealtimeKeys);
                    break;
                case VisualRiskStrategyKind.BakeBackgroundAndGlobalKeepRealtimeFilters:
                    var backgroundKeys = _filterChoices
                        .Where(item => filterKeys.Contains(item.Item.Key, StringComparer.Ordinal) &&
                                       item.Item.Plane.Equals("Background", StringComparison.OrdinalIgnoreCase))
                        .Select(static item => item.Item.Key)
                        .ToHashSet(StringComparer.Ordinal);
                    var foregroundKeys = _filterChoices
                        .Where(item => filterKeys.Contains(item.Item.Key, StringComparer.Ordinal) &&
                                       item.Item.Plane.Equals("Foreground", StringComparison.OrdinalIgnoreCase))
                        .Select(static item => item.Item.Key)
                        .ToHashSet(StringComparer.Ordinal);
                    var globalKeys = filterKeys
                        .Where(key => !backgroundKeys.Contains(key) && !foregroundKeys.Contains(key))
                        .ToHashSet(StringComparer.Ordinal);
                    baked.UnionWith(backgroundKeys);
                    baked.UnionWith(globalKeys);
                    baked.ExceptWith(foregroundKeys);
                    disabled.UnionWith(backgroundKeys);
                    disabled.ExceptWith(globalKeys);
                    disabled.ExceptWith(foregroundKeys);
                    break;
                case VisualRiskStrategyKind.BakeBackgroundPlaneKeepForegroundRealtime:
                case VisualRiskStrategyKind.BakeForegroundPlaneKeepBackgroundRealtime:
                    var bakePlane = strategy.Kind == VisualRiskStrategyKind.BakeBackgroundPlaneKeepForegroundRealtime
                        ? "Background"
                        : "Foreground";
                    var planeKeys = _filterChoices
                        .Where(item => filterKeys.Contains(item.Item.Key, StringComparer.Ordinal) &&
                                       item.Item.Plane.Equals(bakePlane, StringComparison.OrdinalIgnoreCase))
                        .Select(static item => item.Item.Key)
                        .ToHashSet(StringComparer.Ordinal);
                    var realtimeKeys = filterKeys.Where(key => !planeKeys.Contains(key)).ToArray();
                    baked.UnionWith(planeKeys);
                    disabled.UnionWith(planeKeys);
                    baked.ExceptWith(realtimeKeys);
                    disabled.ExceptWith(realtimeKeys);
                    break;
            }
        }
        double? preRollSeconds = strategy.Kind == VisualRiskStrategyKind.PrewarmHistory &&
                                 selectedReview?.Issue.TimeSeconds is { } riskTime
            ? Math.Max(0d, riskTime + 0.001d)
            : null;
        var durationSeconds = preRollSeconds.HasValue
            ? Math.Min(60d, preRollSeconds.Value + 10d)
            : 10d;
        return new VisualRiskDiagnosticOptions(
            profile,
            retained,
            excluded,
            baked,
            disabled,
            strategy.Kind.ToString(),
            preRollSeconds,
            durationSeconds);
    }

    private void ShowActiveRiskDiagnostic(VisualRiskReviewItem review)
    {
        RiskDiagnosticStatusText.Text = review.DiagnosticSummary;
        RiskComparisonList.ItemsSource = review.ComparisonFrames;
        RiskComparisonList.SelectedIndex = review.ComparisonFrames.Count > 0 ? 0 : -1;
        GenerateRiskComparisonButton.IsEnabled = review.Issue.RequiresComparison &&
                                                 review.Issue.TimeSeconds.HasValue &&
                                                 review.ActiveStrategy != VisualRiskStrategyKind.VideoProFallback;
        if (review.ComparisonFrames.Count != 0) return;
        RiskReferenceImage.Source = null;
        RiskCandidateImage.Source = null;
    }

    private void RiskComparisonList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RiskComparisonList.SelectedItem is not VisualRiskComparisonViewItem selected) return;
        RiskReferenceImage.Source = selected.ReferenceImage;
        RiskCandidateImage.Source = selected.CandidateImage;
    }

    private void ConfirmRiskNormal_Click(object sender, RoutedEventArgs e)
    {
        if (RiskReviewGrid.SelectedItem is not VisualRiskReviewItem selected) return;
        if (selected.Issue.RequiresComparison && !selected.HasComparison)
        {
            DarkDialog.Show(this, "这个风险项必须先生成原版／拆层闭环对照，不能空点“正常”。", "尚未对照");
            return;
        }
        if (selected.AppliedStrategy != selected.ActiveStrategy)
        {
            DarkDialog.Show(
                this,
                "当前看到的是候选对照，但这个候选尚未应用到正式渲染配置。请先点击“应用此候选”，再确认正常。",
                "候选尚未应用");
            return;
        }
        selected.Status = VisualRiskReviewStatus.ConfirmedNormal;
        RiskDiagnosticStatusText.Text = "已由用户确认此处画面正常。";
    }

    private void MarkRiskNeedsAdjustment_Click(object sender, RoutedEventArgs e)
    {
        if (RiskReviewGrid.SelectedItem is not VisualRiskReviewItem selected) return;
        selected.Status = VisualRiskReviewStatus.NeedsAdjustment;
        RiskDiagnosticStatusText.Text = "已标记为需要调整；正式拆层渲染会继续被阻止。";
        if (!string.IsNullOrWhiteSpace(selected.Issue.FilterKey))
        {
            FilterOwnershipGrid.SelectedItem = _filterChoices.FirstOrDefault(item =>
                item.Item.Key.Equals(selected.Issue.FilterKey, StringComparison.Ordinal));
            VisualEditorTabs.SelectedItem = FilterEditorTab;
        }
        else if (selected.Issue.DecorationIndex is { } decorationIndex)
        {
            DecorationRetentionList.SelectedItem = _decorationChoices.FirstOrDefault(item =>
                item.Item.Index == decorationIndex);
            VisualEditorTabs.SelectedItem = DecorationEditorTab;
        }
    }

    private void AcceptRisk_Click(object sender, RoutedEventArgs e)
    {
        if (RiskReviewGrid.SelectedItem is not VisualRiskReviewItem selected) return;
        selected.Status = VisualRiskReviewStatus.AcceptedRisk;
        RiskDiagnosticStatusText.Text = "已明确接受此风险；记录不会伪装成画面正常。";
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
        InvalidateRiskComparisons();
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

            if (!_exitWhenQueueFinishes)
            {
                var hasSplitProfile = profiles.Any(static profile => profile != OutputProfile.VideoPro);
                var blockingRisks = _riskReviews.Where(item =>
                        (item.Status is VisualRiskReviewStatus.Pending or VisualRiskReviewStatus.NeedsAdjustment) &&
                        item.Issue.Severity != VisualRiskSeverity.Information &&
                        (hasSplitProfile || item.Issue.AffectsVideoPro))
                    .ToArray();
                if (blockingRisks.Length > 0)
                {
                    MainTabs.SelectedIndex = 1;
                    VisualEditorTabs.SelectedItem = RiskReviewTab;
                    RiskReviewGrid.SelectedItem = blockingRisks[0];
                    throw new InvalidOperationException(
                        $"还有 {blockingRisks.Length:N0} 个相关风险尚未确认或仍需调整。" +
                        "程序已经切到“风险确认”；请生成原版／拆层闭环对照后确认，或明确接受已知风险。");
                }
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
                .Where(static item => (item.Ownership is FilterOwnership.BakeIntoVideo or FilterOwnership.BakeAndRealtime) &&
                                      !item.Item.OfflineSupported)
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
                    memoryLimitMb,
                    _finalCompositeVerificationMode);
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
            task.MemoryLimitMb,
            task.FinalCompositeVerification);
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
        int MemoryLimitMb,
        bool FinalCompositeVerification);

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
            new("烤入视频并保留实时", FilterOwnership.BakeAndRealtime),
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
        public string OfflineStatus => Item.OfflineSupported
            ? "有独立实现；仍须实际对照"
            : "当前无独立实现；实测最终合成、共层或完整压平";
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

    private enum VisualRiskReviewStatus
    {
        Pending,
        ConfirmedNormal,
        NeedsAdjustment,
        AcceptedRisk,
    }

    private enum VisualRiskStrategyKind
    {
        Current,
        FinalCompositeRealtime,
        BakeBackgroundFilterChain,
        BakeBackgroundPlaneKeepForegroundRealtime,
        BakeBackgroundHistoryComposite,
        BakeBackgroundAndGlobalKeepRealtimeFilters,
        BakeForegroundPlaneKeepBackgroundRealtime,
        BakeTargetedFilterWithTargets,
        RetainTargetedFilterWithTargets,
        BakeMaskDependencyClosure,
        RetainMaskDependencyClosure,
        RetainAllMaskDependencyClosures,
        BakeDynamicImageSequence,
        RetainDynamicImageSequence,
        BakeParticleSystems,
        RetainParticleSystems,
        DisableFilterCompromise,
        KeepCreatedObjectsRealtime,
        ExtendParticleTail,
        PreserveBackgroundVideo,
        PrewarmHistory,
        SwitchToVideo,
        SwitchToVideoVfxPro,
        VideoProFallback,
    }

    private sealed record VisualRiskStrategyOption(
        VisualRiskStrategyKind Kind,
        string Label,
        string Description);

    private sealed class VisualRiskReviewItem : INotifyPropertyChanged
    {
        private VisualRiskReviewStatus _status = VisualRiskReviewStatus.Pending;
        private readonly Dictionary<VisualRiskStrategyKind, DiagnosticSnapshot> _diagnostics = [];
        private readonly Dictionary<VisualRiskStrategyKind, string> _diagnosticFailures = [];
        private VisualRiskStrategyKind _activeStrategy = VisualRiskStrategyKind.Current;
        private VisualRiskStrategyKind? _appliedStrategy;

        public VisualRiskReviewItem(VisualRiskIssue issue) => Issue = issue;
        public event PropertyChangedEventHandler? PropertyChanged;
        public VisualRiskIssue Issue { get; }
        public string Title => Issue.Title;
        public string FloorText => Issue.FirstFloor?.ToString(CultureInfo.InvariantCulture) ?? "—";
        public string TimeText => Issue.TimeSeconds.HasValue ? $"{Issue.TimeSeconds.Value:0.###}s" : "—";
        public VisualRiskStrategyKind ActiveStrategy => _activeStrategy;
        public VisualRiskStrategyKind? AppliedStrategy => _appliedStrategy;
        public bool HasComparison => _diagnostics.ContainsKey(_activeStrategy);
        public IReadOnlyList<VisualRiskComparisonViewItem> ComparisonFrames =>
            _diagnostics.TryGetValue(_activeStrategy, out var diagnostic) ? diagnostic.Frames : [];
        public string StatusText => Status switch
        {
            VisualRiskReviewStatus.ConfirmedNormal => "已确认正常",
            VisualRiskReviewStatus.NeedsAdjustment => "需要调整",
            VisualRiskReviewStatus.AcceptedRisk => "已接受风险",
            _ => "待确认",
        };
        public string DiagnosticSummary => HasComparison
            ? _diagnostics[_activeStrategy].StructuralSimilarity.HasValue
                ? $"“{_activeStrategy}”候选闭环对照已生成；整段 SSIM {_diagnostics[_activeStrategy].StructuralSimilarity!.Value:0.000000}。数值只用于候选排序与定位差异，不能自动宣布解决。"
                : "闭环对照已生成；未取得 SSIM，仍可逐帧人工确认。"
            : _diagnosticFailures.TryGetValue(_activeStrategy, out var failure)
                ? "这个候选已实测失败：" + failure
            : Issue.RequiresComparison
                ? "尚未生成闭环对照。"
                : "这是非画面对照项；修复后重新载入，或明确接受风险。";

        public VisualRiskReviewStatus Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
            }
        }

        public void SelectStrategy(VisualRiskStrategyKind strategy)
        {
            if (_activeStrategy == strategy) return;
            _activeStrategy = strategy;
            if (Status != VisualRiskReviewStatus.AcceptedRisk) Status = VisualRiskReviewStatus.Pending;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveStrategy)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasComparison)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ComparisonFrames)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DiagnosticSummary)));
        }

        public void SetDiagnostic(VisualRiskStrategyKind strategy, VisualRiskDiagnosticResult result)
        {
            _activeStrategy = strategy;
            _diagnosticFailures.Remove(strategy);
            _diagnostics[strategy] = new DiagnosticSnapshot(
                result.Frames.Select(static pair => new VisualRiskComparisonViewItem(pair)).ToArray(),
                result.StructuralSimilarity);
            if (Status != VisualRiskReviewStatus.AcceptedRisk) Status = VisualRiskReviewStatus.Pending;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveStrategy)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasComparison)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ComparisonFrames)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DiagnosticSummary)));
        }

        public void SetDiagnosticFailure(VisualRiskStrategyKind strategy, string error)
        {
            _diagnostics.Remove(strategy);
            _diagnosticFailures[strategy] = error;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasComparison)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ComparisonFrames)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DiagnosticSummary)));
        }

        public void MarkStrategyApplied(VisualRiskStrategyKind strategy)
        {
            _appliedStrategy = strategy;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AppliedStrategy)));
        }

        public void InvalidateComparison()
        {
            if (_diagnostics.Count == 0 &&
                (Status is VisualRiskReviewStatus.Pending or VisualRiskReviewStatus.AcceptedRisk))
                return;
            _diagnostics.Clear();
            _diagnosticFailures.Clear();
            _activeStrategy = VisualRiskStrategyKind.Current;
            _appliedStrategy = null;
            if (Status != VisualRiskReviewStatus.AcceptedRisk) Status = VisualRiskReviewStatus.Pending;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveStrategy)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasComparison)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ComparisonFrames)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DiagnosticSummary)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AppliedStrategy)));
        }

        private sealed record DiagnosticSnapshot(
            IReadOnlyList<VisualRiskComparisonViewItem> Frames,
            double? StructuralSimilarity);
    }

    private sealed class VisualRiskComparisonViewItem
    {
        public VisualRiskComparisonViewItem(VisualRiskComparisonFilePair pair)
        {
            TimestampLabel = $"{pair.TimeSeconds:0.###} 秒";
            ReferenceImage = LoadBitmap(pair.ReferencePath, 900);
            CandidateImage = LoadBitmap(pair.CandidatePath, 900);
        }

        public string TimestampLabel { get; }
        public BitmapSource? ReferenceImage { get; }
        public BitmapSource? CandidateImage { get; }
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
