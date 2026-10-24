using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using ADOFAIVfxRenderer.Core;

namespace ADOFAIVfxRenderer.App;

internal sealed class RenderWorkerClient : IDisposable
{
    private readonly DispatcherTimer _timer;
    private Process? _workerProcess;
    private string? _statusPath;
    private string? _lastStatusJson;

    public RenderWorkerClient()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _timer.Tick += PollStatus;
    }

    public event Action<WorkerStatus>? StatusChanged;

    public bool IsRunning => _timer.IsEnabled;

    public string Start(
        GameInstallation installation,
        ChartMetadata chart,
        RenderVariant variant,
        TailExtension tail,
        decimal? durationLimitSeconds = null)
    {
        if (!installation.CanRender || installation.RootDirectory is null)
        {
            throw new InvalidOperationException("所选游戏安装尚未通过兼容性验证。");
        }

        if (Process.GetProcessesByName("ADOFAI VFX Worker").Length > 0)
        {
            throw new InvalidOperationException("已有一个 ADOFAI VFX Worker 正在渲染。");
        }

        if (variant.Profile != OutputProfile.VideoVfx)
        {
            throw new NotSupportedException("首个测试 Worker 目前只支持单个 Video VFX 输出。");
        }

        var workerExecutable = FindStandaloneWorker();
        if (workerExecutable is null)
        {
            throw new FileNotFoundException(
                "找不到独立 Unity Worker。不会回退为启动游戏；请先构建 ADOFAIVfxRenderer.UnityWorker。",
                "ADOFAI VFX Worker.exe");
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var chartDirectory = Path.GetDirectoryName(chart.Path)!;
        var outputDirectory = Path.Combine(chartDirectory, "ADOFAIVfxRenderer Output", timestamp);
        var previewDirectory = Path.Combine(outputDirectory, "preview");
        Directory.CreateDirectory(previewDirectory);
        var chartName = Path.GetFileNameWithoutExtension(chart.Path);
        var outputPath = Path.Combine(
            outputDirectory,
            $"{chartName}-VideoVFX-{variant.Resolution.Name}-{variant.FramesPerSecond}fps.mp4");
        _statusPath = Path.Combine(outputDirectory, "render-status.json");

        var job = new WorkerJob
        {
            JobId = Guid.NewGuid().ToString("N"),
            ChartPath = chart.Path,
            OutputPath = outputPath,
            StatusPath = _statusPath,
            PreviewDirectory = previewDirectory,
            Profile = "VideoVfx",
            Width = variant.Resolution.Width,
            Height = variant.Resolution.Height,
            FramesPerSecond = variant.FramesPerSecond,
            TailSeconds = (float)tail.Seconds,
            IncludeTrack = variant.Content.HasFlag(RenderContent.Track),
            IncludePlanets = variant.Content.HasFlag(RenderContent.Planets),
            IncludeEffects = variant.Content.HasFlag(RenderContent.Effects),
            IncludeBackgroundDecorations = variant.Content.HasFlag(RenderContent.BackgroundDecorations),
            IncludeForegroundDecorations = variant.Content.HasFlag(RenderContent.ForegroundDecorations),
            BitrateKbps = variant.BitrateKbps,
            DurationLimitSeconds = durationLimitSeconds.HasValue ? (float)durationLimitSeconds.Value : 0f,
            FfmpegPath = Path.Combine(installation.RootDirectory, "ffmpeg", "ffmpeg.exe"),
        };
        var jobPath = Path.Combine(outputDirectory, "render-job.json");
        File.WriteAllText(jobPath, JsonSerializer.Serialize(job, JsonOptions));
        _lastStatusJson = null;

        var startInfo = new ProcessStartInfo
        {
            FileName = workerExecutable,
            WorkingDirectory = Path.GetDirectoryName(workerExecutable),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"--job \"{jobPath}\" -screen-fullscreen 0 -screen-width {variant.Resolution.Width} -screen-height {variant.Resolution.Height}",
        };
        _workerProcess = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动独立 Unity Worker。");
        _timer.Start();
        StatusChanged?.Invoke(new WorkerStatus
        {
            State = "starting",
            Message = "正在启动独立 Unity Worker（不会启动游戏）……",
            OutputPath = outputPath,
        });
        return outputPath;
    }

    public void Cancel()
    {
        _timer.Stop();
        if (_workerProcess is { HasExited: false })
        {
            _workerProcess.Kill(entireProcessTree: true);
        }

        StatusChanged?.Invoke(new WorkerStatus
        {
            State = "cancelled",
            Message = "渲染已取消。",
        });
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= PollStatus;
        _workerProcess?.Dispose();
    }

    private void PollStatus(object? sender, EventArgs e)
    {
        try
        {
            if (_statusPath is not null && File.Exists(_statusPath))
            {
                string json;
                using (var stream = new FileStream(_statusPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    json = reader.ReadToEnd();
                }

                if (!string.IsNullOrWhiteSpace(json) && json != _lastStatusJson)
                {
                    var status = JsonSerializer.Deserialize<WorkerStatus>(json, JsonOptions);
                    if (status != null)
                    {
                        _lastStatusJson = json;
                        StatusChanged?.Invoke(status);
                        if (status.State is "complete" or "failed" or "cancelled")
                        {
                            _timer.Stop();
                        }
                    }
                }
            }

            if (_timer.IsEnabled && _workerProcess is { HasExited: true } &&
                (_lastStatusJson is null || !(_lastStatusJson.Contains("\"complete\"") || _lastStatusJson.Contains("\"failed\""))))
            {
                _timer.Stop();
                StatusChanged?.Invoke(new WorkerStatus
                {
                    State = "failed",
                    Message = $"独立 Unity Worker 提前退出，代码 {_workerProcess.ExitCode}。",
                    Error = "请检查独立 Worker 的 Player.log。",
                });
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // Worker 可能正在原子替换状态文件；下一次轮询会重新读取。
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static string? FindStandaloneWorker()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "UnityWorker", "ADOFAI VFX Worker.exe");
        if (File.Exists(packaged))
        {
            return packaged;
        }

        var development = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "artifacts", "UnityWorker", "ADOFAI VFX Worker.exe"));
        return File.Exists(development) ? development : null;
    }
}

