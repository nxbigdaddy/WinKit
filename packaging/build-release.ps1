# 构建发布版并做"零依赖"校验 + 体积报告。
# 用法：powershell -File packaging\build-release.ps1

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

Write-Host "=== 1/4 构建 Release ===" -ForegroundColor Cyan
& $dotnet build "$root\src\WinKit.App\WinKit.App.csproj" -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "构建失败" }

$binDir = Join-Path $root "src\WinKit.App\bin\Release"
if (-not (Test-Path $binDir)) { throw "找不到输出目录：$binDir" }

Write-Host ""
Write-Host "=== 2/4 零依赖校验 ===" -ForegroundColor Cyan
# 设计目标：发布目录里除了主 exe（和可选的 .exe.config）不应该有任何第三方 DLL。
# 一旦出现 DLL，说明有人引入了 NuGet 依赖，会破坏"单文件"承诺 —— 这里直接报错。
$unexpected = Get-ChildItem $binDir -File |
    Where-Object { $_.Extension -notin @('.exe', '.config', '.pdb') }
if ($unexpected) {
    Write-Host "发现非预期文件，说明引入了第三方依赖或多余产物：" -ForegroundColor Red
    $unexpected | ForEach-Object { Write-Host ("  " + $_.Name) -ForegroundColor Red }
    throw "零依赖校验未通过"
}
Write-Host "通过：输出目录除 exe 外没有任何额外文件" -ForegroundColor Green

Write-Host ""
Write-Host "=== 3/4 复制到 packaging\out ===" -ForegroundColor Cyan
$outDir = Join-Path $PSScriptRoot "out"
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$exe = Join-Path $binDir "WinKit.exe"
Copy-Item $exe $outDir -Force
$config = Join-Path $binDir "WinKit.exe.config"
if (Test-Path $config) { Copy-Item $config $outDir -Force }

Write-Host ""
Write-Host "=== 4/4 体积报告 ===" -ForegroundColor Cyan
$target = Join-Path $outDir "WinKit.exe"
$item = Get-Item $target
$sizeKb = [math]::Round($item.Length / 1KB, 1)
$sizeMb = [math]::Round($item.Length / 1MB, 2)

$version = $item.VersionInfo.FileVersion
if (-not $version) { $version = "0.0.0.0" }

Write-Host ("产物    : " + $target)
Write-Host ("版本    : " + $version)
Write-Host ("大小    : {0} KB ({1} MB)" -f $sizeKb, $sizeMb)

# 预算：技术选型里承诺的是 1~3MB。超过 8MB 说明方案跑偏了，需要人工确认。
if ($sizeMb -gt 8) {
    Write-Host "警告：体积超过 8MB，偏离了 1~3MB 的预算，请检查是否引入了不该有的依赖" -ForegroundColor Yellow
} else {
    Write-Host "体积在预算内" -ForegroundColor Green
}

Write-Host ""
Write-Host "完成。发布产物在 packaging\out" -ForegroundColor Green
