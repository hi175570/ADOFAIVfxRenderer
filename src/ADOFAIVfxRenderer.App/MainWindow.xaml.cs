using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ADOFAIVfxRenderer.Core;
using Microsoft.Win32;

namespace ADOFAIVfxRenderer.App;

public partial class MainWindow : Window
{
    private readonly List<CheckBox> _profileChecks = [];
    private readonly List<CheckBox> _resolutionChecks = [];
    private readonly List<CheckBox> _frameRateChecks = [];
    private GameInstallation? _installation;
    private ChartMetadata? _chart;
    private bool _updatingTail;
    private bool _tailFramesWereEdited;
    private bool _viewReady;

    public MainWindow()
    {
        InitializeComponent();
        _viewReady = true;
        PopulateOptions();
        PopulateDetectedGames();
        RecalculateTailFrames();
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
                IsChecked = resolution == VideoPresets.P1080,
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
                IsChecked = frameRate == 60,
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
        var strategy = ((Choice<ResolutionRenderStrategy>)ResolutionStrategyBox.SelectedItem).Value;
        var variants = RenderBatchPlanner.BuildVariants(profiles, resolutions, frameRates, quality);

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

    private int GetReferenceFrameRate() =>
        _frameRateChecks.Where(static check => check.IsChecked == true)
            .Select(static check => (int)check.Tag)
            .DefaultIfEmpty(60)
            .Max();

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
}
