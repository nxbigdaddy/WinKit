# 一键跑完全部验收步骤。
#
# 为什么需要它：验收不能靠"我记得跑过哪些检查"。这个脚本按固定顺序执行全部闸门，
# 任一步失败都会汇总出来。每一步的详细输出都会打印，最后给一张汇总表。
#
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-all.ps1
#      -SkipUi        跳过 UI 自动化（不弹窗口）
#      -SkipTests     跳过单元测试（快速自检时用）

param(
    [switch]$SkipUi,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# 先清掉可能残留的实例，否则单实例逻辑会让被测程序直接退出、造成假失败
Get-Process -Name WinKit -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
Start-Sleep -Seconds 1

$results = New-Object 'System.Collections.Generic.List[object]'

function Invoke-Step {
    param([string]$Name, [scriptblock]$Body)
    Write-Host ""
    Write-Host ("########## " + $Name + " ##########") -ForegroundColor Cyan
    & $Body
    $code = $LASTEXITCODE
    if ($null -eq $code) { $code = 0 }
    $script:results.Add([pscustomobject]@{
        步骤   = $Name
        退出码 = $code
        结果   = $(if ($code -eq 0) { "通过" } else { "失败" })
    })
}

Invoke-Step "1. XAML 资源键与 BasedOn 用法" {
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-xaml-resources.ps1
}

Invoke-Step "2. net48 兼容性与零 NuGet 依赖" {
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-conventions.ps1
}

if (-not $SkipTests) {
    Invoke-Step "3. 单元测试（Core + Platform）" {
        powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test-all.ps1
    }
}

Invoke-Step "4. 发布构建（零依赖校验 + 体积）" {
    powershell -NoProfile -ExecutionPolicy Bypass -File packaging\build-release.ps1
}

Invoke-Step "5. 启动冒烟测试" {
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\smoke-test.ps1 -WaitSeconds 15
}

if (-not $SkipUi) {
    Invoke-Step "6. UI 自动化（逐个点开每个页面）" {
        powershell -NoProfile -ExecutionPolicy Bypass -File scripts\ui-smoke-test.ps1 -PageDwellSeconds 2
    }

    # 单独一条：走完"扫描 → 预览 → 确认 → 清理 → 撤销"的完整破坏性流程。
    # 它用环境变量把清理规则的目标重定向到受控目录，因此不会碰任何真实用户文件。
    Invoke-Step "7. 清理端到端（扫描→确认→清理→撤销还原）" {
        powershell -NoProfile -ExecutionPolicy Bypass -File scripts\ui-cleanup-e2e.ps1
    }
}

# 提权通道的安全验证。放在 UI 开关之外：它不弹窗口，而且**不该**被跳过 ——
# 这条通道能以管理员身份执行动作，必须每次都验证它的拒绝路径。
Invoke-Step "8. 提权通道（正向可用 + 逆向全部被拒）" {
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\elevated-task-e2e.ps1
}
# AI 助手的闸门行为：未启用时点 AI 按钮，必须给出可行动的解释、不建预览窗口、不发任何请求。
# 它不需要 API Key，所以每次验收都能跑 —— "未启用时不联网"这条承诺只能靠实测守住。
Invoke-Step "9. AI 闸门（未启用时不联网）" {
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\ai-gating-e2e.ps1
}
# AI 预览窗口：启用后必须先弹出可编辑的预览窗口、披露条在最上方、未点发送不联网。
# 它用假 key（DPAPI）把 AI 变成"就绪"状态，因此不消耗真实配额、也不发真实请求。
Invoke-Step "10. AI 预览窗口（披露条 + 可编辑 + 不点发送不联网）" {
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\ai-preview-e2e.ps1
}

Get-Process -Name WinKit -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }

Write-Host ""
Write-Host "================== 验收汇总 ==================" -ForegroundColor Cyan
$results | Format-Table -AutoSize

$failed = @($results | Where-Object { $_.退出码 -ne 0 })
if ($failed.Count -gt 0) {
    Write-Host ("有 " + $failed.Count + " 项未通过：" + (($failed | ForEach-Object { $_.步骤 }) -join '、')) -ForegroundColor Red
    exit 1
}

Write-Host ("全部 " + $results.Count + " 项通过") -ForegroundColor Green
exit 0
