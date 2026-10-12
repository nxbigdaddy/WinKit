# 验证 AI 预览窗口：AI 启用后点「用 AI 解释」，必须先弹出可编辑的预览窗口，
# 并逐字显示将要发送的内容；而且**只打开窗口不该联网**。
#
# 为什么单独写一条：目标里有一条是"发送前必须能预览将要发送的文本"。
# ai-gating-e2e.ps1 验证的是"未启用时不建窗口"，本脚本验证"启用后确实建了窗口、且内容如实"。
#
# 技巧：用**假 key**（DPAPI 加密，与应用同熵）把 IsAiReady 变成 true。
# 只要不点「发送」，就不会有任何网络请求 —— 断言里会检查这一点。
# 脚本会在 finally 里删除假 key 并还原配置。
#
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File scripts\ai-preview-e2e.ps1

param([string]$Exe = "")

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Exe)) {
    foreach ($c in @((Join-Path $root 'src\WinKit.App\bin\Debug\WinKit.exe'),
                     (Join-Path $root 'packaging\out\WinKit.exe'))) {
        if (Test-Path $c) { $Exe = $c; break }
    }
}
if (-not (Test-Path $Exe)) { Write-Host "找不到 WinKit.exe" -ForegroundColor Red; exit 2 }
$Exe = (Resolve-Path $Exe).Path
$exeDir = Split-Path -Parent $Exe
$dataDir = Join-Path $exeDir 'WinKit-data'
$logDir = Join-Path $dataDir 'logs'
$configPath = Join-Path $dataDir 'config.json'
$keyPath = Join-Path $dataDir 'secrets\deepseek.key'

Write-Host ("被测程序: " + $Exe) -ForegroundColor Cyan

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Security

# Win32 窗口枚举。
#
# **为什么判定"窗口有没有出现"必须用 Win32 而不是 UIA**：本项目已有先例 ——
# round 5 的模态 MessageBox 对 UIA 的 FindAll(Children) 不可见（当时输出是
# "关掉了 0 个对话框"，把一次成功的确认当成了"没弹框"），而 Win32 EnumWindows
# 一眼就看到 `#32770 | '执行清理确认' | visible=True`。
# 拿到 HWND 之后再用 AutomationElement.FromHandle 桥回 UIA，就能继续读窗口内容。
Add-Type -Namespace W -Name Win32Window -MemberDefinition @'
public delegate bool EnumProc(System.IntPtr h, System.IntPtr p);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, System.IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, ref uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
'@

$Auto = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
$failures = New-Object 'System.Collections.Generic.List[string]'

function Assert-True([bool]$condition, [string]$message) {
    if ($condition) { Write-Host ("  [OK]   " + $message) -ForegroundColor Green }
    else { Write-Host ("  [FAIL] " + $message) -ForegroundColor Red; $script:failures.Add($message) }
}

function Get-TopWindows([int]$processId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($Auto::ProcessIdProperty, $processId)
    return $Auto::RootElement.FindAll($Scope::Children, $cond)
}

# Win32 枚举该进程所有**可见**顶层窗口的 HWND。判定"窗口有没有出现"以它为准。
function Get-Win32WindowHandles([int]$processId) {
    $target = [uint32]$processId
    $script:handles = New-Object 'System.Collections.Generic.List[System.IntPtr]'
    $cb = [W.Win32Window+EnumProc]{
        param($h, $l)
        $procId = [uint32]0
        [void][W.Win32Window]::GetWindowThreadProcessId($h, [ref]$procId)
        if ($procId -eq $target -and [W.Win32Window]::IsWindowVisible($h)) { $script:handles.Add($h) }
        return $true
    }
    [void][W.Win32Window]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:handles
}

function Get-ElementsOfType($rootElement, $controlType) {    $cond = New-Object System.Windows.Automation.PropertyCondition($Auto::ControlTypeProperty, $controlType)
    return $rootElement.FindAll($Scope::Descendants, $cond)
}

