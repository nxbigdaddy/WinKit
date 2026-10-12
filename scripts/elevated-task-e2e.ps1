# 验证提权（headless）通道：正向能用 + **逆向攻不破**。
#
# 为什么必须有这个脚本：提权子进程会以管理员身份执行动作，这个通道本质上是一个
# "以管理员身份跑某段逻辑"的入口。如果不验证它的拒绝路径，它就是一个自带的 UAC 绕过后门。
# 所以本脚本除了验证"正常载荷能工作"，更要验证四类恶意/畸形载荷**都被拒绝**。
#
# 直接以 --task 调用同一个 exe（本机已是管理员，因此不会弹 UAC；UAC 只影响交互体验，
# 通道本身的校验逻辑与是否提权无关）。
#
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File scripts\elevated-task-e2e.ps1

param(
    [string]$Exe = ""
)

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
$quarantine = Join-Path $dataDir 'quarantine'
$journalPath = Join-Path $dataDir 'journal.jsonl'

Write-Host ("被测程序: " + $Exe) -ForegroundColor Cyan

$sandbox = Join-Path $env:LOCALAPPDATA 'winkit-elev-sandbox'
Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null

$failures = New-Object 'System.Collections.Generic.List[string]'

function New-Payload([string]$disposal, $items) {
    $obj = [ordered]@{ items = $items; disposal = $disposal }
    $json = ($obj | ConvertTo-Json -Depth 6 -Compress)
    return [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
}

# 以 headless 方式跑一次任务，返回结果对象
function Invoke-ElevatedTask {
    param([string]$TaskId, [string]$PayloadBase64, [int]$TimeoutSeconds = 60)

    $token = [guid]::NewGuid().ToString('N')
    $resultFile = Join-Path $env:TEMP ("winkit-elev-test-" + $token + ".json")
    Remove-Item $resultFile -Force -ErrorAction SilentlyContinue

    $args = @('--task', $TaskId, '--payload', $PayloadBase64, '--result', $resultFile, '--token', $token)
    $p = Start-Process -FilePath $Exe -ArgumentList $args -PassThru -WindowStyle Hidden
    $null = $p.WaitForExit($TimeoutSeconds * 1000)
    $p.Refresh()
    $exitCode = if ($p.HasExited) { $p.ExitCode } else { $null }
    if (-not $p.HasExited) { try { $p.Kill() } catch { } }

    $parsed = $null
    if (Test-Path $resultFile) {
        try { $parsed = Get-Content $resultFile -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
        Remove-Item $resultFile -Force -ErrorAction SilentlyContinue
    }

    return [pscustomobject]@{
        Token    = $token
        ExitCode = $exitCode
        Result   = $parsed
        Raw      = $(if ($parsed) { ($parsed | ConvertTo-Json -Compress) } else { '(没有结果文件)' })
    }
}

function Assert-True([bool]$condition, [string]$message) {
    if ($condition) { Write-Host ("  [OK]   " + $message) -ForegroundColor Green }
    else { Write-Host ("  [FAIL] " + $message) -ForegroundColor Red; $failures.Add($message) }
}

# ============================================================ 正向：正常载荷
Write-Host ""
Write-Host "① 正向：两个真实文件应被移入隔离区，且能撤销还原" -ForegroundColor Cyan
$fileA = Join-Path $sandbox 'a.tmp'
$fileB = Join-Path $sandbox 'b.tmp'
Set-Content $fileA 'payload A' -Encoding UTF8
Set-Content $fileB 'payload B' -Encoding UTF8

$payload = New-Payload 'Quarantine' @(
    @{ path = $fileA; size = 9;  dir = $false; rule = 'temp.user' },
    @{ path = $fileB; size = 9;  dir = $false; rule = 'temp.user' }
)
$r1 = Invoke-ElevatedTask -TaskId 'cleanup.elevated' -PayloadBase64 $payload

Write-Host ("  结果: " + $r1.Raw)
Assert-True ($null -ne $r1.Result) '回传了结果文件'
if ($r1.Result) {
    Assert-True ($r1.Result.token -eq $r1.Token) 'token 校验一致'
    Assert-True ($r1.Result.success -eq $true) 'success = true'
    Assert-True ("$($r1.Result.output)" -match '已清理') '输出里有清理摘要'
    Assert-True ("$($r1.Result.output)" -match '批次') '输出里带批次号（父进程要靠它提示可撤销）'
}
Assert-True (-not (Test-Path $fileA)) '源文件 A 已被移走'
Assert-True (-not (Test-Path $fileB)) '源文件 B 已被移走'

$qFiles = @(Get-ChildItem $quarantine -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*a.tmp' -or $_.Name -like '*b.tmp' })
Assert-True ($qFiles.Count -ge 2) ("隔离区里能找回这两个文件（找到 " + $qFiles.Count + " 个）")

# 用日志里的批次号验证可撤销
$batchId = $null
if ($r1.Result -and "$($r1.Result.output)" -match '批次\s*([0-9a-f]{8})') { $batchId = $Matches[1] }
Assert-True ($null -ne $batchId) ("从输出里解析出批次号: " + $batchId)
if ($batchId -and (Test-Path $journalPath)) {
    $lines = @(Get-Content $journalPath -Encoding UTF8 | Where-Object { $_ -match $batchId })
    Assert-True ($lines.Count -ge 2) ("操作日志里有该批次的记录（" + $lines.Count + " 条）")
}

# ============================================================ 逆向 1：拒绝彻底删除
Write-Host ""
Write-Host "② 逆向：disposal=Permanent 必须被拒绝，且文件不能被动" -ForegroundColor Cyan
$fileC = Join-Path $sandbox 'c.tmp'
Set-Content $fileC 'must survive' -Encoding UTF8
$payloadC = New-Payload 'Permanent' @(@{ path = $fileC; size = 13; dir = $false; rule = 'temp.user' })
$r2 = Invoke-ElevatedTask -TaskId 'cleanup.elevated' -PayloadBase64 $payloadC
Write-Host ("  结果: " + $r2.Raw)
Assert-True (Test-Path $fileC) '文件仍然存在（彻底删除被拒绝）'
if ($r2.Result) {
    Assert-True ($r2.Result.success -eq $false) '被判定为失败'
    Assert-True ("$($r2.Result.error)" -match '彻底删除|不允许|不接受') '错误信息说明了拒绝原因'
}

# ============================================================ 逆向 2：数据目录之下
Write-Host ""
Write-Host "③ 逆向：路径位于 WinKit 数据目录之下必须被过滤（防止隔离区自吞）" -ForegroundColor Cyan
$canary = Join-Path $quarantine 'canary-should-not-move.tmp'
New-Item -ItemType Directory -Force -Path $quarantine | Out-Null
Set-Content $canary 'canary' -Encoding UTF8
$payloadD = New-Payload 'Quarantine' @(@{ path = $canary; size = 6; dir = $false; rule = 'temp.user' })
$r3 = Invoke-ElevatedTask -TaskId 'cleanup.elevated' -PayloadBase64 $payloadD
Write-Host ("  结果: " + $r3.Raw)
Assert-True (Test-Path $canary) '隔离区里的哨兵文件没有被搬走'
if ($r3.Result) { Assert-True ("$($r3.Result.output)" -notmatch '已清理 1 项') '没有被当成 1 项成功清理' }
Remove-Item $canary -Force -ErrorAction SilentlyContinue

# ============================================================ 逆向 3：畸形载荷
Write-Host ""
Write-Host "④ 逆向：非法 base64 / 非 JSON 载荷必须安全失败" -ForegroundColor Cyan
$r4 = Invoke-ElevatedTask -TaskId 'cleanup.elevated' -PayloadBase64 '这不是base64!!!'
Write-Host ("  结果: " + $r4.Raw)
Assert-True ($null -ne $r4.Result) '仍然回传了结果文件（异常路径也必须回传，否则父进程只能看到超时）'
if ($r4.Result) { Assert-True ($r4.Result.success -eq $false) '被判定为失败' }

$notJson = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('{ this is not json '))
$r5 = Invoke-ElevatedTask -TaskId 'cleanup.elevated' -PayloadBase64 $notJson
Write-Host ("  结果: " + $r5.Raw)
Assert-True ($null -ne $r5.Result -and $r5.Result.success -eq $false) '非 JSON 载荷被安全拒绝'

# ============================================================ 逆向 4：未知任务
Write-Host ""
Write-Host "⑤ 未知任务 id 必须被拒绝" -ForegroundColor Cyan
$r6 = Invoke-ElevatedTask -TaskId 'evil.task' -PayloadBase64 $payload
Write-Host ("  结果: " + $r6.Raw)
Assert-True ($null -ne $r6.Result -and $r6.Result.success -eq $false) '未知任务被拒绝'

# ============================================================ 收尾
Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "提权通道验证未通过：" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host ("  - " + $f) -ForegroundColor Red }
    exit 1
}
Write-Host "提权通道验证通过：正常载荷可用、可撤销；彻底删除 / 数据目录路径 / 畸形载荷 / 未知任务全部被拒绝" -ForegroundColor Green
exit 0
