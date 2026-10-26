param(
    [Parameter(Mandatory = $true)][string]$ChartPath,
    [Parameter(Mandatory = $true)][string]$GamePath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$WorkerPath = "",
    [double]$StartSeconds = 0,
    [double]$DurationSeconds = 10,
    [int]$Width = 640,
    [int]$Height = 360,
    [int]$RenderWidth = 0,
    [int]$RenderHeight = 0,
    [int]$Fps = 15,
    [int]$BitrateKbps = 1200,
    [ValidateSet("Video", "VideoPro", "VideoVfx", "VideoVfxPro")][string]$Profile = "VideoVfx",
    [string]$VideoEncoder = "libopenh264",
    [string]$EncoderPreset = "",
    [Alias("IncludeEffects")][switch]$IncludeFilters,
    [switch]$IncludeTrack,
    [switch]$IncludePlanets,
    [switch]$NoBackgroundDecorations,
    [switch]$NoForegroundDecorations,
    [string[]]$BakedFilterKey = @(),
    [string]$BakedFilterKeysCsv = "",
    [string]$RetainedDecorationIndicesCsv = "",
    [switch]$DiagnoseDecorations,
    [int]$DiagnoseRenderFrame = -1,
    [switch]$CpuCompatibility
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$worker = if ([string]::IsNullOrWhiteSpace($WorkerPath)) {
    Join-Path $projectRoot "artifacts\UnityWorker\ADOFAI VFX Worker.exe"
} else {
    (Resolve-Path -LiteralPath $WorkerPath).Path
}
$chart = (Resolve-Path -LiteralPath $ChartPath).Path
$game = (Resolve-Path -LiteralPath $GamePath).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$projectPrefix = $projectRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$outputPrefix = $output.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $outputPrefix.StartsWith((Join-Path $projectRoot "artifacts").TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Segment output must stay under this project's artifacts directory."
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$preview = Join-Path $output "preview"
New-Item -ItemType Directory -Path $preview -Force | Out-Null
$resolvedBakedFilterKeys = @($BakedFilterKey)
if (-not [string]::IsNullOrWhiteSpace($BakedFilterKeysCsv)) {
    $resolvedBakedFilterKeys += @($BakedFilterKeysCsv.Split(',', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() })
}
$retainedDecorationIndices = if ([string]::IsNullOrWhiteSpace($RetainedDecorationIndicesCsv)) {
    @()
} else {
    @($RetainedDecorationIndicesCsv.Split(',', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { [int]$_.Trim() })
}
$job = [ordered]@{
    jobId = [Guid]::NewGuid().ToString("N")
    chartPath = $chart
    outputPath = Join-Path $output "segment.mp4"
    statusPath = Join-Path $output "render-status.json"
    previewDirectory = $preview
    profile = $Profile
    width = $Width
    height = $Height
    renderWidth = if ($RenderWidth -gt 0) { $RenderWidth } else { $Width }
    renderHeight = if ($RenderHeight -gt 0) { $RenderHeight } else { $Height }
    framesPerSecond = $Fps
    tailSeconds = 0
    includeTrack = [bool]$IncludeTrack
    includePlanets = [bool]$IncludePlanets
    includeFilters = [bool]$IncludeFilters -or $resolvedBakedFilterKeys.Count -gt 0
    bakedFilterKeys = @($resolvedBakedFilterKeys)
    includeBackgroundDecorations = -not [bool]$NoBackgroundDecorations
    includeForegroundDecorations = -not [bool]$NoForegroundDecorations
    retainedDecorationIndices = @($retainedDecorationIndices)
    bitrateKbps = $BitrateKbps
    durationLimitSeconds = $DurationSeconds
    renderStartSeconds = $StartSeconds
    ffmpegPath = Join-Path $game "ffmpeg\ffmpeg.exe"
    gameManagedDirectory = Join-Path $game "A Dance of Fire and Ice_Data\Managed"
    videoEncoder = $VideoEncoder
    encoderPreset = $EncoderPreset
}
$jobPath = Join-Path $output "render-job.json"
$job | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $jobPath -Encoding utf8NoBOM
$log = Join-Path $output "worker.log"
$workerArguments = @(
    '-batchmode', '-logFile', $log, '--job', $jobPath,
    '-screen-fullscreen', '0', '-screen-width', $job.renderWidth, '-screen-height', $job.renderHeight,
    '-force-d3d11'
)
if ($CpuCompatibility) { $workerArguments += '-force-driver-type-warp' }
if ($DiagnoseDecorations) { $workerArguments += '--diagnose-decorations' }
if ($DiagnoseRenderFrame -ge 0) { $workerArguments += @('--diagnose-render-frame', $DiagnoseRenderFrame) }
$process = Start-Process -FilePath $worker -WorkingDirectory (Split-Path $worker) -WindowStyle Hidden -PassThru -Wait -ArgumentList $workerArguments
if ($process.ExitCode -ne 0) {
    throw "Worker exited with code $($process.ExitCode). See $log"
}
Get-Content -LiteralPath $job.statusPath