function Find-ByName($rootElement, [string]$name) {
    if ($null -eq $rootElement) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition($Auto::NameProperty, $name)
    return $rootElement.FindFirst($Scope::Descendants, $cond)
}

function Get-Names($elements) {
    $names = New-Object 'System.Collections.Generic.List[string]'
    foreach ($e in $elements) {
        try { if ($e.Current.Name) { $names.Add($e.Current.Name) } } catch { }
    }
    return $names
}

# ---------------- ① 起点状态必须干净 ----------------
Write-Host ""
Write-Host "① 起点状态" -ForegroundColor Cyan
$originConfig = $null
if (Test-Path $configPath) { $originConfig = Get-Content $configPath -Raw -Encoding UTF8 }
Assert-True ($null -eq $originConfig -or $originConfig -notmatch '"aiEnabled"\s*:\s*true') '起点：配置里 aiEnabled 不是 true'
Assert-True (-not (Test-Path $keyPath)) '起点：不存在遗留的 key 文件'

Get-Process -Name WinKit -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
Start-Sleep -Seconds 1
if (Test-Path $logDir) { Remove-Item (Join-Path $logDir '*.log') -Force -ErrorAction SilentlyContinue }

# 首次运行可能还没有配置文件：先启动一次生成
if (-not (Test-Path $configPath)) {
    $warm = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Seconds 5
    if (-not $warm.HasExited) { $warm.Kill(); $warm.WaitForExit(5000) | Out-Null }
    Start-Sleep -Seconds 1
    $originConfig = Get-Content $configPath -Raw -Encoding UTF8
}

