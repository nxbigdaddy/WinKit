# 端到端验证"垃圾清理"的完整用户流程：扫描 → 预览 → 清理 → 撤销。
#
# 为什么要写这个：单元测试只能证明执行器逻辑对，证明不了
#   ① 页面上的"扫描"按钮真的接上了  ② 确认框真的会弹  ③ "撤销上一次清理"真的能还原。
# 这三件事只有真的用鼠标点一遍才知道。
#
# 关键技巧：清理规则用 Environment.ExpandEnvironmentVariables 展开 %TEMP% / %LOCALAPPDATA%，
# 所以给子进程改这两个环境变量，就能把规则目标**重定向到受控目录**，
# 从而在不碰用户任何真实文件的前提下跑完整流程（默认启用的 4 条规则全部落在受控目录里）。
#
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File scripts\ui-cleanup-e2e.ps1

param(
    [string]$Exe = "",
    [int]$StepTimeoutSeconds = 30
)

$ErrorActionPreference = 'Continue'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$Auto = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Exe)) {
    foreach ($c in @((Join-Path $root 'packaging\out\WinKit.exe'),
                     (Join-Path $root 'src\WinKit.App\bin\Debug\WinKit.exe'))) {
        if (Test-Path $c) { $Exe = $c; break }
    }
}
if (-not (Test-Path $Exe)) { Write-Host "找不到 WinKit.exe" -ForegroundColor Red; exit 2 }
$Exe = (Resolve-Path $Exe).Path
$exeDir = Split-Path -Parent $Exe
Write-Host ("被测程序: " + $Exe) -ForegroundColor Cyan

# ---------- 准备受控目录 ----------
$sandbox = Join-Path $env:LOCALAPPDATA 'winkit-e2e-sandbox'
$fakeTemp = Join-Path $sandbox 'temp'          # 冒充 %TEMP%
$fakeLocalAppData = Join-Path $sandbox 'appdata'  # 冒充 %LOCALAPPDATA%（空目录 → 另 3 条规则找不到东西）
foreach ($d in @($sandbox, $fakeTemp, $fakeLocalAppData)) {
    Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $d | Out-Null
}

$dummyFiles = @()
for ($i = 1; $i -le 3; $i++) {
    $f = Join-Path $fakeTemp ("e2e-dummy-$i.tmp")
    Set-Content -Path $f -Value ("WinKit E2E test payload " + $i) -Encoding UTF8
    # 回拨 3 天：规则默认有 1 小时的"最小年龄"门槛，新文件会被判成"太新"而跳过
    (Get-Item $f).LastWriteTime = (Get-Date).AddDays(-3)
    $dummyFiles += $f
}
Write-Host ("沙箱临时目录: " + $fakeTemp + "（放了 " + $dummyFiles.Count + " 个 3 天前的文件）")

$quarantine = Join-Path $exeDir 'WinKit-data\quarantine'
Write-Host ("隔离区目录  : " + $quarantine)

$failures = New-Object 'System.Collections.Generic.List[string]'

# ---------- UIA 辅助 ----------
function Get-ProcessWindows([int]$processId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($Auto::ProcessIdProperty, $processId)
    return $Auto::RootElement.FindAll($Scope::Children, $cond)
}

function Get-MainWindow([int]$processId) {
    $best = $null; $bestArea = 0
    foreach ($w in (Get-ProcessWindows $processId)) {
        try {
            $r = $w.Current.BoundingRectangle
            $area = $r.Width * $r.Height
            if ($area -gt $bestArea) { $bestArea = $area; $best = $w }
        } catch { }
    }
    return $best
}

# 注意：这个函数的参数**不能**叫 $scope —— PowerShell 变量名大小写不敏感，
# 它会遮蔽脚本级变量 $Scope（TreeScope 枚举），导致 $Scope::Descendants 取不到值。
function Find-ByName($element, [string]$name) {
    if ($null -eq $element) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition($Auto::NameProperty, $name)
    return $element.FindFirst($Scope::Descendants, $cond)
}

function Invoke-Element($element) {
    if ($null -eq $element) { return $false }
    try { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch { }
    try { $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); return $true } catch { }
    try { $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); return $true } catch { }
    return $false
}

