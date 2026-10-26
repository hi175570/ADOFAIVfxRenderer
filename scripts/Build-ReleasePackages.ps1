param(
    [string]$Version = "0.1.0",
    [string]$Configuration = "Release",
    [switch]$SkipWorkerBuild,
    [switch]$SkipResourcePatcherBuild
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$releaseRoot = Join-Path $projectRoot "artifacts\Release"
$publishRoot = Join-Path $projectRoot "artifacts\Publish\ADOFAIVfxRenderer"
$portableRoot = Join-Path $projectRoot "artifacts\Portable\ADOFAIVfxRenderer"
$portableZip = Join-Path $releaseRoot "ADOFAIVfxRenderer-v$Version-win-x64-portable.zip"
$checksums = Join-Path $releaseRoot "SHA256SUMS.txt"
$innoCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$iscc = $innoCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) { throw "找不到 Inno Setup 6 编译器 ISCC.exe。" }

if (-not $SkipWorkerBuild) {
    & (Join-Path $PSScriptRoot "Build-UnityWorker.ps1") -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Unity Worker 构建失败。" }
}
if (-not $SkipResourcePatcherBuild) {
    & (Join-Path $PSScriptRoot "Build-ResourcePatcher.ps1") -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "资源准备器构建失败。" }
}
& (Join-Path $PSScriptRoot "Publish-TestPackage.ps1") -Configuration $Configuration -SkipWorkerBuild -SelfContained
if ($LASTEXITCODE -ne 0) { throw "桌面应用发布失败。" }

$releaseFull = [IO.Path]::GetFullPath($releaseRoot)
$artifactPrefix = [IO.Path]::GetFullPath((Join-Path $projectRoot "artifacts")).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not ($releaseFull + [IO.Path]::DirectorySeparatorChar).StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "拒绝清理 artifacts 之外的发布目录。"
}
if (Test-Path -LiteralPath $releaseFull) { Remove-Item -LiteralPath $releaseFull -Recurse -Force }
New-Item -ItemType Directory -Path $releaseFull -Force | Out-Null

$portableParent = Split-Path $portableRoot
if (Test-Path -LiteralPath $portableRoot) { Remove-Item -LiteralPath $portableRoot -Recurse -Force }
New-Item -ItemType Directory -Path $portableParent -Force | Out-Null
Copy-Item -LiteralPath $publishRoot -Destination $portableRoot -Recurse -Force
Compress-Archive -LiteralPath $portableRoot -DestinationPath $portableZip -CompressionLevel Optimal -Force

& $iscc `
    "/DMyAppVersion=$Version" `
    "/DSourceDir=$publishRoot" `
    "/DOutputDir=$releaseRoot" `
    (Join-Path $projectRoot "installer\ADOFAIVfxRenderer.iss")
if ($LASTEXITCODE -ne 0) { throw "安装程序构建失败。" }

$setup = Join-Path $releaseRoot "ADOFAIVfxRenderer-v$Version-win-x64-setup.exe"
if (-not (Test-Path -LiteralPath $portableZip) -or -not (Test-Path -LiteralPath $setup)) {
    throw "发布产物不完整。"
}
$hashLines = foreach ($path in @($portableZip, $setup)) {
    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $path
    "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($path))"
}
[IO.File]::WriteAllLines($checksums, $hashLines, [Text.UTF8Encoding]::new($false))

Get-Item -LiteralPath $portableZip,$setup,$checksums | Select-Object FullName,Length
