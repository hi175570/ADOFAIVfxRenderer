param(
    [Parameter(Mandatory = $true)][string]$ChartPath,
    [Parameter(Mandatory = $true)][string]$GamePath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [double]$StartSeconds = 0,
    [double]$DurationSeconds = 10,
    [int]$Width = 640,
    [int]$Height = 360,
    [int]$Fps = 15,
    [string]$VideoEncoder = "libopenh264",
    [string]$EncoderPreset = "",
    [switch]$IncludeEffects,
    [string[]]$BakedFilterKey = @(),
    [switch]$CpuCompatibility
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$worker = Join-Path $projectRoot "artifacts\UnityWorker\ADOFAI VFX Worker.exe"
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
$job = [ordered]@{
    jobId = [Guid]::NewGuid().ToString("N")
    chartPath = $chart
    outputPath = Join-Path $output "segment.mp4"
    statusPath = Join-Path $output "render-status.json"
    previewDirectory = $preview
    profile = "VideoVfx"
    width = $Width
    height = $Height
    framesPerSecond = $Fps
    tailSeconds = 0
    includeTrack = $false
    includePlanets = $false
    includeEffects = [bool]$IncludeEffects -or $BakedFilterKey.Count -gt 0
    bakedFilterKeys = @($BakedFilterKey)
    includeBackgroundDecorations = $true
    includeForegroundDecorations = $true
    retainedDecorationIndices = @()
    bitrateKbps = 1200
    durationLimitSeconds = $DurationSeconds
    renderStartSeconds = $StartSeconds
    ffmpegPath = Join-Path $game "ffmpeg\ffmpeg.exe"
    videoEncoder = $VideoEncoder
    encoderPreset = $EncoderPreset
}
$jobPath = Join-Path $output "render-job.json"
$job | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $jobPath -Encoding utf8NoBOM
$log = Join-Path $output "worker.log"
$workerArguments = @(
    '-batchmode', '-logFile', $log, '--job', $jobPath,
    '-screen-fullscreen', '0', '-screen-width', $Width, '-screen-height', $Height,
    '-force-d3d11'
)
if ($CpuCompatibility) { $workerArguments += '-force-driver-type-warp' }
$process = Start-Process -FilePath $worker -WorkingDirectory (Split-Path $worker) -WindowStyle Hidden -PassThru -Wait -ArgumentList $workerArguments
if ($process.ExitCode -ne 0) {
    throw "Worker exited with code $($process.ExitCode). See $log"
}
Get-Content -LiteralPath $job.statusPath
