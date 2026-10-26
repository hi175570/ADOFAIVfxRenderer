param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$scriptPath = Join-Path $PSScriptRoot "Patch-WorkerGameResources.py"
$distPath = Join-Path $projectRoot "artifacts\Tools"
$workPath = Join-Path $projectRoot "artifacts\PyInstaller\work"
$specPath = Join-Path $projectRoot "artifacts\PyInstaller"
$outputPath = Join-Path $distPath "Patch-WorkerGameResources.exe"

python -c "import PyInstaller, UnityPy"
if ($LASTEXITCODE -ne 0) {
    throw "缺少 PyInstaller 或 UnityPy；请先运行 python -m pip install pyinstaller UnityPy。"
}

New-Item -ItemType Directory -Path $distPath,$workPath,$specPath -Force | Out-Null
python -m PyInstaller `
    --noconfirm `
    --clean `
    --onefile `
    --name Patch-WorkerGameResources `
    --collect-data UnityPy `
    --exclude-module pandas `
    --exclude-module scipy `
    --exclude-module matplotlib `
    --exclude-module pytest `
    --exclude-module IPython `
    --exclude-module PyQt6 `
    --exclude-module pygame `
    --exclude-module tkinter `
    --exclude-module sqlalchemy `
    --exclude-module black `
    --exclude-module jedi `
    --exclude-module nbformat `
    --exclude-module zmq `
    --distpath $distPath `
    --workpath $workPath `
    --specpath $specPath `
    $scriptPath
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $outputPath)) {
    throw "Worker 资源准备器构建失败。"
}

Write-Host "独立资源准备器已生成：$outputPath"
