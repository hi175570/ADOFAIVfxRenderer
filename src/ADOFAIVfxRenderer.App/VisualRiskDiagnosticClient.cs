using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ADOFAIVfxRenderer.Core;

namespace ADOFAIVfxRenderer.App;

internal sealed record VisualRiskDiagnosticOptions(
    OutputProfile Profile,
    IReadOnlySet<int> RetainedDecorationIndices,
    IReadOnlySet<int> ExcludedDecorationIndices,
    IReadOnlySet<string> BakedFilterKeys,
    IReadOnlySet<string> DisabledFilterKeys,
    string DiagnosticLabel = "current",
    double? PreRollSeconds = null,
    double DurationSeconds = 10d);

internal sealed record VisualRiskComparisonFilePair(
    double TimeSeconds,
    string ReferencePath,
    string CandidatePath);

internal sealed record VisualRiskDiagnosticResult(
    IReadOnlyList<VisualRiskComparisonFilePair> Frames,
    string ReferenceVideoPath,
    string CandidateVideoPath,
    double? StructuralSimilarity);

internal sealed record VisualRiskCandidateFrameResult(
    double TimeSeconds,
    string FramePath,
    string CandidateVideoPath);

internal static partial class VisualRiskDiagnosticClient
{
    private const int Width = 640;
    private const int Height = 360;
    private const int FramesPerSecond = 30;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<VisualRiskCandidateFrameResult> GenerateCandidateFrameAsync(
        GameInstallation installation,
        ChartMetadata chart,
        VisualRiskIssue issue,
        VisualRiskDiagnosticOptions options,
        IProgress<string>? progress = null)
    {
        if (!installation.CanRender || installation.RootDirectory is null || installation.DataDirectory is null)
            throw new InvalidOperationException("请先选择并验证游戏安装目录。");
        if (!issue.TimeSeconds.HasValue)
            throw new InvalidOperationException("这个位置没有可渲染的谱面时间点。");
        if (Process.GetProcessesByName("ADOFAI VFX Worker").Length > 0)
            throw new InvalidOperationException("已有渲染任务正在运行，请完成或取消后再生成风险对照。");

        var baseWorker = RenderWorkerClient.FindStandaloneWorker()
                         ?? throw new FileNotFoundException("找不到独立 Unity Worker。");
        RenderWorkerClient.ValidateStandaloneWorker(baseWorker, installation.RootDirectory);
        var installedWorker = RenderWorkerClient.EnsureGameResourceWorker(
            baseWorker,
            installation.DataDirectory,
            placeholderMaximumSide: 0);

        var chartDirectory = Path.GetDirectoryName(chart.Path)!;
        var diagnosticRoot = CreateDiagnosticRoot(chartDirectory, singleFrame: true);

        var firstPlayableFloorSeconds = (double)DerivedChartExporter.CalculateRecommendedRenderStartSeconds(chart.Path);
        var sampleTime = issue.TimeSeconds.Value + (issue.RequiresContinuousFrames ? 3d : 0.001d);
        var startTime = Math.Max(0d, issue.TimeSeconds.Value - (issue.RequiresContinuousFrames ? 3d : 0.25d));
        if (options.PreRollSeconds.HasValue)
            startTime = Math.Max(0d, issue.TimeSeconds.Value - options.PreRollSeconds.Value);
        var durationSeconds = Math.Max(1d, sampleTime - startTime + 0.25d);
        var backgroundStartTime = Math.Max(0d, Math.Min(startTime, firstPlayableFloorSeconds));
        var backgroundDurationSeconds = Math.Max(1d, sampleTime - backgroundStartTime + 0.25d);
        var backgroundDirectory = Path.Combine(diagnosticRoot, "candidate-background");
        var candidateDirectory = Path.Combine(diagnosticRoot, "candidate-final");
        var framePath = Path.Combine(candidateDirectory, "candidate-frame.jpg");
        var temporaryDerivedChart = Path.Combine(
            chartDirectory,
            $".ADOFAIVfxRenderer-risk-{Guid.NewGuid():N}.adofai");

        try
        {
            WorkerRunResult candidate;
            if (options.DiagnosticLabel.StartsWith("VideoProFallback", StringComparison.Ordinal))
            {
                progress?.Report("正在用原版完整合成生成这一帧……");
                candidate = await RunWorkerAsync(
                    installedWorker,
                    installation,
                    chart.Path,
                    candidateDirectory,
                    startTime,
                    durationSeconds,
                    installedRuntime: true,
                    profile: OutputProfile.VideoPro,
                    content: OutputProfiles.Get(OutputProfile.VideoPro).VideoContent,
                    retainedDecorationIndices: new HashSet<int>(),
                    excludedDecorationIndices: new HashSet<int>(),
                    bakedFilterKeys: new HashSet<string>(StringComparer.Ordinal));
            }
            else
            {
                progress?.Report($"正在渲染 {OutputProfiles.Get(options.Profile).DisplayName} 视频层……");
                var content = OutputProfiles.Get(options.Profile).VideoContent;
                var background = await RunWorkerAsync(
                    installedWorker,
                    installation,
                    chart.Path,
                    backgroundDirectory,
                    backgroundStartTime,
                    backgroundDurationSeconds,
                    installedRuntime: false,
                    profile: options.Profile,
                    content: content,
                    retainedDecorationIndices: options.RetainedDecorationIndices,
                    excludedDecorationIndices: options.ExcludedDecorationIndices,
                    bakedFilterKeys: options.BakedFilterKeys);

                var retained = new HashSet<int>(options.RetainedDecorationIndices);
                retained.UnionWith(background.UnbakedDecorationIndices);
                var renderStartAfterFirstPlayableFloor =
                    (decimal)backgroundStartTime - (decimal)firstPlayableFloorSeconds;
                DerivedChartExporter.Export(
                    chart.Path,
                    background.VideoPath,
                    options.Profile,
                    renderStartAfterFirstPlayableFloor,
                    retained,
                    options.ExcludedDecorationIndices,
                    options.DisabledFilterKeys,
                    temporaryDerivedChart);

                progress?.Report("正在用原版运行时合成视频、轨道和保留层……");
                candidate = await RunWorkerAsync(
                    installedWorker,
                    installation,
                    temporaryDerivedChart,
                    candidateDirectory,
                    startTime,
                    durationSeconds,
                    installedRuntime: true,
                    profile: OutputProfile.VideoPro,
                    content: OutputProfiles.Get(OutputProfile.VideoPro).VideoContent,
                    retainedDecorationIndices: new HashSet<int>(),
                    excludedDecorationIndices: new HashSet<int>(),
                    bakedFilterKeys: new HashSet<string>(StringComparer.Ordinal));
            }

            progress?.Report("正在提取最终合成画面……");
            var ffmpeg = Path.Combine(installation.RootDirectory, "ffmpeg", "ffmpeg.exe");
            await ExtractSingleFrameAsync(ffmpeg, candidate.VideoPath, framePath, sampleTime - startTime);
            return new VisualRiskCandidateFrameResult(sampleTime, framePath, candidate.VideoPath);
        }
        finally
        {
            if (File.Exists(temporaryDerivedChart)) File.Delete(temporaryDerivedChart);
        }
    }

