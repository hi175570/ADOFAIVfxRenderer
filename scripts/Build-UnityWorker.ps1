param(
    [string]$UnityPath,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($UnityPath)) {
    $candidates = @(
        (Join-Path $env:USERPROFILE "UnityEditors\6000.3.10f1\Editor\Unity.exe"),
        "C:\Program Files\Unity\Hub\Editor\6000.3.10f1\Editor\Unity.exe"
    )
    $UnityPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($UnityPath) -or -not (Test-Path -LiteralPath $UnityPath)) {
    throw "Unity 6000.3.10f1 was not found. Pass -UnityPath explicitly."
}

$unityProject = Join-Path $projectRoot "src\ADOFAIVfxRenderer.UnityWorker"
$output = Join-Path $projectRoot "artifacts\UnityWorker"
$log = Join-Path $projectRoot "artifacts\unity-build.log"
New-Item -ItemType Directory -Path (Split-Path $log) -Force | Out-Null
$env:ADOFAI_VFX_WORKER_OUTPUT = $output

$unityArguments = @(
    "-batchmode",
    "-nographics",
    "-quit",
    "-projectPath", "`"$unityProject`"",
    "-executeMethod", "ADOFAIVfxWorker.Editor.BuildWorker.BuildFromCommandLine",
    "-logFile", "`"$log`""
)
$unityProcess = Start-Process `
    -FilePath $UnityPath `
    -ArgumentList $unityArguments `
    -WindowStyle Hidden `
    -Wait `
    -PassThru

if ($unityProcess.ExitCode -ne 0) {
    throw "Unity Worker build failed with exit code $($unityProcess.ExitCode). See $log"
}

dotnet build (Join-Path $projectRoot "ADOFAIVfxRenderer.slnx") -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "Desktop build failed with exit code $LASTEXITCODE."
}

Write-Host "Built Unity Worker: $output"
