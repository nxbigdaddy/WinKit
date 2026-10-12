# 冒烟验证：真的把 exe 启动起来，确认它能跑，再关掉。
#
# 为什么需要这个：单元测试全绿、编译零错误，都不能证明这个 WPF 程序真的能启动 ——
# 资源字典没合并、XAML 运行时解析失败、静态构造里空引用，这些都会让窗口一闪而过。
# 所以必须实测：进程活着 + 窗口标题出来了 + 数据目录建起来了 + 日志写进去了。
#
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File scripts\smoke-test.ps1
#      -Exe <路径>  -WaitSeconds 10  -KeepRunning

param(
    [string]$Exe = "",
    [int]$WaitSeconds = 10,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Continue'

$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Exe)) {
    $candidates = @(
        (Join-Path $root 'src\WinKit.App\bin\Release\WinKit.exe'),
        (Join-Path $root 'src\WinKit.App\bin\Debug\WinKit.exe')
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $Exe = $c; break }
    }
}

if ([string]::IsNullOrWhiteSpace($Exe) -or -not (Test-Path $Exe)) {
    Write-Host "找不到 WinKit.exe，请先构建，或用 -Exe 指定路径" -ForegroundColor Red
    exit 2
}

$Exe = (Resolve-Path $Exe).Path
$exeDir = Split-Path -Parent $Exe
Write-Host ("被测程序: " + $Exe) -ForegroundColor Cyan

# 记录启动前的数据目录状态，用于判断"是这次启动新建的"
$portableData = Join-Path $exeDir 'WinKit-data'
$fallbackData = Join-Path $env:LOCALAPPDATA 'WinKit'
$configBefore = Test-Path (Join-Path $portableData 'config.json')

Write-Host ""
Write-Host "=== 启动 ===" -ForegroundColor Cyan
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$proc = Start-Process -FilePath $Exe -PassThru
if (-not $proc) { Write-Host "启动失败：Start-Process 返回空" -ForegroundColor Red; exit 1 }

# 等窗口句柄出现（这比单纯 sleep 更能证明 XAML 真的加载成功了）
$deadline = (Get-Date).AddSeconds($WaitSeconds)
$windowShown = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 200
    try { $proc.Refresh() } catch { break }
    if ($proc.HasExited) { break }
    if ($proc.MainWindowHandle -ne 0) { $windowShown = $true; break }
}
$sw.Stop()

$exitCode = $null
try { if ($proc.HasExited) { $exitCode = $proc.ExitCode } } catch { }

Write-Host ""
Write-Host "=== 结果 ===" -ForegroundColor Cyan
$failures = New-Object 'System.Collections.Generic.List[string]'

if ($proc.HasExited) {
    Write-Host ("进程已退出，退出码 = " + $exitCode) -ForegroundColor Red
    $failures.Add("进程启动后立即退出（退出码 $exitCode）—— 典型的未捕获异常，检查日志与 Windows 事件查看器")
} else {
    Write-Host ("进程存活     : 是") -ForegroundColor Green
    Write-Host ("窗口出现耗时 : {0} ms" -f $sw.ElapsedMilliseconds)
    try {
        Write-Host ("窗口标题     : " + $proc.MainWindowTitle)
        if ([string]::IsNullOrWhiteSpace($proc.MainWindowTitle)) {
            $failures.Add("窗口句柄有了但标题为空，界面可能没有正常构建")
        }
    } catch { }
    try { Write-Host ("工作集内存   : " + [math]::Round($proc.WorkingSet64 / 1MB, 1) + " MB") } catch { }
    try { Write-Host ("线程数       : " + $proc.Threads.Count) } catch { }

    if (-not $windowShown) {
        $failures.Add("等待 $WaitSeconds 秒后仍未出现主窗口")
    }
}

# 数据目录与日志
Write-Host ""
Write-Host "=== 数据目录 ===" -ForegroundColor Cyan
$dataDir = $null
if (Test-Path $portableData) { $dataDir = $portableData; Write-Host ("使用便携模式: " + $portableData) -ForegroundColor Green }
elseif (Test-Path $fallbackData) { $dataDir = $fallbackData; Write-Host ("使用回退模式: " + $fallbackData) -ForegroundColor Yellow }
else { $failures.Add("启动后既没有 WinKit-data 也没有 %LOCALAPPDATA%\WinKit，配置与日志没有落盘") }

if ($dataDir) {
    foreach ($sub in @('config.json', 'logs', 'backups', 'undo', 'cache')) {
        $p = Join-Path $dataDir $sub
        $ok = Test-Path $p
        Write-Host ("  " + $(if ($ok) { "[OK]  " } else { "[缺失]" }) + " " + $sub) -ForegroundColor $(if ($ok) { 'Gray' } else { 'Yellow' })
    }

    $logDir = Join-Path $dataDir 'logs'
    if (Test-Path $logDir) {
        $log = Get-ChildItem $logDir -Filter '*.log' -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($log) {
            Write-Host ""
            Write-Host ("=== 日志尾部 (" + $log.Name + ") ===") -ForegroundColor Cyan
            Get-Content $log.FullName -Encoding UTF8 -Tail 12 | ForEach-Object { Write-Host ("  " + $_) }
        } else {
            $failures.Add("logs 目录里没有 .log 文件，日志系统可能没初始化")
        }
    }
}

# 清理
if (-not $KeepRunning -and -not $proc.HasExited) {
    try { $proc.Kill(); $proc.WaitForExit(3000) | Out-Null; Write-Host ""; Write-Host "已关闭测试进程" -ForegroundColor Gray } catch { }
}

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "冒烟验证未通过：" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host ("  - " + $f) -ForegroundColor Red }
    exit 1
}

Write-Host "冒烟验证通过：程序能启动、窗口构建成功、数据目录与日志正常" -ForegroundColor Green
exit 0