    public static async Task<VisualRiskDiagnosticResult> GenerateAsync(
        GameInstallation installation,
        ChartMetadata chart,
        VisualRiskIssue issue,
        VisualRiskDiagnosticOptions options,
        IProgress<string>? progress = null)
    {
        if (!installation.CanRender || installation.RootDirectory is null || installation.DataDirectory is null)
            throw new InvalidOperationException("请先选择并验证游戏安装目录。");
        if (!issue.RequiresComparison || !issue.TimeSeconds.HasValue)
            throw new InvalidOperationException("这个风险项没有可渲染的时间点，应直接修复或明确接受风险。");
        if (Process.GetProcessesByName("ADOFAI VFX Worker").Length > 0)
            throw new InvalidOperationException("已有渲染任务正在运行，请完成或取消后再生成风险对照。");

        var baseWorker = RenderWorkerClient.FindStandaloneWorker()
                         ?? throw new FileNotFoundException("找不到独立 Unity Worker。");
        RenderWorkerClient.ValidateStandaloneWorker(baseWorker, installation.RootDirectory);
        var installedWorker = RenderWorkerClient.EnsureGameResourceWorker(
            baseWorker,
            installation.DataDirectory,
            placeholderMaximumSide: 0);

        var chartDirectory = Path.GetDirectoryName(chart.Path)!;
        var diagnosticRoot = CreateDiagnosticRoot(chartDirectory, singleFrame: false);

        var firstPlayableFloorSeconds = (double)DerivedChartExporter.CalculateRecommendedRenderStartSeconds(chart.Path);
        var warmup = options.PreRollSeconds ?? (issue.RequiresContinuousFrames ? 3d : 0.5d);
        var startTime = Math.Max(0d, issue.TimeSeconds.Value - warmup);
        var effectiveWarmup = issue.TimeSeconds.Value - startTime;
        var requiredPostRiskSeconds = issue.Kind == VisualRiskKind.HistoryFeedback ? 10d : 1d;
        var durationSeconds = Math.Clamp(
            Math.Max(options.DurationSeconds, effectiveWarmup + requiredPostRiskSeconds),
            1d,
            60d);
        var backgroundStartTime = Math.Max(0d, Math.Min(startTime, firstPlayableFloorSeconds));
        var backgroundDurationSeconds = Math.Max(
            1d,
            startTime + durationSeconds - backgroundStartTime);
        var referenceDirectory = Path.Combine(diagnosticRoot, "reference-original");
        var backgroundDirectory = Path.Combine(diagnosticRoot, "candidate-background");
        var candidateDirectory = Path.Combine(diagnosticRoot, "candidate-final");
        var temporaryDerivedChart = Path.Combine(
            chartDirectory,
            $".ADOFAIVfxRenderer-risk-{Guid.NewGuid():N}.adofai");
        try
        {
            progress?.Report("正在用原版场景渲染风险位置参考片段……");
            var reference = await RunWorkerAsync(
                installedWorker,
                installation,
                chart.Path,
                referenceDirectory,
                startTime,
                durationSeconds,
                installedRuntime: true,
                profile: OutputProfile.VideoPro,
                content: OutputProfiles.Get(OutputProfile.VideoPro).VideoContent,
                retainedDecorationIndices: new HashSet<int>(),
                excludedDecorationIndices: new HashSet<int>(),
                bakedFilterKeys: new HashSet<string>(StringComparer.Ordinal));

            progress?.Report($"正在渲染 {OutputProfiles.Get(options.Profile).DisplayName} 视频层……");
            var content = OutputProfiles.Get(options.Profile).VideoContent;
            var background = await RunWorkerAsync(
                installedWorker,
                installation,
                chart.Path,
                backgroundDirectory,
                backgroundStartTime,
                backgroundDurationSeconds,
                installedRuntime: false,
                profile: options.Profile,
                content: content,
                retainedDecorationIndices: options.RetainedDecorationIndices,
                excludedDecorationIndices: options.ExcludedDecorationIndices,
                bakedFilterKeys: options.BakedFilterKeys);

            var retained = new HashSet<int>(options.RetainedDecorationIndices);
            retained.UnionWith(background.UnbakedDecorationIndices);
            // The exporter receives seconds after the first playable floor, while
            // diagnostic workers receive an absolute song time. Mixing these two
            // clocks shifts the temporary video by the entire countdown/first-tile
            // interval and makes a correct candidate look visually wrong.
            var renderStartAfterFirstPlayableFloor =
                (decimal)backgroundStartTime - (decimal)firstPlayableFloorSeconds;
            DerivedChartExporter.Export(
                chart.Path,
                background.VideoPath,
                options.Profile,
                renderStartAfterFirstPlayableFloor,
                retained,
                options.ExcludedDecorationIndices,
                options.DisabledFilterKeys,
                temporaryDerivedChart);

            progress?.Report("正在用原版场景载入派生谱，生成最终拆层合成……");
            var candidate = await RunWorkerAsync(
                installedWorker,
                installation,
                temporaryDerivedChart,
                candidateDirectory,
                startTime,
                durationSeconds,
                installedRuntime: true,
                profile: OutputProfile.VideoPro,
                content: OutputProfiles.Get(OutputProfile.VideoPro).VideoContent,
                retainedDecorationIndices: new HashSet<int>(),
                excludedDecorationIndices: new HashSet<int>(),
                bakedFilterKeys: new HashSet<string>(StringComparer.Ordinal));

            progress?.Report("正在提取十组同时间点画面并计算差异……");
            var ffmpeg = Path.Combine(installation.RootDirectory, "ffmpeg", "ffmpeg.exe");
            var comparisonStartOffset = Math.Max(0d, issue.TimeSeconds.Value - startTime - 3d);
            var comparisonDuration = Math.Min(10d, durationSeconds - comparisonStartOffset);
            var frameCount = Math.Max(1, Math.Min(10, (int)Math.Ceiling(comparisonDuration)));
            await ExtractFramesAsync(
                ffmpeg,
                reference.VideoPath,
                referenceDirectory,
                frameCount,
                comparisonStartOffset);
            await ExtractFramesAsync(
                ffmpeg,
                candidate.VideoPath,
                candidateDirectory,
                frameCount,
                comparisonStartOffset);
            var similarity = await CalculateSimilarityAsync(
                ffmpeg,
                reference.VideoPath,
                candidate.VideoPath,
                comparisonStartOffset,
                comparisonDuration);
            var frames = Enumerable.Range(1, frameCount)
                .Select(frame => new VisualRiskComparisonFilePair(
                    startTime + comparisonStartOffset + frame - 1,
                    Path.Combine(referenceDirectory, $"frame-{frame:00}.jpg"),
                    Path.Combine(candidateDirectory, $"frame-{frame:00}.jpg")))
                .ToArray();
            return new VisualRiskDiagnosticResult(
                frames,
                reference.VideoPath,
                candidate.VideoPath,
                similarity);
        }
        finally
        {
            if (File.Exists(temporaryDerivedChart)) File.Delete(temporaryDerivedChart);
        }
    }

