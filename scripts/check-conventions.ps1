# 静态检查：net48 上不可用的语法/API，以及"发布产物零第三方依赖"这条硬约束。
#
# 为什么需要这个：net48 缺少一批 .NET Core 才有的 API（System.Index、string.Contains(char)、
# HashCode.Combine 等），用错了编译期就会报错倒还好；但更危险的是有人往生产工程里加
# PackageReference —— 那会往输出目录塞 DLL，直接破坏"单文件"承诺。
#
# 用法：powershell -File scripts\check-conventions.ps1

param([string]$Root = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'

$srcDir = Join-Path $Root 'src'

$rules = @(
    @{ Name = '从末尾索引 arr[^1]';      Pattern = '\[\^(?:[A-Za-z_]\w*|\d+)\]';                Scope = '*.cs';
       Fix = 'net48 没有 System.Index，请用 arr[arr.Length - 1]' }
    @{ Name = 'string.Contains(char)';   Pattern = "\.Contains\('";      Scope = '*.cs';
       Fix = 'net48 没有这个重载，请用 IndexOf(c) >= 0' }
    @{ Name = 'HashCode.Combine';        Pattern = 'HashCode\.Combine';  Scope = '*.cs';
       Fix = 'net48 没有 HashCode，请手写组合或改用匿名类型做键' }
    @{ Name = 'System.Text.Json';        Pattern = 'System\.Text\.Json'; Scope = '*.cs';
       Fix = '会引入多个 DLL，请用 WinKit.Core.Serialization.Json' }
    @{ Name = 'Index/Range 命名空间';    Pattern = 'using System\.(Index|Range)'; Scope = '*.cs';
       Fix = 'net48 不支持，请改用索引算术' }
    @{ Name = 'Span/ReadOnlySpan';       Pattern = '\bReadOnlySpan<';    Scope = '*.cs';
       Fix = 'net48 无内置支持，需引 System.Memory 包（会破坏零依赖）' }
    @{ Name = '生产工程引入 NuGet 依赖'; Pattern = '<PackageReference';  Scope = '*.csproj';
       Fix = '发布产物必须零第三方依赖；NuGet 只能出现在 tests/ 下的工程里' }
)

$problems = New-Object 'System.Collections.Generic.List[string]'
$scanned = 0

foreach ($rule in $rules) {
    $files = Get-ChildItem -Path $srcDir -Recurse -File -Filter $rule.Scope |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

    foreach ($file in $files) {
        $scanned++
        $lines = Get-Content $file.FullName -Encoding UTF8
        for ($i = 0; $i -lt $lines.Count; $i++) {
            # 先剥掉行内注释再匹配，否则"避免引入 System.Text.Json"这类说明性注释会被误报
            $code = $lines[$i]
            $commentAt = $code.IndexOf('//')
            if ($commentAt -ge 0) { $code = $code.Substring(0, $commentAt) }

            if ($code -match $rule.Pattern) {
                $rel = $file.FullName.Replace($Root + '\', '')
                $problems.Add(("[{0}] {1}:{2}" -f $rule.Name, $rel, ($i + 1)))
                $problems.Add(("        修正建议：" + $rule.Fix))
            }
        }
    }
}

# record / init 只允许出现在带 IsExternalInit polyfill 的工程里
$polyfillProjects = @()
foreach ($proj in (Get-ChildItem -Path $srcDir -Recurse -File -Filter IsExternalInit.cs |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })) {
    # 形如 src\WinKit.Core\Compat\IsExternalInit.cs → 取 src\WinKit.Core
    $projectDir = Split-Path -Parent (Split-Path -Parent $proj.FullName)
    $polyfillProjects += $projectDir
    Write-Host ("检测到 IsExternalInit polyfill：" + $projectDir.Replace($Root + '\', ''))
}

foreach ($file in (Get-ChildItem -Path $srcDir -Recurse -File -Filter *.cs |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })) {
    $covered = $false
    foreach ($p in $polyfillProjects) {
        if ($file.FullName.StartsWith($p, [System.StringComparison]::OrdinalIgnoreCase)) { $covered = $true; break }
    }
    if ($covered) { continue }

    $lines = Get-Content $file.FullName -Encoding UTF8
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $code = $lines[$i]
        $commentAt = $code.IndexOf('//')
        if ($commentAt -ge 0) { $code = $code.Substring(0, $commentAt) }

        if ($code -match '\brecord\s+\w' -or $code -match '\{\s*get;\s*init;\s*\}') {
            $rel = $file.FullName.Replace($Root + '\', '')
            $problems.Add(("[record/init 缺少 polyfill] {0}:{1}" -f $rel, ($i + 1)))
            $problems.Add("        修正建议：该工程没有 IsExternalInit polyfill，请改用普通属性，或补一个 polyfill")
        }
    }
}

Write-Host ("扫描 src/ 下 {0} 个文件" -f $scanned)

# ---- 网络出口唯一性检查 ----
#
# WinKit 的隐私承诺是"默认不联网、可选联网"。光靠文档写一句不够 ——
# 这里把承诺变成机器可校验的约束：**整个 src 之下只允许一个文件出现网络 API**。
# 将来任何人（包括我自己）在别处加一个 HttpClient，这个检查就会失败。
#
# 这条规则的价值在于：它让"只有一个出口"这个事实可以被审计，
# 而不是需要通读全部代码去相信。
$allowedNetworkFile = 'src\WinKit.Platform\Ai\DeepSeekClient.cs'
$networkPatterns = @('HttpClient', 'WebRequest', 'WebClient', 'HttpWebRequest', 'Socket', 'TcpClient')
$networkHits = 0

foreach ($file in (Get-ChildItem -Path $srcDir -Recurse -File -Filter *.cs |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })) {
    $rel = $file.FullName.Replace($Root + '\', '')
    if ($rel -eq $allowedNetworkFile) { continue }

    $lines = Get-Content $file.FullName -Encoding UTF8
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $code = $lines[$i]
        $commentAt = $code.IndexOf('//')
        if ($commentAt -ge 0) { $code = $code.Substring(0, $commentAt) }

        foreach ($pattern in $networkPatterns) {
            if ($code -match ("\b" + $pattern + "\b")) {
                $networkHits++
                $problems.Add(("[网络出口越界] {0}:{1}  出现了 {2}" -f $rel, ($i + 1), $pattern))
                $problems.Add("        修正建议：全项目只允许 {0} 使用网络 API；其余功能必须保持本地" -f $allowedNetworkFile)
            }
        }
    }
}
Write-Host ("检查网络出口唯一性，越界 {0} 处（允许的唯一出口：{1}）" -f $networkHits, $allowedNetworkFile)

if ($problems.Count -gt 0) {
    Write-Host ""
    Write-Host "发现违反 net48 / 零依赖约束的代码：" -ForegroundColor Red
    foreach ($p in $problems) { Write-Host ("  " + $p) -ForegroundColor Red }
    exit 1
}

Write-Host "通过：没有发现 net48 不兼容语法，生产工程零 NuGet 依赖" -ForegroundColor Green
exit 0