internal sealed class WorkerJob
{
    [JsonPropertyName("jobId")]
    public string JobId { get; init; } = "";

    [JsonPropertyName("chartPath")]
    public string ChartPath { get; init; } = "";

    [JsonPropertyName("outputPath")]
    public string OutputPath { get; init; } = "";

    [JsonPropertyName("statusPath")]
    public string StatusPath { get; init; } = "";

    [JsonPropertyName("previewDirectory")]
    public string PreviewDirectory { get; init; } = "";

    [JsonPropertyName("profile")]
    public string Profile { get; init; } = "";

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("framesPerSecond")]
    public int FramesPerSecond { get; init; }

    [JsonPropertyName("tailSeconds")]
    public float TailSeconds { get; init; }

    [JsonPropertyName("includeTrack")]
    public bool IncludeTrack { get; init; }

    [JsonPropertyName("includePlanets")]
    public bool IncludePlanets { get; init; }

    [JsonPropertyName("includeEffects")]
    public bool IncludeEffects { get; init; }

    [JsonPropertyName("includeBackgroundDecorations")]
    public bool IncludeBackgroundDecorations { get; init; }

    [JsonPropertyName("includeForegroundDecorations")]
    public bool IncludeForegroundDecorations { get; init; }

    [JsonPropertyName("bitrateKbps")]
    public int BitrateKbps { get; init; }

    [JsonPropertyName("durationLimitSeconds")]
    public float DurationLimitSeconds { get; init; }

    [JsonPropertyName("ffmpegPath")]
    public string FfmpegPath { get; init; } = "";
}

internal sealed class WorkerStatus
{
    [JsonPropertyName("jobId")]
    public string JobId { get; init; } = "";

    [JsonPropertyName("state")]
    public string State { get; init; } = "";

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("outputPath")]
    public string OutputPath { get; init; } = "";

    [JsonPropertyName("previewPath")]
    public string PreviewPath { get; init; } = "";

    [JsonPropertyName("error")]
    public string Error { get; init; } = "";

    [JsonPropertyName("currentFrame")]
    public int CurrentFrame { get; init; }

    [JsonPropertyName("totalFrames")]
    public int TotalFrames { get; init; }

    [JsonPropertyName("percent")]
    public float Percent { get; init; }

    [JsonPropertyName("renderedFramesPerSecond")]
    public float RenderedFramesPerSecond { get; init; }

    [JsonPropertyName("secondsPerFrame")]
    public float SecondsPerFrame { get; init; }
}
