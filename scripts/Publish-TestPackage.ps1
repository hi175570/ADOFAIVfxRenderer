param(
    [string]$Configuration = "Release",
    [switch]$SkipWorkerBuild,
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publishRoot = Join-Path $projectRoot "artifacts\Publish\ADOFAIVfxRenderer"
$workerSource = Join-Path $projectRoot "artifacts\UnityWorker"
$workerDestination = Join-Path $publishRoot "UnityWorker"
$toolsDestination = Join-Path $publishRoot "Tools"
$resourcePatcher = Join-Path $projectRoot "artifacts\Tools\Patch-WorkerGameResources.exe"

if (-not $SkipWorkerBuild) {
    & (Join-Path $PSScriptRoot "Build-UnityWorker.ps1") -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "Unity Worker build failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $workerSource "ADOFAI VFX Worker.exe"))) {
    throw "The standalone Unity Worker has not been built."
}

$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot "artifacts"))
$publishRootFull = [IO.Path]::GetFullPath($publishRoot)
$artifactsPrefix = $artifactsRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $publishRootFull.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace a publish folder outside artifacts."
}
if (Test-Path -LiteralPath $publishRootFull) {
    Remove-Item -LiteralPath $publishRootFull -Recurse -Force
}
New-Item -ItemType Directory -Path $publishRootFull -Force | Out-Null
$selfContainedValue = if ($SelfContained) { "true" } else { "false" }
dotnet publish `
    (Join-Path $projectRoot "src\ADOFAIVfxRenderer.App\ADOFAIVfxRenderer.App.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained $selfContainedValue `
    -p:NuGetAudit=false `
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
New-Item -ItemType Directory -Path $toolsDestination -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot "scripts\Patch-WorkerGameResources.py") `
    -Destination (Join-Path $toolsDestination "Patch-WorkerGameResources.py") -Force
if (Test-Path -LiteralPath $resourcePatcher) {
    Copy-Item -LiteralPath $resourcePatcher `
        -Destination (Join-Path $toolsDestination "Patch-WorkerGameResources.exe") -Force
}

$gameAssembly = Join-Path $workerDestination "ADOFAI VFX Worker_Data\Managed\Assembly-CSharp.dll"
if (Test-Path -LiteralPath $gameAssembly) {
    throw "Package rejected: the standalone Worker unexpectedly contains Assembly-CSharp.dll."
}

Write-Host "Test package ready: $publishRoot"