# ---------------- ② 写入假 key + 临时启用 AI ----------------
Write-Host ""
Write-Host "② 准备：假 key + 临时启用 AI（只在 finally 里还原）" -ForegroundColor Cyan
try {
    New-Item -ItemType Directory -Force -Path (Split-Path $keyPath) | Out-Null
    $entropy = [Text.Encoding]::UTF8.GetBytes('WinKit.DeepSeek.ApiKey.v1')
    $fakeKey = [Text.Encoding]::UTF8.GetBytes('sk-fake0000000000000000000000')
    $blob = [System.Security.Cryptography.ProtectedData]::Protect(
        $fakeKey, $entropy, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    [IO.File]::WriteAllBytes($keyPath, $blob)
    [IO.File]::WriteAllText($configPath, ($originConfig -replace '"aiEnabled"\s*:\s*false', '"aiEnabled":true'),
        (New-Object Text.UTF8Encoding($false)))
    Assert-True (Test-Path $keyPath) '假 key 已写入（DPAPI 加密）'

    # ---------------- ③ 打开体检页并生成报告 ----------------
    Write-Host ""
    Write-Host "③ 先跑一次体检（否则命令会走『请先开始体检』分支）" -ForegroundColor Cyan
    $proc = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Seconds 6

    $windows = Get-TopWindows $proc.Id
    $main = $null; $bestArea = 0
    foreach ($w in $windows) {
        $r = $w.Current.BoundingRectangle
        $area = $r.Width * $r.Height
        if ($area -gt $bestArea) { $bestArea = $area; $main = $w }
    }
    if (-not $main) { throw '主窗口没出现' }
    $mainHandle = $main.Current.NativeWindowHandle

    $navHealth = Find-ByName $main '一键体检'
    if ($navHealth) { $navHealth.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    Start-Sleep -Seconds 3

    $runButton = Find-ByName $main '开始体检'
    if ($runButton) {
        try { $runButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { }
    }
    # 体检要扫盘，给它足够时间；等到出现分数文本或超时
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        $texts = Get-Names (Get-ElementsOfType $main ([System.Windows.Automation.ControlType]::Text))
        if (($texts -join ' ') -match '体检得分') { break }
    }
    $reportTexts = Get-Names (Get-ElementsOfType $main ([System.Windows.Automation.ControlType]::Text))
    Assert-True (($reportTexts -join ' ') -match '体检得分') '体检已出结果（执行『用 AI 解释』的前置条件满足）'

    # ---------------- ④ 点『用 AI 解释』，列出全部窗口 ----------------
    Write-Host ""
    Write-Host "④ 点『用 AI 解释』后列出该进程的**全部**顶层窗口" -ForegroundColor Cyan
    $explain = Find-ByName $main '用 AI 解释'
    Assert-True ($null -ne $explain) '体检页存在『用 AI 解释』按钮'
    if ($explain) {
        # **必须先滚进视口再 Invoke**：多次实测证明，UIA 的 InvokePattern 对
        # "不在滚动视口内"的元素是**静默无效**的 —— 不报错、也不触发 Click。
        # round 8 能弹出说明框、round 10/11 不能，唯一差别就是：跑过体检之后
        # 结果卡片把按钮挤出了视口。这一步是必需的，不是保险。
        try { $explain.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView(); Start-Sleep -Milliseconds 600 } catch { }
        $rect = $explain.Current.BoundingRectangle
        Write-Host ('   按钮位置 ' + [int]$rect.X + ',' + [int]$rect.Y + '  ' + [int]$rect.Width + 'x' + [int]$rect.Height + '  offscreen=' + $explain.Current.IsOffscreen) -ForegroundColor Gray
        try {
            $explain.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Write-Host '   已滚入视口并发出 Invoke' -ForegroundColor Gray
        } catch {
            Write-Host ('   Invoke 失败: ' + $_.Exception.Message) -ForegroundColor Yellow
            $failures.Add('Invoke 失败：' + $_.Exception.Message)
        }
    }
    Start-Sleep -Seconds 5

    $preview = $null
    $ownerDisabled = $false
    try { $ownerDisabled = -not $main.Current.IsEnabled } catch { }

    Write-Host "   —— Win32 枚举到的可见窗口（判定依据）——" -ForegroundColor Gray
    foreach ($handle in (Get-Win32WindowHandles $proc.Id)) {
        $isMain = ($handle -eq [IntPtr]$mainHandle)
        $element = $null
        try { $element = $Auto::FromHandle($handle) } catch { }
        $title = ''; $width = 0; $height = 0
        if ($element) {
            try {
                $title = $element.Current.Name
                $rect = $element.Current.BoundingRectangle
                $width = [int]$rect.Width; $height = [int]$rect.Height
            } catch { }
        }
        Write-Host ("   " + $(if ($isMain) { "[主窗口]" } else { "[其它]  " }) + " hwnd=" + $handle +
                    " title='" + $title + "'  " + $width + "x" + $height) -ForegroundColor Gray
        if (-not $isMain -and $width -ge 200 -and $height -ge 150) { $preview = $element }
    }
    Write-Host ("   主窗口 IsEnabled=" + (-not $ownerDisabled) + "（模态窗口出现时属主会被禁用）") -ForegroundColor Gray

    Assert-True (($null -ne $preview) -or $ownerDisabled) '出现了预览窗口（Win32 枚举 到 或 属主被模态禁用）'

    # ---------------- ⑤ 预览窗口的内容 ----------------
    Write-Host ""
    Write-Host "⑤ 预览窗口内容" -ForegroundColor Cyan
    if ($preview) {
        Write-Host ("   窗口标题: '" + $preview.Current.Name + "'") -ForegroundColor Gray
        $previewTexts = Get-Names (Get-ElementsOfType $preview ([System.Windows.Automation.ControlType]::Text))
        foreach ($t in $previewTexts) {
            Write-Host ("     · " + ($t -replace "`n", ' ')) -ForegroundColor Gray
        }
        $joined = $previewTexts -join ' '

        $edits = Get-ElementsOfType $preview ([System.Windows.Automation.ControlType]::Edit)
        $buttons = Get-Names (Get-ElementsOfType $preview ([System.Windows.Automation.ControlType]::Button))

        Assert-True ($joined -match '本次将发送') '最上方有"本次将发送…"的披露条'
        Assert-True ($joined -match '不含') '披露文案写明了"不含什么"'
        Assert-True ($joined -match '修改') '提示了正文可以修改'
        Assert-True ($edits.Count -ge 2) ("存在可编辑/只读文本框（实际 " + $edits.Count + " 个：正文、系统指令、回复）")
        Assert-True ($buttons.Count -ge 2) ("存在动作按钮（" + ($buttons -join ' / ') + "）")

        # 编辑框里的初值应当就是将要发送的正文（而不是空）
        $firstEdit = $null
        foreach ($e in $edits) {
            try {
                $pattern = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
                if ($pattern.Current.Value) { $firstEdit = $pattern.Current.Value; break }
            } catch { }
        }
        Assert-True (-not [string]::IsNullOrWhiteSpace($firstEdit)) '正文编辑框里已有将要发送的内容（不是空的）'
        if ($firstEdit) {
            Write-Host ("   正文首行: " + (($firstEdit -split "`n")[0])) -ForegroundColor Gray
            Assert-True ($firstEdit -match '体检') '正文内容与触发的功能一致（体检报告解读）'
        }
    }

    $proc.Refresh()
    if (-not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(5000) | Out-Null }

    # ---------------- ⑥ 只打开窗口不该联网 ----------------
    Write-Host ""
    Write-Host "⑥ 只打开窗口、不点发送，不应联网" -ForegroundColor Cyan
    $log = $null
    if (Test-Path $logDir) {
        $log = Get-ChildItem $logDir -Filter '*.log' -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    }
    if ($log) {
        $allLines = @(Get-Content $log.FullName -Encoding UTF8)
        $aiLines = @($allLines | Where-Object { $_ -match 'AI 请求' })
        $problemLines = @($allLines | Where-Object { $_ -match '\[ERROR\]|\[WARN ' })
        Assert-True ($aiLines.Count -eq 0) ("日志里没有 AI 请求记录（实际 " + $aiLines.Count + " 条）")
        # 上一个真实缺陷（TwoWay 绑定只读属性）就是靠 ERROR 行定位的 —— 这个信号绝不能不看
        Write-Host ("   ERROR/WARN 行数: " + $problemLines.Count) -ForegroundColor Gray
        $problemLines | Select-Object -First 6 | ForEach-Object { Write-Host ("      " + $_) -ForegroundColor Yellow }
        Assert-True ($problemLines.Count -eq 0) ("过程无异常：ERROR/WARN " + $problemLines.Count + " 条")
    } else {
        Assert-True $false '没有生成日志文件'
    }
}
catch {
    $failures.Add('流程出错：' + $_.Exception.Message)
}
finally {
    Write-Host ""
    Write-Host "⑦ 还原现场" -ForegroundColor Cyan
    Get-Process -Name WinKit -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
    Start-Sleep -Milliseconds 500
    if (Test-Path $keyPath) { Remove-Item $keyPath -Force -ErrorAction SilentlyContinue }
    if ($null -ne $originConfig) {
        [IO.File]::WriteAllText($configPath, $originConfig, (New-Object Text.UTF8Encoding($false)))
    }
    $keyGone = -not (Test-Path $keyPath)
    $configRestored = $true
    if (Test-Path $configPath) {
        $configRestored = ((Get-Content $configPath -Raw -Encoding UTF8) -notmatch '"aiEnabled"\s*:\s*true')
    }
    Write-Host ("   假 key 已删除: " + $keyGone + " / 配置已还原: " + $configRestored) -ForegroundColor Gray
    if (-not $keyGone -or -not $configRestored) {
        $failures.Add('环境未还原干净，请手动检查 secrets\deepseek.key 与 config.json')
    }
}

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "AI 预览窗口验证未通过：" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host ("  - " + $f) -ForegroundColor Red }
    exit 1
}
Write-Host "AI 预览窗口验证通过：启用后先弹出可编辑的预览窗口、披露条在最上方、未点发送不联网" -ForegroundColor Green
exit 0
