using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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
    private GameInstallation? _installation;
    private ChartMetadata? _chart;
    private bool _updatingTail;
    private bool _tailFramesWereEdited;
    private bool _viewReady;
    private decimal? _durationLimitSeconds;

    public MainWindow()
    {
        SourceInitialized += ApplyDarkTitleBar;
        InitializeComponent();
        _worker.StatusChanged += Worker_StatusChanged;
        _viewReady = true;
        PopulateOptions();
        PopulateDetectedGames();
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

            ChartPathBox.Text = chartPath;
            _chart = ChartMetadataReader.Read(chartPath);
            UpdateChartCompatibilityText();

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
            MessageBox.Show(this, exception.Message, "冒烟测试无法启动", MessageBoxButton.OK, MessageBoxImage.Error);
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

        QualityBox.ItemsSource = new[]
        {
            new Choice<VideoQualityPreset>("平衡（推荐）", VideoQualityPreset.Balanced),
            new Choice<VideoQualityPreset>("高质量", VideoQualityPreset.HighQuality),
            new Choice<VideoQualityPreset>("较小文件", VideoQualityPreset.SmallerFile),
            new Choice<VideoQualityPreset>("自定义", VideoQualityPreset.Custom),
        };
        QualityBox.SelectedIndex = 0;
        RefreshContentChoices();
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
        UpdateChartCompatibilityText();
    }

    private void BrowseChart_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 ADOFAI 谱面",
            Filter = "ADOFAI 谱面 (*.adofai)|*.adofai|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ChartPathBox.Text = dialog.FileName;
        try
        {
            _chart = ChartMetadataReader.Read(dialog.FileName);
            UpdateChartCompatibilityText();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _chart = null;
            ChartStatusText.Text = $"✗ {exception.Message}";
        }
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
        EffectsCheckBox.IsChecked = content.HasFlag(RenderContent.Effects);
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
        if (EffectsCheckBox.IsChecked == true) content |= RenderContent.Effects;
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
            MessageBox.Show(this, "输出版本、分辨率和 FPS 都至少选择一个。", "无法生成计划", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParseNonNegativeDecimal(TailSecondsBox.Text, out var tailSeconds))
        {
            MessageBox.Show(this, "结束延长秒数无效。", "无法生成计划", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show(this, exception.Message, "无法生成计划", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            $"共 {variants.Count} 个输出 · {oneSecondPlan.Passes.Count} 个共享画面通道 · " +
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

            var profiles = _profileChecks.Where(static check => check.IsChecked == true).ToArray();
            var resolutions = _resolutionChecks.Where(static check => check.IsChecked == true).ToArray();
            var frameRates = _frameRateChecks.Where(static check => check.IsChecked == true).ToArray();
            if (profiles.Length != 1 || resolutions.Length != 1 || frameRates.Length != 1)
            {
                throw new InvalidOperationException("首个实机测试请只选择一个版本、一个分辨率和一个 FPS。");
            }

            var profile = (OutputProfile)profiles[0].Tag;
            if (profile is not (OutputProfile.VideoVfx or OutputProfile.VideoVfxPro))
            {
                throw new NotSupportedException("当前可实测的是 Video VFX 与 Video VFX Pro；Video / Video Pro 要等原版滤镜适配完成。");
            }

            if (!TryParseNonNegativeDecimal(TailSecondsBox.Text, out var tailSeconds))
            {
                throw new InvalidOperationException("结束延长秒数无效。");
            }

            var quality = ((Choice<VideoQualityPreset>)QualityBox.SelectedItem).Value;
            var customBitrate = GetCustomBitrate(quality);
            var variant = new RenderVariant(
                profile,
                (VideoResolution)resolutions[0].Tag,
                (int)frameRates[0].Tag,
                quality,
                GetContentOverride(),
                customBitrate);
            var outputPath = _worker.Start(
                _installation,
                _chart,
                variant,
                TailExtension.FromSeconds(tailSeconds),
                _durationLimitSeconds);

            StartRenderButton.IsEnabled = false;
            CancelRenderButton.IsEnabled = true;
            RenderProgressBar.Value = 0;
            WorkerProgressText.Text = "0 / ? 帧 · 0%";
            WorkerSpeedText.Text = "正在启动";
            SummaryText.Text = $"输出文件：{outputPath}";
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "无法开始渲染", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelRender_Click(object sender, RoutedEventArgs e) => _worker.Cancel();

    private void Worker_StatusChanged(WorkerStatus status)
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
                : $"输出文件：{status.OutputPath}";
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

        if (status.State is "complete" or "failed" or "cancelled")
        {
            StartRenderButton.IsEnabled = true;
            CancelRenderButton.IsEnabled = false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}
