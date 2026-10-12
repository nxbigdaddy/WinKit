# UI 自动化冒烟测试：启动程序，用 UI Automation 逐个点开每个导航页，检查日志有没有异常。
#
# 为什么需要它：编译通过 + 主窗口能开，并不代表每个功能页都能开。
# XAML 资源键缺失、ElementStyle 类型不匹配、DataTrigger 枚举比较、绑定转换器异常
# 这些问题只在**页面真正被加载**的那一刻才暴露。这个脚本让机器代替人去点一遍。
#
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File scripts\ui-smoke-test.ps1
#      -Exe <路径> -PageDwellSeconds 3

param(
    [string]$Exe = "",
    [int]$PageDwellSeconds = 3,
    [int]$WindowTimeoutSeconds = 20
)

$ErrorActionPreference = 'Continue'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$Auto = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]

$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Exe)) {
    foreach ($c in @((Join-Path $root 'packaging\out\WinKit.exe'),
                     (Join-Path $root 'src\WinKit.App\bin\Release\WinKit.exe'),
                     (Join-Path $root 'src\WinKit.App\bin\Debug\WinKit.exe'))) {
        if (Test-Path $c) { $Exe = $c; break }
    }
}
if ([string]::IsNullOrWhiteSpace($Exe) -or -not (Test-Path $Exe)) {
    Write-Host "找不到 WinKit.exe" -ForegroundColor Red
    exit 2
}
$Exe = (Resolve-Path $Exe).Path
$exeDir = Split-Path -Parent $Exe
Write-Host ("被测程序: " + $Exe) -ForegroundColor Cyan

$logDir = Join-Path (Join-Path $exeDir 'WinKit-data') 'logs'
if (Test-Path $logDir) { Remove-Item (Join-Path $logDir '*.log') -Force -ErrorAction SilentlyContinue }

# 期望的导航标题（来自 Tasks/*Module.cs 的 Title）。新增页面必须同步加到这里，
# 否则"只点开了 N 个页面"的断言会失效、留下覆盖盲区。
$expectedTitles = @('总览', '进程管理', '垃圾清理', '一键体检', '撤销中心', '设置')

function Get-ProcessWindows([int]$processId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($Auto::ProcessIdProperty, $processId)
    return $Auto::RootElement.FindAll($Scope::Children, $cond)
}

# 只把"真正的窗口"算进来：句柄不同、非离屏、有实际尺寸。
# WPF 会为 ToolTip / Popup / 输入法创建额外的顶层窗口，它们尺寸为 0 或离屏，必须排除。
function Get-RealExtraWindows([int]$processId, [int]$mainHandle) {
    $extra = @()
    foreach ($w in (Get-ProcessWindows $processId)) {
        try {
            $c = $w.Current
            if ($c.NativeWindowHandle -eq $mainHandle) { continue }
            if ($c.IsOffscreen) { continue }
            $r = $c.BoundingRectangle
            if ($r.Width -lt 40 -or $r.Height -lt 40) { continue }
            $extra += ("'" + $c.Name + "' (" + [int]$r.Width + "x" + [int]$r.Height + ")")
        }
        catch { }
    }
    return $extra
}

$failures = New-Object 'System.Collections.Generic.List[string]'
$visited = New-Object 'System.Collections.Generic.List[string]'

$proc = Start-Process -FilePath $Exe -PassThru
if (-not $proc) { Write-Host "启动失败" -ForegroundColor Red; exit 1 }

