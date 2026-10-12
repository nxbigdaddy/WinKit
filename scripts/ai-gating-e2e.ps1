# 验证 AI 助手的"闸门"行为：未启用时，点 AI 按钮必须一次请求都不发。
#
# 为什么这是整个 AI 功能里最该自动化的一条：
#   隐私承诺已改为"默认不联网、可选联网"，而这条承诺的用户可见部分全在**闸门**上 ——
#   未启用/无 key 时点下去只应该得到一句解释，绝不该建预览窗口、更不该联网。
#   "只有一个网络出口"由 check-conventions.ps1 静态守住；"未启用时不发请求"只能靠实测。
#
# 本脚本**不需要 API Key**，因此可以放进每次验收里跑。
#
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File scripts\ai-gating-e2e.ps1

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

Write-Host ("被测程序: " + $Exe) -ForegroundColor Cyan

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -Namespace W -Name AiGate -MemberDefinition @'
public delegate bool EnumProc(System.IntPtr h, System.IntPtr p);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, System.IntPtr p);
[DllImport("user32.dll")] public static extern bool EnumChildWindows(System.IntPtr parent, EnumProc cb, System.IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, ref uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(System.IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(System.IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool PostMessageW(System.IntPtr h, uint msg, System.IntPtr w, System.IntPtr l);
public const uint WM_COMMAND = 0x0111; public const int IDOK = 1;
'@

$Auto = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
$failures = New-Object 'System.Collections.Generic.List[string]'

function Assert-True([bool]$condition, [string]$message) {
    if ($condition) { Write-Host ("  [OK]   " + $message) -ForegroundColor Green }
    else { Write-Host ("  [FAIL] " + $message) -ForegroundColor Red; $failures.Add($message) }
}

# 干净起点：清掉日志，保证"没有 AI 请求"这条断言不受到历史记录影响
Get-Process -Name WinKit -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
Start-Sleep -Seconds 1
if (Test-Path $logDir) { Remove-Item (Join-Path $logDir '*.log') -Force -ErrorAction SilentlyContinue }

# ---- ① 默认配置必须关着 AI ----
Write-Host ""
Write-Host "① 默认配置" -ForegroundColor Cyan
$configPath = Join-Path $dataDir 'config.json'
if (-not (Test-Path $configPath)) {
    # 首次运行还没有配置文件：启动一次让它生成
    $warm = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Seconds 5
    if (-not $warm.HasExited) { $warm.Kill(); $warm.WaitForExit(5000) | Out-Null }
    Start-Sleep -Seconds 1
}
$config = if (Test-Path $configPath) { Get-Content $configPath -Raw -Encoding UTF8 } else { "" }
Assert-True ($config -notmatch '"aiEnabled"\s*:\s*true') '配置里 aiEnabled 不是 true（默认关闭）'

# ---- 启动并打开体检页 ----
$proc = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 6

$cond = New-Object System.Windows.Automation.PropertyCondition($Auto::ProcessIdProperty, $proc.Id)
$wins = $Auto::RootElement.FindAll($Scope::Children, $cond)
$main = $null; $best = 0
foreach ($w in $wins) {
    $r = $w.Current.BoundingRectangle
    if ($r.Width * $r.Height -gt $best) { $best = $r.Width * $r.Height; $main = $w }
}
if (-not $main) {
    Write-Host "主窗口没出现" -ForegroundColor Red
    if (-not $proc.HasExited) { $proc.Kill() }
    exit 1
}
$mainHandle = $main.Current.NativeWindowHandle
function Find-N($el, [string]$n) {
    if (-not $el) { return $null }
    return $el.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($Auto::NameProperty, $n)))
}

Write-Host ""
Write-Host "② 在未启用状态下点『用 AI 解释』" -ForegroundColor Cyan
$nav = Find-N $main '一键体检'
if ($nav) { $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
Start-Sleep -Seconds 3

$button = Find-N $main '用 AI 解释'
Assert-True ($null -ne $button) '体检页存在『用 AI 解释』按钮'
if ($button) {
    try { $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { }
}
Start-Sleep -Seconds 3

# ---- ③ 必须弹出一句解释，且指向设置页 ----
Write-Host ""
Write-Host "③ 说明框" -ForegroundColor Cyan
$target = [uint32]$proc.Id
$script:dialog = [IntPtr]::Zero
$cb = [W.AiGate+EnumProc]{
    param($h, $l)
    $pp = [uint32]0
    [void][W.AiGate]::GetWindowThreadProcessId($h, [ref]$pp)
    if ($pp -eq $target -and $h -ne $mainHandle -and [W.AiGate]::IsWindowVisible($h)) {
        $c = New-Object System.Text.StringBuilder 64
        [void][W.AiGate]::GetClassNameW($h, $c, 64)
        if ($c.ToString() -eq '#32770') { $script:dialog = $h; return $false }
    }
    return $true
}
[void][W.AiGate]::EnumWindows($cb, [IntPtr]::Zero)

Assert-True ($script:dialog -ne [IntPtr]::Zero) '弹出了说明框（而不是静默什么都不做）'

$dialogText = ""
if ($script:dialog -ne [IntPtr]::Zero) {
    $texts = New-Object 'System.Collections.Generic.List[string]'
    $cb2 = [W.AiGate+EnumProc]{
        param($h, $l)
        $c = New-Object System.Text.StringBuilder 64
        [void][W.AiGate]::GetClassNameW($h, $c, 64)
        if ($c.ToString() -eq 'Static') {
            $t = New-Object System.Text.StringBuilder 1024
            [void][W.AiGate]::GetWindowTextW($h, $t, 1024)
            if ($t.Length -gt 0) { $texts.Add($t.ToString()) }
        }
        return $true
    }
    [void][W.AiGate]::EnumChildWindows($script:dialog, $cb2, [IntPtr]::Zero)
    $dialogText = ($texts -join "`n")
    Write-Host ("  实际文案: " + ($dialogText -replace "`n", ' / ')) -ForegroundColor Gray

    Assert-True ($dialogText -match '未启用|未配置|不可用') '文案说明了"为什么不能用"'
    Assert-True ($dialogText -match '设置') '文案指出了"去哪里配置"'

    [void][W.AiGate]::PostMessageW($script:dialog, [W.AiGate]::WM_COMMAND, [IntPtr][W.AiGate]::IDOK, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 800
}

# ---- ④ 绝不能出现预览窗口 ----
Write-Host ""
Write-Host "④ 不应出现预览窗口" -ForegroundColor Cyan
$extra = @()
foreach ($w in $Auto::RootElement.FindAll($Scope::Children, $cond)) {
    if ($w.Current.NativeWindowHandle -eq $mainHandle) { continue }
    if ($w.Current.NativeWindowHandle -eq $script:dialog) { continue }
    $r = $w.Current.BoundingRectangle
    if ($r.Width -ge 60 -and $r.Height -ge 40 -and -not $w.Current.IsOffscreen) {
        $extra += ("'" + $w.Current.Name + "'")
    }
}
Assert-True ($extra.Count -eq 0) ("未出现预览窗口或其它窗口（实际: " + $(if ($extra.Count -eq 0) { "无" } else { $extra -join ' / ' }) + "）")

$proc.Refresh()
if (-not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(5000) | Out-Null }

# ---- ⑤ 日志里不能有请求记录 ----
Write-Host ""
Write-Host "⑤ 没有发出任何 AI 请求" -ForegroundColor Cyan
$log = $null
if (Test-Path $logDir) {
    $log = Get-ChildItem $logDir -Filter '*.log' -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
}
if ($log) {
    $aiLines = @(Get-Content $log.FullName -Encoding UTF8 | Where-Object { $_ -match 'AI 请求|AI 响应' })
    Assert-True ($aiLines.Count -eq 0) ("日志里没有 AI 请求记录（实际 " + $aiLines.Count + " 条）")
} else {
    Assert-True $false '没有生成日志文件'
}

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "AI 闸门验证未通过：" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host ("  - " + $f) -ForegroundColor Red }
    exit 1
}
Write-Host "AI 闸门验证通过：未启用时给出可行动的解释，不建预览窗口、不发送任何请求" -ForegroundColor Green
exit 0
