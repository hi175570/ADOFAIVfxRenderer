using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
    private string? _activeOutputPath;
    private string? _lastStatusJson;
    private string? _activeChartPath;
    private OutputProfile _activeProfile;
    private decimal _activeRenderStartSeconds;
    private IReadOnlySet<int> _activeRetainedDecorationIndices = new HashSet<int>();
    private IReadOnlySet<int> _activeExcludedDecorationIndices = new HashSet<int>();
    private IReadOnlySet<string> _activeDisabledFilterKeys = new HashSet<string>();
    private bool _derivedExportAttempted;
    private bool _skipDerivedExport;
    private long _activeMemoryLimitBytes;
    private long _activeEstimatedTextureBytes;
    private int _activeTextureCount;
    private int _activePlaceholderMaximumSide;
    private string? _temporaryRenderChartPath;
    private string _activeJobId = "";

    public RenderWorkerClient()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _timer.Tick += PollStatus;
    }

    public event Action<WorkerStatus>? StatusChanged;

    public bool IsRunning => _timer.IsEnabled;

    public async Task WaitForExitAsync()
    {
        if (_workerProcess is { HasExited: false }) await _workerProcess.WaitForExitAsync();
    }

    public string Start(
        GameInstallation installation,
        ChartMetadata chart,
        RenderVariant variant,
        TailExtension tail,
        decimal? durationLimitSeconds = null,
        IReadOnlySet<int>? retainedDecorationIndices = null,
        IReadOnlySet<int>? excludedDecorationIndices = null,
        IReadOnlySet<string>? bakedFilterKeys = null,
        IReadOnlySet<string>? disabledFilterKeys = null,
        RenderBackend renderBackend = RenderBackend.Automatic,
        int memoryLimitMb = 0,
        bool finalCompositeVerification = false)
    {
        if (!installation.CanRender || installation.RootDirectory is null)
        {
            throw new InvalidOperationException("所选游戏安装尚未通过兼容性验证。");
        }

        if (Process.GetProcessesByName("ADOFAI VFX Worker").Length > 0)
        {
            throw new InvalidOperationException("已有一个 ADOFAI VFX Worker 正在渲染。");
        }

        var workerExecutable = FindStandaloneWorker();
        if (workerExecutable is null)
        {
            throw new FileNotFoundException(
                "找不到独立 Unity Worker。不会回退为启动游戏；请先构建 ADOFAIVfxRenderer.UnityWorker。",
                "ADOFAI VFX Worker.exe");
        }

        ValidateStandaloneWorker(workerExecutable, installation.RootDirectory);
        var textureBudgetPlan = memoryLimitMb > 0
            ? TextureBudgetCalculator.Calculate(chart.Path, memoryLimitMb)
            : null;
        var placeholderMaximumSide = textureBudgetPlan?.PlaceholderMaximumSide ?? 0;
        workerExecutable = EnsureGameResourceWorker(
            workerExecutable,
            installation.DataDirectory ?? throw new InvalidOperationException("游戏安装缺少 Data 目录。"),
            placeholderMaximumSide);
        var ffmpegPath = Path.Combine(installation.RootDirectory, "ffmpeg", "ffmpeg.exe");
        var resolvedBackend = ResolveBackend(renderBackend, ffmpegPath);
        var usesOfflineFilters = variant.Content.HasFlag(RenderContent.Filters) || bakedFilterKeys?.Count > 0;
        var internalWidth = variant.Resolution.Width;
        var internalHeight = variant.Resolution.Height;
        if (usesOfflineFilters && internalHeight < 1080)
        {
            internalHeight = 1080;
            internalWidth = (int)Math.Round(internalHeight * variant.Resolution.Width / (double)variant.Resolution.Height);
        }

        var renderStartSeconds = DerivedChartExporter.CalculateRecommendedRenderStartSeconds(chart.Path);
        CleanupTemporaryRenderChart();
        var renderChartPath = variant.Profile == OutputProfile.VideoPro
            ? RenderChartSanitizer.CreateTemporaryCopy(chart.Path, excludedDecorationIndices)
            : chart.Path;
        if (!Path.GetFullPath(renderChartPath).Equals(Path.GetFullPath(chart.Path), StringComparison.OrdinalIgnoreCase))
            _temporaryRenderChartPath = renderChartPath;
        var layout = RenderOutputLayout.Create(chart.Path, variant.Profile);
        if (finalCompositeVerification)
        {
            const string name = "Video VFX 最终合成验证";
            layout = layout with
            {
                VideoPath = Path.Combine(layout.OutputDirectory, name + ".mp4"),
                PreviewDirectory = Path.Combine(layout.OutputDirectory, ".preview", name),
                JobPath = Path.Combine(layout.OutputDirectory, $"render-job-{name}.json"),
                StatusPath = Path.Combine(layout.OutputDirectory, $"render-status-{name}.json"),
                WorkerLogPath = Path.Combine(layout.OutputDirectory, $"worker-{name}.log"),
            };
        }
        var outputDirectory = layout.OutputDirectory;
        var previewDirectory = layout.PreviewDirectory;
        Directory.CreateDirectory(previewDirectory);
        var outputPath = layout.VideoPath;
        _activeOutputPath = outputPath;
        _statusPath = layout.StatusPath;

        var job = new WorkerJob
        {
            JobId = Guid.NewGuid().ToString("N"),
            ChartPath = renderChartPath,
            OutputPath = outputPath,
            StatusPath = _statusPath,
            PreviewDirectory = previewDirectory,
            Profile = variant.Profile.ToString(),
            Width = variant.Resolution.Width,
            Height = variant.Resolution.Height,
            RenderWidth = internalWidth,
            RenderHeight = internalHeight,
            FramesPerSecond = variant.FramesPerSecond,
            TailSeconds = (float)tail.Seconds,
            IncludeTrack = variant.Content.HasFlag(RenderContent.Track),
            IncludePlanets = variant.Content.HasFlag(RenderContent.Planets),
            IncludeFilters = usesOfflineFilters,
            BakedFilterKeys = bakedFilterKeys?.OrderBy(static key => key, StringComparer.Ordinal).ToArray() ?? [],
            IncludeBackgroundDecorations = variant.Content.HasFlag(RenderContent.BackgroundDecorations),
            IncludeForegroundDecorations = variant.Content.HasFlag(RenderContent.ForegroundDecorations),
            RetainedDecorationIndices = retainedDecorationIndices?.OrderBy(static index => index).ToArray() ?? [],
            ExcludedDecorationIndices = excludedDecorationIndices?.OrderBy(static index => index).ToArray() ?? [],
            BitrateKbps = variant.BitrateKbps,
            DurationLimitSeconds = durationLimitSeconds.HasValue ? (float)durationLimitSeconds.Value : 0f,
            RenderStartSeconds = (float)renderStartSeconds,
            FfmpegPath = ffmpegPath,
            GameManagedDirectory = Path.Combine(
                installation.DataDirectory ?? throw new InvalidOperationException("游戏安装缺少 Data 目录。"),
                "Managed"),
            VideoEncoder = resolvedBackend.VideoEncoder,
            EncoderPreset = resolvedBackend.EncoderPreset,
            PlaceholderMaximumSide = placeholderMaximumSide,
            TextureMemoryBudgetBytes = memoryLimitMb > 0 ? memoryLimitMb * 1024L * 1024L : 0L,
        };
        var jobPath = layout.JobPath;
        if (File.Exists(_statusPath)) File.Delete(_statusPath);
        File.WriteAllText(jobPath, JsonSerializer.Serialize(job, JsonOptions));
        _lastStatusJson = null;
        _activeJobId = job.JobId;
        _activeChartPath = chart.Path;
        _activeProfile = variant.Profile;
        // The worker renders from the first playable floor. The exporter parameter is an
        // additional offset after that floor, so the normal value remains zero.
        _activeRenderStartSeconds = 0m;
        _activeRetainedDecorationIndices = retainedDecorationIndices is null
            ? new HashSet<int>()
            : new HashSet<int>(retainedDecorationIndices);
        _activeExcludedDecorationIndices = excludedDecorationIndices is null
            ? new HashSet<int>()
            : new HashSet<int>(excludedDecorationIndices);
        _activeDisabledFilterKeys = disabledFilterKeys is null
            ? new HashSet<string>()
            : new HashSet<string>(disabledFilterKeys, StringComparer.Ordinal);
        _derivedExportAttempted = false;
        _skipDerivedExport = finalCompositeVerification;
        _activeMemoryLimitBytes = memoryLimitMb > 0 ? memoryLimitMb * 1024L * 1024L : 0L;
        _activeEstimatedTextureBytes = textureBudgetPlan?.FullResolutionBytes ?? 0L;
        _activeTextureCount = textureBudgetPlan?.TextureCount ?? 0;
        _activePlaceholderMaximumSide = placeholderMaximumSide;

        var workerLogPath = layout.WorkerLogPath;
        var useInstalledGameRuntime = variant.Profile == OutputProfile.VideoPro;
        var installedRuntimeArguments = useInstalledGameRuntime
            ? $" --render-installed-game --chart \"{renderChartPath}\""
            : "";
        var memoryDiagnosticArguments = Environment.GetCommandLineArgs().Contains("--diagnose-memory")
            ? " --diagnose-memory"
            : "";
        var startInfo = new ProcessStartInfo
        {
            FileName = workerExecutable,
            WorkingDirectory = useInstalledGameRuntime
                ? installation.RootDirectory
                : Path.GetDirectoryName(workerExecutable),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            // Unity's normal player mode can create a visible top-level window even when
            // ProcessWindowStyle.Hidden is requested. Batch mode keeps the render device
            // available (unlike -nographics) without opening a second game-like window.
            Arguments = $"-batchmode -logFile \"{workerLogPath}\" --job \"{jobPath}\"{installedRuntimeArguments}{memoryDiagnosticArguments} -screen-fullscreen 0 -screen-width {internalWidth} -screen-height {internalHeight} {resolvedBackend.UnityArguments}",
        };
        _workerProcess = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动独立 Unity Worker。");
        _timer.Start();
        StatusChanged?.Invoke(new WorkerStatus
        {
            State = "starting",
            Message = $"正在启动独立 Unity Worker（{resolvedBackend.Description}；不会启动游戏）……",
            OutputPath = outputPath,
            MemoryLimitBytes = _activeMemoryLimitBytes,
            EstimatedTextureBytes = _activeEstimatedTextureBytes,
            TextureCount = _activeTextureCount,
            PlaceholderMaximumSide = _activePlaceholderMaximumSide,
        });
        return outputPath;
    }

    public void Cancel()
    {
        _timer.Stop();
        if (_workerProcess is { HasExited: false })
        {
            _workerProcess.Kill(entireProcessTree: true);
            _workerProcess.WaitForExit(5000);
        }
        CleanupTemporaryVideos(_activeOutputPath);

        StatusChanged?.Invoke(new WorkerStatus
        {
            State = "cancelled",
            Message = "渲染已取消。",
            OutputPath = _activeOutputPath ?? "",
        });
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= PollStatus;
        _workerProcess?.Dispose();
        CleanupTemporaryRenderChart();
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
                        if (!string.Equals(status.JobId, _activeJobId, StringComparison.Ordinal)) return;
                        status.MemoryLimitBytes = _activeMemoryLimitBytes;
                        status.EstimatedTextureBytes = _activeEstimatedTextureBytes;
                        status.TextureCount = _activeTextureCount;
                        status.PlaceholderMaximumSide = _activePlaceholderMaximumSide;
                        if (_workerProcess is { HasExited: false })
                        {
                            _workerProcess.Refresh();
                            status.WorkingSetBytes = _workerProcess.WorkingSet64;
                            status.PeakWorkingSetBytes = _workerProcess.PeakWorkingSet64;
                        }
                        if (status.State == "complete" && !_derivedExportAttempted)
                        {
                            _derivedExportAttempted = true;
                            if (_skipDerivedExport)
                            {
                                status.Message = "Video VFX 派生谱最终合成验证已完成；未再次生成派生谱。";
                            }
                            else
                            try
                            {
                                var retainedDecorationIndices = new HashSet<int>(_activeRetainedDecorationIndices);
                                retainedDecorationIndices.UnionWith(status.UnbakedDecorationIndices);
                                var derived = DerivedChartExporter.Export(
                                    _activeChartPath ?? throw new InvalidOperationException("缺少活动谱面路径。"),
                                    status.OutputPath,
                                    _activeProfile,
                                    _activeRenderStartSeconds,
                                    retainedDecorationIndices,
                                    _activeExcludedDecorationIndices,
                                    _activeDisabledFilterKeys);
                                status.DerivedChartPath = derived.ChartPath;
                                status.VideoOffsetMilliseconds = derived.VideoOffsetMilliseconds;
                                status.Message =
                                    $"视频与派生谱面已完成；视频偏移 {derived.VideoOffsetMilliseconds} ms；" +
                                    $"自动保留 {status.UnbakedDecorationIndices.Length} 个未成功烘焙的装饰；" +
                                    $"关闭 {derived.DisabledFilterEvents} 个已烤入或忽略的滤镜事件。";
                            }
                            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
                            {
                                status.Message = "视频已完成，但派生谱面导出失败。";
                                status.Error = exception.Message;
                            }
                        }
                        if (status.State is "complete" or "failed" or "cancelled")
                        {
                            _timer.Stop();
                            CleanupTemporaryRenderChart();
                            if (status.State is "failed" or "cancelled")
                                CleanupTemporaryVideos(status.OutputPath);
                        }
                        _lastStatusJson = json;
                        StatusChanged?.Invoke(status);
                    }
                }
            }

            if (_timer.IsEnabled && _workerProcess is { HasExited: true } &&
                (_lastStatusJson is null || !(_lastStatusJson.Contains("\"complete\"") || _lastStatusJson.Contains("\"failed\""))))
            {
                _timer.Stop();
                CleanupTemporaryVideos(_activeOutputPath);
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

    private void CleanupTemporaryRenderChart()
    {
        if (string.IsNullOrWhiteSpace(_temporaryRenderChartPath)) return;
        try
        {
            if (File.Exists(_temporaryRenderChartPath)) File.Delete(_temporaryRenderChartPath);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        _temporaryRenderChartPath = null;
    }

    private static void CleanupTemporaryVideos(string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) return;
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        var fileName = Path.GetFileNameWithoutExtension(fullPath);
        var extension = Path.GetExtension(fullPath);
        foreach (var temporaryPath in Directory.GetFiles(directory, $".{fileName}.*.rendering{extension}"))
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { /* the worker may still be releasing FFmpeg; startup also removes stale files */ }
            catch (UnauthorizedAccessException) { /* keep the canonical output untouched */ }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    internal static string? FindStandaloneWorker()
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

    internal static void ValidateStandaloneWorker(string workerExecutable, string gameRootDirectory)
    {
        var workerPath = Path.GetFullPath(workerExecutable);
        var gameRoot = Path.GetFullPath(gameRootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (workerPath.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("拒绝启动：渲染 Worker 位于游戏目录内，无法证明它不是游戏本体。");
        }

        var workerDirectory = Path.GetDirectoryName(workerPath)
            ?? throw new InvalidOperationException("无法确定独立 Worker 的目录。");
        var workerName = Path.GetFileNameWithoutExtension(workerPath);
        var managedDirectory = Path.Combine(workerDirectory, $"{workerName}_Data", "Managed");
        var workerAssembly = Path.Combine(managedDirectory, "ADOFAIVfxWorker.Runtime.dll");
        var gameAssembly = Path.Combine(managedDirectory, "Assembly-CSharp.dll");

        if (!File.Exists(workerAssembly))
        {
            throw new InvalidOperationException(
                "拒绝启动：目标 Player 不包含 ADOFAIVfxWorker.Runtime.dll，不是本项目构建的独立 Worker。");
        }

        if (File.Exists(gameAssembly))
        {
            throw new InvalidOperationException(
                "拒绝启动：目标 Player 含有游戏 Assembly-CSharp.dll；正式渲染器绝不会运行游戏 Player。");
        }
    }

    internal static string EnsureGameResourceWorker(
        string workerExecutable,
        string gameDataDirectory,
        int placeholderMaximumSide = 0)
    {
        var workerDirectory = Path.GetDirectoryName(Path.GetFullPath(workerExecutable))
            ?? throw new InvalidOperationException("无法确定独立 Worker 的目录。");
        var gameData = Path.GetFullPath(gameDataDirectory);
        var gameResources = Path.Combine(gameData, "resources.assets");
        if (!File.Exists(gameResources))
            throw new FileNotFoundException("游戏 Data 目录缺少 resources.assets。", gameResources);

        var patchExecutableCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Tools", "Patch-WorkerGameResources.exe"),
            Path.Combine(Path.GetDirectoryName(workerDirectory) ?? workerDirectory, "Tools", "Patch-WorkerGameResources.exe"),
        };
        var patchScriptCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Tools", "Patch-WorkerGameResources.py"),
            Path.Combine(Path.GetDirectoryName(workerDirectory) ?? workerDirectory, "Tools", "Patch-WorkerGameResources.py"),
            Path.GetFullPath(Path.Combine(workerDirectory, "..", "..", "scripts", "Patch-WorkerGameResources.py")),
        };
        var patchExecutable = patchExecutableCandidates.FirstOrDefault(File.Exists);
        var patchScript = patchScriptCandidates.FirstOrDefault(File.Exists);
        var patchTool = patchExecutable ?? patchScript
            ?? throw new FileNotFoundException(
                "找不到本机 Worker 资源准备器 Patch-WorkerGameResources.exe 或 .py。",
                patchExecutableCandidates[0]);

        var workerRuntime = Path.Combine(
            workerDirectory,
            Path.GetFileNameWithoutExtension(workerExecutable) + "_Data",
            "Managed",
            "ADOFAIVfxWorker.Runtime.dll");
        var identity = string.Join("|", new[]
        {
            FileIdentity(workerExecutable),
            FileIdentity(workerRuntime),
            FileIdentity(gameResources),
            FileIdentity(Path.Combine(gameData, "globalgamemanagers")),
            FileIdentity(patchTool),
            placeholderMaximumSide.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20];
        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ADOFAIVfxRenderer",
            "WorkerCache");
        Directory.CreateDirectory(cacheRoot);
        var preparedDirectory = Path.Combine(cacheRoot, cacheKey);
        var preparedExecutable = Path.Combine(preparedDirectory, Path.GetFileName(workerExecutable));
        if (File.Exists(preparedExecutable)) return preparedExecutable;

        var temporaryDirectory = Path.Combine(cacheRoot, cacheKey + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = patchExecutable ?? "python",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (patchExecutable == null) startInfo.ArgumentList.Add(patchScript!);
            startInfo.ArgumentList.Add("--worker");
            startInfo.ArgumentList.Add(workerDirectory);
            startInfo.ArgumentList.Add("--game-data");
            startInfo.ArgumentList.Add(gameData);
            startInfo.ArgumentList.Add("--output");
            startInfo.ArgumentList.Add(temporaryDirectory);
            startInfo.ArgumentList.Add("--include-game-scene");
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 Worker 资源准备器。");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(standardOutput, standardError);
            if (process.ExitCode != 0)
            {
                var details = string.Join(Environment.NewLine,
                    new[] { standardError.Result.Trim(), standardOutput.Result.Trim() }
                        .Where(static value => value.Length > 0));
                throw new InvalidOperationException(
                    "本机 Worker 资源准备失败。" +
                    (details.Length > 0 ? Environment.NewLine + details : ""));
            }

            var temporaryExecutable = Path.Combine(temporaryDirectory, Path.GetFileName(workerExecutable));
            if (!File.Exists(temporaryExecutable))
                throw new InvalidOperationException("资源准备器完成后没有生成 Worker 可执行文件。");
            if (placeholderMaximumSide > 0)
            {
                ManagedGameAssemblyPatcher.ApplyDynamicTextureStreaming(
                    Path.Combine(
                        temporaryDirectory,
                        Path.GetFileNameWithoutExtension(workerExecutable) + "_Data",
                        "Managed"));
            }
            try
            {
                Directory.Move(temporaryDirectory, preparedDirectory);
            }
            catch (IOException) when (File.Exists(preparedExecutable))
            {
                TryDeleteDirectory(temporaryDirectory);
            }
            return preparedExecutable;
        }
        catch
        {
            TryDeleteDirectory(temporaryDirectory);
            throw;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException)
            {
                if (attempt < 3) Thread.Sleep(150 * (attempt + 1));
            }
            catch (UnauthorizedAccessException)
            {
                if (attempt < 3) Thread.Sleep(150 * (attempt + 1));
            }
        }
    }

    private static string FileIdentity(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("准备 Worker 所需文件不存在。", path);
        return $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
    }

    private static ResolvedRenderBackend ResolveBackend(RenderBackend backend, string ffmpegPath)
    {
        if (!File.Exists(ffmpegPath)) throw new FileNotFoundException("游戏目录内缺少 FFmpeg。", ffmpegPath);
        return backend switch
        {
            RenderBackend.Automatic => ResolveAutomaticEncoder(ffmpegPath),
            RenderBackend.CpuCompatibility => new ResolvedRenderBackend(
                backend, "libopenh264", "", "-force-d3d11 -force-driver-type-warp", "CPU WARP 光栅 + CPU H.264 编码"),
            RenderBackend.HardwareGpu => ResolveFirstHardwareEncoder(ffmpegPath),
            RenderBackend.NvidiaRtx when CanEncode(ffmpegPath, "h264_nvenc") => new ResolvedRenderBackend(
                backend, "h264_nvenc", "p7", "-force-d3d11", "GPU 光栅 + NVIDIA NVENC p7 编码"),
            RenderBackend.NvidiaRtx => throw new InvalidOperationException(
                "当前机器或 FFmpeg 无法实际启动 NVIDIA NVENC；RTX 模式没有静默回退。请选择自动、CPU 或普通 GPU。"),
            _ => throw new ArgumentOutOfRangeException(nameof(backend)),
        };
    }

    private static ResolvedRenderBackend ResolveAutomaticEncoder(string ffmpegPath)
    {
        foreach (var encoder in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
        {
            if (!CanEncode(ffmpegPath, encoder)) continue;
            return new ResolvedRenderBackend(
                RenderBackend.Automatic,
                encoder,
                encoder == "h264_nvenc" ? "p4" : "",
                "-force-d3d11",
                $"自动：GPU 光栅 + {encoder} 硬件编码");
        }
        return new ResolvedRenderBackend(
            RenderBackend.Automatic,
            "libopenh264",
            "",
            "-force-d3d11",
            "自动：GPU 光栅 + CPU H.264 编码（无可用硬件编码器）");
    }

    private static ResolvedRenderBackend ResolveFirstHardwareEncoder(string ffmpegPath)
    {
        foreach (var encoder in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
        {
            if (!CanEncode(ffmpegPath, encoder)) continue;
            return new ResolvedRenderBackend(
                RenderBackend.HardwareGpu,
                encoder,
                encoder == "h264_nvenc" ? "p4" : "",
                "-force-d3d11",
                $"GPU 光栅 + {encoder} 硬件编码");
        }
        throw new InvalidOperationException(
            "没有找到可实际编码一帧的 H.264 硬件编码器；普通 GPU 模式没有静默回退。请选择自动或 CPU。 ");
    }

    private static bool CanEncode(string ffmpegPath, string encoder)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                Arguments = $"-hide_banner -loglevel error -f lavfi -i color=c=black:s=64x64:r=1 -frames:v 1 -an -c:v {encoder} -f null NUL",
            });
            if (process is null) return false;
            if (!process.WaitForExit(8000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return false;
        }
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

    [JsonPropertyName("renderWidth")]
    public int RenderWidth { get; init; }

    [JsonPropertyName("renderHeight")]
    public int RenderHeight { get; init; }

    [JsonPropertyName("framesPerSecond")]
    public int FramesPerSecond { get; init; }

    [JsonPropertyName("tailSeconds")]
    public float TailSeconds { get; init; }

    [JsonPropertyName("includeTrack")]
    public bool IncludeTrack { get; init; }

    [JsonPropertyName("includePlanets")]
    public bool IncludePlanets { get; init; }

    [JsonPropertyName("includeFilters")]
    public bool IncludeFilters { get; init; }

    [JsonPropertyName("bakedFilterKeys")]
    public string[] BakedFilterKeys { get; init; } = [];

    [JsonPropertyName("includeBackgroundDecorations")]
    public bool IncludeBackgroundDecorations { get; init; }

    [JsonPropertyName("includeForegroundDecorations")]
    public bool IncludeForegroundDecorations { get; init; }

    [JsonPropertyName("retainedDecorationIndices")]
    public int[] RetainedDecorationIndices { get; init; } = [];

    [JsonPropertyName("excludedDecorationIndices")]
    public int[] ExcludedDecorationIndices { get; init; } = [];

    [JsonPropertyName("bitrateKbps")]
    public int BitrateKbps { get; init; }

    [JsonPropertyName("durationLimitSeconds")]
    public float DurationLimitSeconds { get; init; }

    [JsonPropertyName("renderStartSeconds")]
    public float RenderStartSeconds { get; init; }

    [JsonPropertyName("ffmpegPath")]
    public string FfmpegPath { get; init; } = "";

    [JsonPropertyName("gameManagedDirectory")]
    public string GameManagedDirectory { get; init; } = "";

    [JsonPropertyName("videoEncoder")]
    public string VideoEncoder { get; init; } = "libopenh264";

    [JsonPropertyName("encoderPreset")]
    public string EncoderPreset { get; init; } = "";

    [JsonPropertyName("placeholderMaximumSide")]
    public int PlaceholderMaximumSide { get; init; }

    [JsonPropertyName("textureMemoryBudgetBytes")]
    public long TextureMemoryBudgetBytes { get; init; }
}

internal sealed class WorkerStatus
{
    [JsonPropertyName("jobId")]
    public string JobId { get; init; } = "";

    [JsonPropertyName("state")]
    public string State { get; init; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("outputPath")]
    public string OutputPath { get; init; } = "";

    [JsonPropertyName("previewPath")]
    public string PreviewPath { get; init; } = "";

    [JsonPropertyName("error")]
    public string Error { get; set; } = "";

    [JsonPropertyName("unbakedDecorationIndices")]
    public int[] UnbakedDecorationIndices { get; init; } = [];

    public string DerivedChartPath { get; set; } = "";

    public int VideoOffsetMilliseconds { get; set; }

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

    [JsonPropertyName("chartDurationSeconds")]
    public float ChartDurationSeconds { get; init; }

    public long WorkingSetBytes { get; set; }

    public long PeakWorkingSetBytes { get; set; }

    public long MemoryLimitBytes { get; set; }

    public long EstimatedTextureBytes { get; set; }

    public int TextureCount { get; set; }

    public int PlaceholderMaximumSide { get; set; }
}