$mainHandle = 0
try {
    # 等主窗口出现，取面积最大的那个作为主窗口
    $deadline = (Get-Date).AddSeconds($WindowTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
        $proc.Refresh()
        if ($proc.HasExited) { break }
        $wins = Get-ProcessWindows $proc.Id
        if ($wins.Count -gt 0) {
            $best = $null; $bestArea = 0
            foreach ($w in $wins) {
                try {
                    $r = $w.Current.BoundingRectangle
                    $area = $r.Width * $r.Height
                    if ($area -gt $bestArea) { $bestArea = $area; $best = $w }
                }
                catch { }
            }
            if ($best -and $bestArea -gt 0) {
                $mainHandle = $best.Current.NativeWindowHandle
                Write-Host ("主窗口: '" + $best.Current.Name + "'  handle=" + $mainHandle + "  面积=" + [int]$bestArea) -ForegroundColor Green
                break
            }
        }
    }

    $proc.Refresh()
    if ($proc.HasExited) {
        $failures.Add("进程启动后立即退出（退出码 $($proc.ExitCode)）。若日志里出现『检测到 WinKit 已在运行』，说明这台机器上还有另一个 WinKit 实例没关掉 —— 那属于测试环境问题，不是本程序的缺陷。")
    }
    elseif ($mainHandle -eq 0) {
        $failures.Add("等待 $WindowTimeoutSeconds 秒仍未找到主窗口")
    }
    else {
        # 精确定位导航项：先找 ListBox 控件，再取它的直接子项
        $procCond = New-Object System.Windows.Automation.PropertyCondition($Auto::ProcessIdProperty, $proc.Id)
        $all = $Auto::RootElement.FindAll($Scope::Descendants, $procCond)
        $listCond = New-Object System.Windows.Automation.PropertyCondition(
            $Auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::List)
        $lists = $all.FindAll($Scope::Descendants, $listCond)

        $itemCond = New-Object System.Windows.Automation.PropertyCondition(
            $Auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)

        $candidates = @()
        foreach ($list in $lists) {
            foreach ($it in $list.FindAll($Scope::Children, $itemCond)) { $candidates += $it }
        }
        if ($candidates.Count -eq 0) {
            $candidates = @($all.FindAll($Scope::Descendants, $itemCond))
        }

        Write-Host ("找到 ListBox " + $lists.Count + " 个，候选导航项 " + $candidates.Count + " 个：") -ForegroundColor Cyan
        foreach ($c in $candidates) {
            try { Write-Host ("   - '" + $c.Current.Name + "'  class=" + $c.Current.ClassName) } catch { }
        }

        # 只点名字在预期标题里的项，避免误点到主题按钮之类的其它元素
        $targets = @()
        foreach ($t in $expectedTitles) {
            foreach ($c in $candidates) {
                try { if ($c.Current.Name -eq $t) { $targets += $c; break } } catch { }
            }
        }
        if ($targets.Count -eq 0) { $targets = $candidates }

        Write-Host ("将依次点开 " + $targets.Count + " 个页面") -ForegroundColor Cyan
        foreach ($item in $targets) {
            $name = ""
            try { $name = $item.Current.Name } catch { }
            if ([string]::IsNullOrWhiteSpace($name)) { $name = "(未命名项)" }

            try {
                $pattern = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
                $pattern.Select()
                $visited.Add($name)
                Write-Host ("  已点开: " + $name) -ForegroundColor Gray
                Start-Sleep -Seconds $PageDwellSeconds
            }
            catch {
                $failures.Add("点击导航项『$name』失败：" + $_.Exception.Message)
                continue
            }

            $proc.Refresh()
            if ($proc.HasExited) { $failures.Add("点开『$name』之后进程退出了"); break }

            $extra = Get-RealExtraWindows $proc.Id $mainHandle
            if ($extra.Count -gt 0) {
                $failures.Add("点开『$name』后出现了额外可见窗口（很可能是未捕获异常导致的弹框）：" + ($extra -join ' / '))
                break
            }
        }
    }
}
catch {
    $failures.Add("UI 自动化过程本身出错：" + $_.Exception.Message)
}
finally {
    $proc.Refresh()
    if (-not $proc.HasExited) {
        try { $proc.Kill(); $proc.WaitForExit(5000) | Out-Null } catch { }
    }
}

# 日志检查
Write-Host ""
Write-Host "=== 日志检查 ===" -ForegroundColor Cyan
$logFile = $null
if (Test-Path $logDir) {
    $logFile = Get-ChildItem $logDir -Filter '*.log' -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

if (-not $logFile) {
    $failures.Add("没有生成日志文件")
}
else {
    $lines = Get-Content $logFile.FullName -Encoding UTF8
    $errors = @($lines | Where-Object { $_ -match '\[ERROR\]' })
    $warns = @($lines | Where-Object { $_ -match '\[WARN \]' })
    Write-Host ("日志: " + $logFile.Name + "  共 " + $lines.Count + " 行 / 错误 " + $errors.Count + " / 警告 " + $warns.Count)

    if ($errors.Count -gt 0) {
        Write-Host "错误明细：" -ForegroundColor Red
        $errors | Select-Object -First 20 | ForEach-Object { Write-Host ("  " + $_) -ForegroundColor Red }
        $failures.Add("日志里有 " + $errors.Count + " 条 ERROR")
    }
    if ($warns.Count -gt 0) {
        Write-Host "警告明细：" -ForegroundColor Yellow
        $warns | Select-Object -First 10 | ForEach-Object { Write-Host ("  " + $_) -ForegroundColor Yellow }
    }
    Write-Host "日志尾部：" -ForegroundColor Gray
    $lines | Select-Object -Last 8 | ForEach-Object { Write-Host ("  " + $_) -ForegroundColor Gray }
}

Write-Host ""
Write-Host ("访问过的页面: " + $(if ($visited.Count -gt 0) { $visited -join ' → ' } else { "（无）" }))

if ($visited.Count -gt 0 -and $visited.Count -lt $expectedTitles.Count) {
    $failures.Add("只点开了 $($visited.Count) 个页面，期望 $($expectedTitles.Count) 个")
}

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "UI 冒烟测试未通过：" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host ("  - " + $f) -ForegroundColor Red }
    exit 1
}

Write-Host "UI 冒烟测试通过：所有导航页都能打开，日志无错误" -ForegroundColor Green
exit 0
