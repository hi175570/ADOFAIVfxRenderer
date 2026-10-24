param(
    [string]$Configuration = "Release",
    [switch]$SkipWorkerBuild
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publishRoot = Join-Path $projectRoot "artifacts\Publish\ADOFAIVfxRenderer"
$workerSource = Join-Path $projectRoot "artifacts\UnityWorker"
$workerDestination = Join-Path $publishRoot "UnityWorker"

if (-not $SkipWorkerBuild) {
    & (Join-Path $PSScriptRoot "Build-UnityWorker.ps1") -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "Unity Worker build failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $workerSource "ADOFAI VFX Worker.exe"))) {
    throw "The standalone Unity Worker has not been built."
}

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
dotnet publish `
    (Join-Path $projectRoot "src\ADOFAIVfxRenderer.App\ADOFAIVfxRenderer.App.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained false `
    -o $publishRoot
if ($LASTEXITCODE -ne 0) {
    throw "Desktop publish failed with exit code $LASTEXITCODE."
}

$publishPrefix = [IO.Path]::GetFullPath($publishRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$workerDestinationFull = [IO.Path]::GetFullPath($workerDestination)
if (-not $workerDestinationFull.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace a Worker directory outside the generated publish folder."
}
if (Test-Path -LiteralPath $workerDestinationFull) {
    Remove-Item -LiteralPath $workerDestinationFull -Recurse -Force
}
Copy-Item -LiteralPath $workerSource -Destination $workerDestinationFull -Recurse -Force

$gameAssembly = Join-Path $workerDestination "ADOFAI VFX Worker_Data\Managed\Assembly-CSharp.dll"
if (Test-Path -LiteralPath $gameAssembly) {
    throw "Package rejected: the standalone Worker unexpectedly contains Assembly-CSharp.dll."
}

Write-Host "Test package ready: $publishRoot"
