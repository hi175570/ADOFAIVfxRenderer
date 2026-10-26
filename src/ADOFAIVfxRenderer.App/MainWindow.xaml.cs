using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
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
    private readonly Queue<RenderVariant> _renderQueue = new();
    private readonly List<string> _completedBatchOutputs = [];
    private GameInstallation? _batchInstallation;
    private ChartMetadata? _batchChart;
    private TailExtension _batchTail = TailExtension.Zero;
    private int _batchTotal;
    private int _batchCompleted;
    private IReadOnlySet<int> _batchRetainedDecorationIndices = new HashSet<int>();
    private IReadOnlySet<string> _batchBakedFilterKeys = new HashSet<string>();
    private IReadOnlySet<string> _batchDisabledFilterKeys = new HashSet<string>();
    private RenderBackend _batchRenderBackend = RenderBackend.Automatic;

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
        Loaded += ApplyCommandLineSmokeTest;
    }

    private void ApplyCommandLineSmokeTest(object sender, RoutedEventArgs e)
    {
        Loaded -= ApplyCommandLineSmokeTest;
        var arguments = Environment.GetCommandLineArgs();
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
            StartRender_Click(this, new RoutedEventArgs());
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            DarkDialog.Show(this, exception.Message, "冒烟测试无法启动");
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
            new Choice<RenderBackend>("自动（GPU 画面 + CPU 编码，推荐）", RenderBackend.Automatic),
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
        AppSettingsStore.Save(new AppSettings(effectiveGame, effectiveChart, chartDirectory));
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
        var unsupportedBake = _filterChoices.Count(static item =>
            item.Ownership == FilterOwnership.BakeIntoVideo && !item.Item.OfflineSupported);
        FilterOwnershipSummaryText.Text = _chart is null
            ? "尚未载入谱面。"
            : $"共 {_filterChoices.Count:N0} 组 · 保留实时 {realtime:N0} · 烤入视频 {baked:N0} · 忽略 {ignored:N0}" +
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
            ? _decorationChoices.Where(static item => item.IsListed)
            : _decorationChoices.Where(static item => !item.IsListed);
        return retained.Select(static item => item.Item.Index).ToHashSet();
    }

    private void RefreshRetentionSummary()
    {
        if (RetentionExplanationText is null || RetentionCountText is null) return;
        var retainedCount = GetRetainedDecorationIndices().Count;
        RetentionExplanationText.Text = CurrentRetentionMode == DecorationRetentionMode.Whitelist
            ? "勾选的贴图不烘焙进 Video VFX，而是在派生谱面中继续实时显示。"
            : "勾选的贴图仍烘焙进视频；未勾选的贴图留在派生谱面中实时显示。";
        RetentionCountText.Text = _chart is null
            ? "尚未载入谱面"
            : $"实时保留 {retainedCount:N0} / {_decorationChoices.Count:N0} 个";
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

        OutputList.ItemsSource = variants.Select(static variant => variant.DisplayName).ToArray();
        SummaryText.Text =
            $"共 {variants.Count} 个顺序渲染任务 · {oneSecondPlan.Passes.Count} 组相同画面配置 · " +
            $"每秒 {oneSecondPlan.UniqueSceneMoments} 个唯一时间点\n" +
            $"结束后继续 {tailSeconds:0.######} 秒；不同 FPS 自动换算各自尾帧。";
    }

    private void StartRender_Click(object sender, RoutedEventArgs e)
    {
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

            _renderQueue.Clear();
            foreach (var variant in variants) _renderQueue.Enqueue(variant);
            _completedBatchOutputs.Clear();
            _batchInstallation = _installation;
            _batchChart = _chart;
            _batchTail = TailExtension.FromSeconds(tailSeconds);
            _batchRetainedDecorationIndices = GetRetainedDecorationIndices();
            _batchBakedFilterKeys = GetBakedFilterKeys();
            _batchDisabledFilterKeys = GetDisabledFilterKeys();
            var unsupportedBake = _filterChoices
                .Where(static item => item.Ownership == FilterOwnership.BakeIntoVideo && !item.Item.OfflineSupported)
                .Select(static item => item.DisplayName)
                .ToArray();
            if (unsupportedBake.Length > 0)
            {
                throw new NotSupportedException("这些滤镜尚不能离线烤入：" + string.Join("、", unsupportedBake));
            }
            _batchRenderBackend = ((Choice<RenderBackend>)RenderBackendBox.SelectedItem).Value;
            _batchTotal = variants.Count;
            _batchCompleted = 0;
            OutputList.ItemsSource = variants.Select(static variant => "等待 · " + variant.DisplayName).ToArray();

            StartRenderButton.IsEnabled = false;
            CancelRenderButton.IsEnabled = true;
            RenderProgressBar.Value = 0;
            WorkerProgressText.Text = "0 / ? 帧 · 0%";
            WorkerSpeedText.Text = "正在启动";
            StartNextBatchRender();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            _renderQueue.Clear();
            StartRenderButton.IsEnabled = true;
            CancelRenderButton.IsEnabled = false;
            DarkDialog.Show(this, exception.Message, "无法开始渲染");
        }
    }

    private void CancelRender_Click(object sender, RoutedEventArgs e) => _worker.Cancel();

    private void StartNextBatchRender()
    {
        if (_renderQueue.Count == 0)
        {
            StartRenderButton.IsEnabled = true;
            CancelRenderButton.IsEnabled = false;
            SummaryText.Text = $"批量渲染完成：{_batchCompleted}/{_batchTotal}\n" + string.Join("\n", _completedBatchOutputs);
            return;
        }

        var variant = _renderQueue.Dequeue();
        var outputPath = _worker.Start(
            _batchInstallation ?? throw new InvalidOperationException("缺少批量渲染的游戏安装信息。"),
            _batchChart ?? throw new InvalidOperationException("缺少批量渲染的谱面信息。"),
            variant,
            _batchTail,
            _durationLimitSeconds,
            _batchRetainedDecorationIndices,
            _batchBakedFilterKeys,
            _batchDisabledFilterKeys,
            _batchRenderBackend);
        var current = _batchTotal - _renderQueue.Count;
        SummaryText.Text = $"批量任务 {current}/{_batchTotal}：{variant.DisplayName}\n" +
                           $"烤入滤镜 {_batchBakedFilterKeys.Count:N0} 组；派生谱关闭 {_batchDisabledFilterKeys.Count:N0} 组。\n" +
                           $"输出文件：{outputPath}";
    }

    private async void Worker_StatusChanged(WorkerStatus status)
    {
        WorkerStateText.Text = status.Message;
        RenderProgressBar.Value = Math.Clamp(status.Percent, 0f, 100f);
        WorkerProgressText.Text = $"{status.CurrentFrame:N0} / {status.TotalFrames:N0} 帧 · {status.Percent:0.0}%";
        WorkerSpeedText.Text = status.RenderedFramesPerSecond > 0
            ? $"{status.RenderedFramesPerSecond:0.00} 帧/秒 · {status.SecondsPerFrame:0.000} 秒/帧"
            : status.State;

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
            _batchCompleted++;
            _completedBatchOutputs.Add(string.IsNullOrWhiteSpace(status.DerivedChartPath)
                ? status.OutputPath
                : $"{status.OutputPath} | {status.DerivedChartPath} | 偏移 {status.VideoOffsetMilliseconds} ms");
            await _worker.WaitForExitAsync();
            try
            {
                StartNextBatchRender();
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                _renderQueue.Clear();
                StartRenderButton.IsEnabled = true;
                CancelRenderButton.IsEnabled = false;
                DarkDialog.Show(this, exception.Message, "无法继续批量渲染");
            }
        }
        else if (status.State is "failed" or "cancelled")
        {
            _renderQueue.Clear();
            StartRenderButton.IsEnabled = true;
            CancelRenderButton.IsEnabled = false;
        }
    }

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

    private sealed record Choice<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }

    private enum DecorationRetentionMode
    {
        Whitelist,
        Blacklist,
    }

    private sealed class DecorationRetentionItem : INotifyPropertyChanged
    {
        private bool _isListed;
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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsListed)));
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
