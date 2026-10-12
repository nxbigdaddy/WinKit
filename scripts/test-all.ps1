# 运行全部单元测试。
# 用法：powershell -File scripts\test-all.ps1

$ErrorActionPreference = 'Continue'

$root = Split-Path -Parent $PSScriptRoot
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

$projects = @(
    "tests\WinKit.Core.Tests\WinKit.Core.Tests.csproj",
    "tests\WinKit.Platform.Tests\WinKit.Platform.Tests.csproj"
)

$failed = @()

foreach ($project in $projects) {
    $full = Join-Path $root $project
    if (-not (Test-Path $full)) {
        Write-Host ("跳过（不存在）：" + $project) -ForegroundColor Yellow
        continue
    }

    Write-Host ""
    Write-Host ("=== " + $project + " ===") -ForegroundColor Cyan
    & $dotnet test $full -c Debug --nologo -v q
    if ($LASTEXITCODE -ne 0) { $failed += $project }
}

Write-Host ""
if ($failed.Count -gt 0) {
    Write-Host "以下测试工程失败：" -ForegroundColor Red
    $failed | ForEach-Object { Write-Host ("  " + $_) -ForegroundColor Red }
    exit 1
}

Write-Host "全部测试通过" -ForegroundColor Green