    private static async Task<WorkerRunResult> RunWorkerAsync(
        string worker,
        GameInstallation installation,
        string chartPath,
        string outputDirectory,
        double startTime,
        double durationSeconds,
        bool installedRuntime,
        OutputProfile profile,
        RenderContent content,
        IReadOnlySet<int> retainedDecorationIndices,
        IReadOnlySet<int> excludedDecorationIndices,
        IReadOnlySet<string> bakedFilterKeys,
        int? diagnoseRenderFrame = null)
    {
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "comparison.mp4");
        var statusPath = Path.Combine(outputDirectory, "render-status.json");
        var previewDirectory = Path.Combine(outputDirectory, "preview");
        Directory.CreateDirectory(previewDirectory);
        var job = new WorkerJob
        {
            JobId = Guid.NewGuid().ToString("N"),
            ChartPath = chartPath,
            OutputPath = outputPath,
            StatusPath = statusPath,
            PreviewDirectory = previewDirectory,
            Profile = profile.ToString(),
            Width = Width,
            Height = Height,
            RenderWidth = Width,
            RenderHeight = Height,
            FramesPerSecond = FramesPerSecond,
            TailSeconds = 0,
            IncludeTrack = content.HasFlag(RenderContent.Track),
            IncludePlanets = content.HasFlag(RenderContent.Planets),
            IncludeFilters = bakedFilterKeys.Count > 0 || content.HasFlag(RenderContent.Filters),
            BakedFilterKeys = bakedFilterKeys.OrderBy(static key => key, StringComparer.Ordinal).ToArray(),
            IncludeBackgroundDecorations = content.HasFlag(RenderContent.BackgroundDecorations),
            IncludeForegroundDecorations = content.HasFlag(RenderContent.ForegroundDecorations),
            RetainedDecorationIndices = retainedDecorationIndices.OrderBy(static index => index).ToArray(),
            ExcludedDecorationIndices = excludedDecorationIndices.OrderBy(static index => index).ToArray(),
            BitrateKbps = 2500,
            DurationLimitSeconds = (float)durationSeconds,
            RenderStartSeconds = (float)startTime,
            FfmpegPath = Path.Combine(installation.RootDirectory!, "ffmpeg", "ffmpeg.exe"),
            GameManagedDirectory = Path.Combine(installation.DataDirectory!, "Managed"),
        };
        var jobPath = Path.Combine(outputDirectory, "render-job.json");
        File.WriteAllText(jobPath, JsonSerializer.Serialize(job, JsonOptions));
        var logPath = Path.Combine(outputDirectory, "worker.log");
        var arguments = $"-batchmode -logFile \"{logPath}\" --job \"{jobPath}\"" +
                        (installedRuntime ? $" --render-installed-game --chart \"{chartPath}\"" : "") +
                        (diagnoseRenderFrame.HasValue ? $" --diagnose-render-frame {diagnoseRenderFrame.Value}" : "") +
                        $" -screen-fullscreen 0 -screen-width {Width} -screen-height {Height}";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = worker,
            WorkingDirectory = installedRuntime
                ? installation.RootDirectory
                : Path.GetDirectoryName(worker),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = arguments,
        }) ?? throw new InvalidOperationException("无法启动风险对照 Worker。");
        await process.WaitForExitAsync();
        WorkerStatus? status = null;
        if (File.Exists(statusPath))
        {
            try
            {
                status = JsonSerializer.Deserialize<WorkerStatus>(
                    File.ReadAllText(statusPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException)
            {
                // The status text is included verbatim in the failure below.
            }
        }
        if (process.ExitCode != 0 || status?.State != "complete" || !File.Exists(outputPath))
        {
            var error = File.Exists(statusPath) ? File.ReadAllText(statusPath) : $"退出代码 {process.ExitCode}";
            var logTail = File.Exists(logPath)
                ? string.Join(Environment.NewLine, File.ReadLines(logPath).TakeLast(20))
                : "";
            throw new InvalidOperationException(
                "风险对照渲染失败：" + error +
                (logTail.Length > 0 ? Environment.NewLine + logTail : ""));
        }
        return new WorkerRunResult(outputPath, status.UnbakedDecorationIndices);
    }

    private static async Task ExtractFramesAsync(
        string ffmpeg,
        string videoPath,
        string outputDirectory,
        int frameCount,
        double startOffsetSeconds)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            WorkingDirectory = outputDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-ss", startOffsetSeconds.ToString("0.#########", CultureInfo.InvariantCulture),
                     "-i", videoPath,
                     "-vf", "fps=1", "-frames:v", frameCount.ToString(CultureInfo.InvariantCulture), "-c:v", "mjpeg",
                     Path.Combine(outputDirectory, "frame-%02d.jpg"),
                 })
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法启动 FFmpeg 来提取风险对照图。");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg 提取风险对照图失败，退出代码 {process.ExitCode}。");
    }

    private static string CreateDiagnosticRoot(string chartDirectory, bool singleFrame)
    {
        var cacheRoot = Path.Combine(chartDirectory, ".vfxd");
        var diagnosticRoot = Path.Combine(
            cacheRoot,
            $"{DateTime.Now:yyyyMMddHHmmss}-{(singleFrame ? "f" : "c")}-{Guid.NewGuid():N}"[..26]);
        Directory.CreateDirectory(diagnosticRoot);
        PreRenderCacheRegistry.Register(diagnosticRoot);
        return diagnosticRoot;
    }

    private static async Task ExtractSingleFrameAsync(
        string ffmpeg,
        string videoPath,
        string outputPath,
        double offsetSeconds)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            WorkingDirectory = Path.GetDirectoryName(outputPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-ss", Math.Max(0d, offsetSeconds).ToString("0.#########", CultureInfo.InvariantCulture),
                     "-i", videoPath,
                     "-frames:v", "1", "-c:v", "mjpeg", outputPath,
                 })
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法启动 FFmpeg 来提取风险对照图。");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 || !File.Exists(outputPath))
            throw new InvalidOperationException($"FFmpeg 提取风险对照图失败，退出代码 {process.ExitCode}。");
    }

    private static async Task<double?> CalculateSimilarityAsync(
        string ffmpeg,
        string referenceVideo,
        string candidateVideo,
        double startOffsetSeconds,
        double durationSeconds)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner",
                     "-ss", startOffsetSeconds.ToString("0.#########", CultureInfo.InvariantCulture),
                     "-i", referenceVideo,
                     "-ss", startOffsetSeconds.ToString("0.#########", CultureInfo.InvariantCulture),
                     "-i", candidateVideo,
                     "-t", durationSeconds.ToString("0.#########", CultureInfo.InvariantCulture),
                     "-lavfi", "[0:v][1:v]ssim", "-f", "null", "NUL",
                 })
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法启动 FFmpeg 来计算风险对照差异。");
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await errorTask;
        if (process.ExitCode != 0) return null;
        var match = SsimPattern().Match(output);
        return match.Success && double.TryParse(
            match.Groups[1].Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private sealed record WorkerRunResult(string VideoPath, int[] UnbakedDecorationIndices);

    [GeneratedRegex("All:([0-9.]+)")]
    private static partial Regex SsimPattern();

    [GeneratedRegex("[^A-Za-z0-9._-]+")]
    private static partial Regex InvalidFileName();
}
