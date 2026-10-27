param(
    [Parameter(Mandatory = $true)][string]$Application,
    [Parameter(Mandatory = $true)][string]$Chart,
    [Parameter(Mandatory = $true)][string]$GamePath,
    [string]$Profile = "VideoPro",
    [string]$Resolution = "1080p",
    [int]$FramesPerSecond = 60,
    [decimal]$DurationSeconds = 10,
    [decimal]$TextureBudgetGb = 1,
    [string]$ExcludeDecorationIndices = "",
    [switch]$Unlimited,
    [string]$Backend = "HardwareGpu",
    [Parameter(Mandatory = $true)][string]$OutputJson
)

$ErrorActionPreference = "Stop"
$arguments = @(
    "--smoke-chart", (Resolve-Path -LiteralPath $Chart).Path,
    "--game-path", (Resolve-Path -LiteralPath $GamePath).Path,
    "--profile", $Profile,
    "--resolution", $Resolution,
    "--fps", $FramesPerSecond,
    "--duration-limit", $DurationSeconds.ToString([Globalization.CultureInfo]::InvariantCulture),
    "--tail-seconds", "0",
    "--texture-budget-gb", $TextureBudgetGb.ToString([Globalization.CultureInfo]::InvariantCulture),
    "--backend", $Backend,
    "--diagnose-memory",
    "--exit-when-complete"
)
if (-not [string]::IsNullOrWhiteSpace($ExcludeDecorationIndices)) {
    $arguments += @("--exclude-decoration-indices", $ExcludeDecorationIndices)
}
if ($Unlimited) { $arguments += "--unlimited-memory" }
$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = (Resolve-Path -LiteralPath $Application).Path
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
foreach ($argument in $arguments) { $startInfo.ArgumentList.Add([string]$argument) }
$applicationProcess = [Diagnostics.Process]::Start($startInfo)
if ($null -eq $applicationProcess) { throw "Failed to start renderer application." }
$samples = [Collections.Generic.List[object]]::new()
$peaks = @{}
while (-not $applicationProcess.HasExited) {
    $sampleTime = [DateTime]::UtcNow
    foreach ($process in Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -in @("ADOFAIVfxRenderer", "ADOFAI VFX Worker", "ffmpeg")
    }) {
        $key = "$($process.ProcessName):$($process.Id)"
        $workingSet = $process.WorkingSet64
        if (-not $peaks.ContainsKey($key) -or $workingSet -gt $peaks[$key]) {
            $peaks[$key] = $workingSet
        }
        $samples.Add([pscustomobject]@{
            TimeUtc = $sampleTime
            Process = $process.ProcessName
            Id = $process.Id
            WorkingSetBytes = $workingSet
            PrivateMemoryBytes = $process.PrivateMemorySize64
        })
    }
    Start-Sleep -Milliseconds 200
    $applicationProcess.Refresh()
}
$result = [pscustomobject]@{
    ExitCode = $applicationProcess.ExitCode
    StartedUtc = $applicationProcess.StartTime.ToUniversalTime()
    EndedUtc = [DateTime]::UtcNow
    Peaks = $peaks
    MaximumCombinedWorkingSetBytes = ($samples | Group-Object TimeUtc | ForEach-Object {
        ($_.Group | Measure-Object WorkingSetBytes -Sum).Sum
    } | Measure-Object -Maximum).Maximum
    Samples = $samples
}
$directory = Split-Path -Parent $OutputJson
if ($directory) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputJson -Encoding utf8
$result | Select-Object ExitCode, StartedUtc, EndedUtc, MaximumCombinedWorkingSetBytes
$peaks.GetEnumerator() | Sort-Object Name | Format-Table Name, Value
exit $applicationProcess.ExitCode
