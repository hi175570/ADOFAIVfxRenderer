using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ADOFAIVfxRenderer.Core;

namespace ADOFAIVfxRenderer.App;

internal sealed record DecorationComparisonFilePair(
    double TimeSeconds,
    string WithDecorationPath,
    string WithoutDecorationPath);

internal static class DecorationDiagnosticClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<IReadOnlyList<DecorationComparisonFilePair>> GenerateAsync(
        GameInstallation installation,
        ChartMetadata chart,
        DecorationCatalogItem decoration,
        bool includeTrack,
        bool includePlanets,
        bool includeEffects,
        IProgress<string>? progress = null)
    {
        if (!installation.CanRender || installation.RootDirectory is null)
            throw new InvalidOperationException("请先选择并验证游戏安装目录。");
        if (Process.GetProcessesByName("ADOFAI VFX Worker").Length > 0)
            throw new InvalidOperationException("已有渲染任务正在运行，请完成或取消后再生成装饰对照。");

        var worker = RenderWorkerClient.FindStandaloneWorker()
                     ?? throw new FileNotFoundException("找不到独立 Unity Worker。");
        RenderWorkerClient.ValidateStandaloneWorker(worker, installation.RootDirectory);

        var chartDirectory = Path.GetDirectoryName(chart.Path)!;
        var diagnosticRoot = Path.Combine(
            chartDirectory,
            "ADOFAIVfxRenderer Diagnostics",
            $"{DateTime.Now:yyyyMMdd-HHmmss}-decoration-{decoration.Index}");
        Directory.CreateDirectory(diagnosticRoot);
        PreRenderCacheRegistry.Register(diagnosticRoot);
        var startTime = Math.Max(0d, decoration.TimeSeconds + 0.001d);

        progress?.Report("正在渲染包含此贴图的 10 个时间点……");
        var withDirectory = Path.Combine(diagnosticRoot, "with-decoration");
        var withVideo = await RunWorkerAsync(
            worker,
            installation.RootDirectory,
            chart.Path,
            withDirectory,
            startTime,
            includeTrack,
            includePlanets,
            includeEffects,
            []);

        progress?.Report("正在渲染不包含此贴图的同一组时间点……");
        var withoutDirectory = Path.Combine(diagnosticRoot, "without-decoration");
        var withoutVideo = await RunWorkerAsync(
            worker,
            installation.RootDirectory,
            chart.Path,
            withoutDirectory,
            startTime,
            includeTrack,
            includePlanets,
            includeEffects,
            [decoration.Index]);

        progress?.Report("正在整理 10 组同帧对照……");
        var ffmpeg = Path.Combine(installation.RootDirectory, "ffmpeg", "ffmpeg.exe");
        await ExtractFramesAsync(ffmpeg, withVideo, withDirectory);
        await ExtractFramesAsync(ffmpeg, withoutVideo, withoutDirectory);

        return Enumerable.Range(1, 10)
            .Select(frame => new DecorationComparisonFilePair(
                startTime + frame - 1,
                Path.Combine(withDirectory, $"frame-{frame:00}.jpg"),
                Path.Combine(withoutDirectory, $"frame-{frame:00}.jpg")))
            .ToArray();
    }

    private static async Task<string> RunWorkerAsync(
        string worker,
        string gameDirectory,
        string chartPath,
        string outputDirectory,
        double startTime,
        bool includeTrack,
        bool includePlanets,
        bool includeEffects,
        int[] retainedDecorationIndices)
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
            Profile = nameof(OutputProfile.VideoVfx),
            Width = 640,
            Height = 360,
            FramesPerSecond = 1,
            TailSeconds = 0,
            IncludeTrack = includeTrack,
            IncludePlanets = includePlanets,
            IncludeEffects = includeEffects,
            IncludeBackgroundDecorations = true,
            IncludeForegroundDecorations = true,
            BitrateKbps = 1200,
            DurationLimitSeconds = 10,
            RenderStartSeconds = (float)startTime,
            FfmpegPath = Path.Combine(gameDirectory, "ffmpeg", "ffmpeg.exe"),
            RetainedDecorationIndices = retainedDecorationIndices,
        };
        var jobPath = Path.Combine(outputDirectory, "render-job.json");
        File.WriteAllText(jobPath, JsonSerializer.Serialize(job, JsonOptions));
        var logPath = Path.Combine(outputDirectory, "worker.log");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = worker,
            WorkingDirectory = Path.GetDirectoryName(worker),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"-batchmode -logFile \"{logPath}\" --job \"{jobPath}\" -screen-fullscreen 0 -screen-width 640 -screen-height 360",
        }) ?? throw new InvalidOperationException("无法启动装饰对照 Worker。");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 || !File.Exists(outputPath))
        {
            var error = File.Exists(statusPath) ? File.ReadAllText(statusPath) : $"退出代码 {process.ExitCode}";
            throw new InvalidOperationException("装饰对照渲染失败：" + error);
        }
        return outputPath;
    }

    private static async Task ExtractFramesAsync(string ffmpeg, string videoPath, string outputDirectory)
    {
        var outputPattern = Path.Combine(outputDirectory, "frame-%02d.jpg");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = ffmpeg,
            WorkingDirectory = outputDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"-hide_banner -loglevel error -y -i \"{videoPath}\" -vsync 0 -c:v mjpeg \"{outputPattern}\"",
        }) ?? throw new InvalidOperationException("无法启动 FFmpeg 来整理对照图片。");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg 整理对照图片失败，退出代码 {process.ExitCode}。");
    }
}