# 把元素滚进可视区。
#
# 为什么必须做这一步：本页内容比窗口高，"执行清理"按钮初始位于滚动视口之外
# （实测按钮 Y=1453，而窗口纵向只覆盖 160~880）。对不在视口内的元素，
# UIA 的 InvokePattern.Invoke() 不会真正触发 Click —— 表现为"点了没反应"，
# 很容易被误判成产品缺陷。真实用户是先滚动再点的，测试也必须先滚动。
function Ensure-Visible($main, $element) {
    if ($null -eq $main -or $null -eq $element) { return $false }

    try {
        $element.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView()
        Start-Sleep -Milliseconds 400
    } catch { }

    for ($i = 0; $i -lt 25; $i++) {
        try {
            $wr = $main.Current.BoundingRectangle
            $er = $element.Current.BoundingRectangle
            if ($er.Width -gt 0 -and $er.Y -ge $wr.Y -and ($er.Y + $er.Height) -le ($wr.Y + $wr.Height)) {
                return $true
            }
        } catch { return $false }

        # 退化为直接操作 ScrollViewer
        $found = $false
        try {
            $spCond = New-Object System.Windows.Automation.PropertyCondition(
                $Auto::IsScrollPatternAvailableProperty, $true)
            $sv = $main.FindFirst($Scope::Descendants, $spCond)
            if ($sv) {
                $sp = $sv.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
                $sp.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount,
                           [System.Windows.Automation.ScrollAmount]::LargeIncrement)
                $found = $true
            }
        } catch { }
        if (-not $found) { return $false }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

# 找元素 → 滚进视口 → 触发。返回 $true 表示触发成功。
function Click-Control($main, [string]$name) {
    $element = Find-ByName $main $name
    if ($null -eq $element) { return $false }
    [void](Ensure-Visible $main $element)

    # 滚动后重新取一次（列表可能已重建）
    $element = Find-ByName $main $name
    if ($null -eq $element) { return $false }
    return Invoke-Element $element
}

# ---- 模态对话框处理（走 Win32，不用 UIA）----
#
# 为什么不用 UIA 找对话框：这个进程有 10 个以上的顶层窗口
# （CiceroUIWndFrame / MSCTFIME UI / MediaContextNotificationWindow 等隐藏辅助窗口），
# 实测 UIA 的 FindAll(Children) 既看不到确认框、也不报错 —— 会得出"确认框没弹"的
# **错误结论**，而 EnumWindows 能稳定地看到 `#32770 | '执行清理确认' | visible=True`。
# 另外，对不在滚动视口内的元素 UIA 的 Invoke 也不会真正触发 Click。
# 教训：验证 UI 时，Win32 窗口层面的证据比 UIA 更可靠。
Add-Type -Namespace W32 -Name Dlg -MemberDefinition @'
public delegate bool EnumProc(System.IntPtr h, System.IntPtr p);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, System.IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, ref uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(System.IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(System.IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool PostMessageW(System.IntPtr h, uint msg, System.IntPtr w, System.IntPtr l);
public const uint WM_COMMAND = 0x0111;
public const int IDOK = 1;
'@

$script:dlgHandle = [IntPtr]::Zero
$script:dlgTitle = ''

# 找一个属于该进程、可见、类名为 #32770（标准对话框窗口类）的顶层窗口。
function Find-DialogWindow([int]$processId) {
    $target = [uint32]$processId
    $script:dlgHandle = [IntPtr]::Zero
    $script:dlgTitle = ''
    $cb = [W32.Dlg+EnumProc]{
        param($h, $l)
        $procId = [uint32]0
        [void][W32.Dlg]::GetWindowThreadProcessId($h, [ref]$procId)
        if ($procId -eq $target -and [W32.Dlg]::IsWindowVisible($h)) {
            $cls = New-Object System.Text.StringBuilder 64
            [void][W32.Dlg]::GetClassNameW($h, $cls, 64)
            if ($cls.ToString() -eq '#32770') {
                $title = New-Object System.Text.StringBuilder 512
                [void][W32.Dlg]::GetWindowTextW($h, $title, 512)
                $script:dlgHandle = $h
                $script:dlgTitle = $title.ToString()
                return $false   # 找到就停止枚举
            }
        }
        return $true
    }
    [void][W32.Dlg]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:dlgHandle
}

# 依次按掉最多 max 个对话框（确认框 → 结果框），每按一个返回计数。
# 用 WM_COMMAND + IDOK：标准 MessageBox 的「确定」按钮控件 ID 就是 IDOK。
function Dismiss-Dialogs([int]$processId, [int]$max = 3, [int]$timeoutSeconds = 25) {
    $dismissed = 0
    for ($n = 0; $n -lt $max; $n++) {
        $deadline = (Get-Date).AddSeconds($timeoutSeconds)
        $handle = [IntPtr]::Zero
        while ((Get-Date) -lt $deadline) {
            $handle = Find-DialogWindow $processId
            if ($handle -ne [IntPtr]::Zero) { break }
            Start-Sleep -Milliseconds 250
        }
        if ($handle -eq [IntPtr]::Zero) { break }

        Write-Host ("    检测到对话框『" + $script:dlgTitle + "』，按下「确定」") -ForegroundColor DarkGray
        [void][W32.Dlg]::PostMessageW($handle, [W32.Dlg]::WM_COMMAND, [IntPtr][W32.Dlg]::IDOK, [IntPtr]::Zero)
        $dismissed++
        Start-Sleep -Milliseconds 900
    }
    return $dismissed
}

function Count-Candidates($main) {
    if ($null -eq $main) { return -1 }
    $cond = New-Object System.Windows.Automation.PropertyCondition($Auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)
    return $main.FindAll($Scope::Descendants, $cond).Count
}

# ---------- 启动（带环境变量重定向）----------
Get-Process -Name WinKit -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
Start-Sleep -Seconds 1

$savedTemp = $env:TEMP; $savedTmp = $env:TMP; $savedLad = $env:LOCALAPPDATA
$env:TEMP = $fakeTemp
$env:TMP = $fakeTemp
$env:LOCALAPPDATA = $fakeLocalAppData
$proc = Start-Process -FilePath $Exe -PassThru
$env:TEMP = $savedTemp; $env:TMP = $savedTmp; $env:LOCALAPPDATA = $savedLad

$main = $null; $mainHandle = 0
try {
    # 等主窗口
    $deadline = (Get-Date).AddSeconds($StepTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
        $proc.Refresh()
        if ($proc.HasExited) { break }
        $main = Get-MainWindow $proc.Id
        if ($main) { $mainHandle = $main.Current.NativeWindowHandle; break }
    }
    if (-not $main) { throw "主窗口没出现" }
    Write-Host ("主窗口已就绪，handle=" + $mainHandle) -ForegroundColor Green

    # ---- 步骤 1：打开垃圾清理页 ----
    Write-Host ""
    Write-Host "① 打开『垃圾清理』页面" -ForegroundColor Cyan
    if (-not (Invoke-Element (Find-ByName $main '垃圾清理'))) { throw "导航里找不到『垃圾清理』" }
    Start-Sleep -Seconds 3
    $proc.Refresh(); if ($proc.HasExited) { throw "打开清理页后进程退出了（页面很可能抛异常）" }

    # ---- 步骤 2：点扫描 ----
    Write-Host "② 点击『扫描』" -ForegroundColor Cyan
    if (-not (Click-Control $main '扫描')) { throw "找不到或点不动『扫描』按钮" }
    Start-Sleep -Seconds 6
    $proc.Refresh(); if ($proc.HasExited) { throw "扫描过程中进程退出了" }
    Dismiss-Dialogs $proc.Id 1 3 | Out-Null

    $candidates = Count-Candidates $main
    Write-Host ("    扫描后候选行数: " + $candidates)
    if ($candidates -lt 3) {
        $failures.Add("扫描后候选行数只有 $candidates，期望至少 3（沙箱里放了 3 个 3 天前的文件）")
    }

    # ---- 步骤 3：点执行清理 ----
    Write-Host "③ 点击『执行清理』（会弹确认框）" -ForegroundColor Cyan
    if (-not (Click-Control $main '执行清理')) { throw "找不到或点不动『执行清理』按钮" }
    $dialogs = Dismiss-Dialogs $proc.Id 3 25
    Write-Host ("    关掉了 " + $dialogs + " 个对话框")
    if ($dialogs -lt 1) {
        $failures.Add("点『执行清理』后没有弹出确认对话框 —— 危险操作缺少确认是不可接受的")
    }
    Start-Sleep -Seconds 3

    # ---- 步骤 4：核对文件去向 ----
    Write-Host "④ 核对：原文件应已消失、隔离区里应出现它们" -ForegroundColor Cyan
    $stillThere = @($dummyFiles | Where-Object { Test-Path $_ })
    $quarantined = @()
    if (Test-Path $quarantine) {
        $quarantined = @(Get-ChildItem $quarantine -Recurse -File -ErrorAction SilentlyContinue)
    }
    Write-Host ("    沙箱里剩余: " + $stillThere.Count + " / 隔离区文件数: " + $quarantined.Count)
    if ($stillThere.Count -gt 0) { $failures.Add("清理后仍有 " + $stillThere.Count + " 个沙箱文件没被移走") }
    if ($quarantined.Count -lt 3) { $failures.Add("隔离区里只有 " + $quarantined.Count + " 个文件，期望至少 3") }

    # ---- 步骤 5：点撤销 ----
    Write-Host "⑤ 点击『撤销上一次清理』" -ForegroundColor Cyan
    if (-not (Click-Control $main '撤销上一次清理')) { throw "找不到或点不动『撤销上一次清理』按钮" }
    Dismiss-Dialogs $proc.Id 2 25 | Out-Null
    Start-Sleep -Seconds 3

    # ---- 步骤 6：核对已还原 ----
    Write-Host "⑥ 核对：文件应已回到沙箱目录且内容一致" -ForegroundColor Cyan
    $restored = 0
    foreach ($f in $dummyFiles) {
        if (Test-Path $f) {
            $text = Get-Content $f -Raw -Encoding UTF8
            if ($text -match 'WinKit E2E test payload') { $restored++ }
        }
    }
    Write-Host ("    成功还原且内容正确: " + $restored + " / " + $dummyFiles.Count)
    if ($restored -ne $dummyFiles.Count) {
        $failures.Add("撤销后只有 $restored/" + $dummyFiles.Count + " 个文件回到原位且内容正确")
    }
}
catch {
    $failures.Add("端到端流程出错：" + $_.Exception.Message)
}
finally {
    $proc.Refresh()
    if (-not $proc.HasExited) {
        try { $proc.Kill(); $proc.WaitForExit(5000) | Out-Null } catch { }
    }
}

# ---------- 日志检查 ----------
Write-Host ""
Write-Host "=== 日志检查 ===" -ForegroundColor Cyan
$logDir = Join-Path $exeDir 'WinKit-data\logs'
if (Test-Path $logDir) {
    $log = Get-ChildItem $logDir -Filter '*.log' -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($log) {
        $lines = Get-Content $log.FullName -Encoding UTF8
        $errors = @($lines | Where-Object { $_ -match '\[ERROR\]' })
        Write-Host ("日志: " + $log.Name + "  共 " + $lines.Count + " 行 / 错误 " + $errors.Count)
        if ($errors.Count -gt 0) {
            $errors | Select-Object -First 10 | ForEach-Object { Write-Host ("  " + $_) -ForegroundColor Red }
            $failures.Add("日志里有 " + $errors.Count + " 条 ERROR")
        }
        $lines | Select-Object -Last 14 | ForEach-Object { Write-Host ("  " + $_) -ForegroundColor Gray }
    }
}

# ---------- 清理沙箱 ----------
Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "清理端到端验证未通过：" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host ("  - " + $f) -ForegroundColor Red }
    exit 1
}
Write-Host "清理端到端验证通过：扫描 → 预览 → 确认 → 清理（进隔离区）→ 撤销还原，全链路可用" -ForegroundColor Green
exit 0
