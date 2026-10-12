# 静态检查：XAML 里引用的每个 StaticResource / DynamicResource 键是否真的有定义。
#
# 为什么需要这个：多个 agent 并行写 XAML 时，最容易出的错是引用了不存在的资源键
# （比如把 SectionTitleTextStyle 写成 SectionTitleStyle）。这类错误**编译期查不出来**，
# 只在运行时抛 XAML 解析异常、整个页面白屏。这个脚本把风险提前到构建阶段。
#
# 用法：powershell -File scripts\check-xaml-resources.ps1

param([string]$Root = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'

$srcDir = Join-Path $Root 'src'
if (-not (Test-Path $srcDir)) { throw "找不到 src 目录：$srcDir" }

$files = Get-ChildItem -Path $srcDir -Recurse -File -Filter *.xaml |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

if ($files.Count -eq 0) {
    Write-Host "没有找到任何 XAML 文件" -ForegroundColor Yellow
    exit 0
}

# 1) 收集全部已定义的资源键
$defined = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($file in $files) {
    $text = Get-Content $file.FullName -Raw -Encoding UTF8
    foreach ($m in [regex]::Matches($text, 'x:Key\s*=\s*"([^"]+)"')) {
        [void]$defined.Add($m.Groups[1].Value)
    }
}

# 2) 检查每个引用
$problems = New-Object 'System.Collections.Generic.List[string]'
$referenceCount = 0
$fileCount = 0

foreach ($file in $files) {
    $fileCount++
    $lines = Get-Content $file.FullName -Encoding UTF8
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($m in [regex]::Matches($lines[$i], '\{(?:StaticResource|DynamicResource)\s+([^}]+?)\s*\}')) {
            $key = $m.Groups[1].Value.Trim()
            $referenceCount++

            # 跳过本身就是标记扩展的引用，例如 {StaticResource {x:Type Button}}
            if ($key.StartsWith('{') -or $key.Contains(':')) { continue }

            if (-not $defined.Contains($key)) {
                $rel = $file.FullName.Replace($Root + '\', '')
                $problems.Add(("{0}:{1}  引用未定义的资源键：{2}" -f $rel, ($i + 1), $key))
            }
        }
    }
}

Write-Host ("扫描 {0} 个 XAML 文件，{1} 个资源键定义，{2} 处资源引用" -f $fileCount, $defined.Count, $referenceCount)

# 2) 非法用法检查：Style.BasedOn 是普通 CLR 属性而不是依赖属性，**不能**用 DynamicResource。
#    用错会在运行时抛 XamlParseException，表现是整个页面加载失败 —— 编译期完全查不出来。
#    （这个规则来自一次真实事故：进程页因此整页打不开，靠 UI 自动化测试才发现。）
$illegalCount = 0
foreach ($file in $files) {
    $lines = Get-Content $file.FullName -Encoding UTF8
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match 'BasedOn\s*=\s*"\{DynamicResource') {
            $illegalCount++
            $rel = $file.FullName.Replace($Root + '\', '')
            $problems.Add(("{0}:{1}  BasedOn 不支持 DynamicResource，必须改用 StaticResource" -f $rel, ($i + 1)))
        }
    }
}
Write-Host ("检查 BasedOn 用法，发现 {0} 处非法组合" -f $illegalCount)

# 3) Style.TargetType 与使用位置是否兼容。
#    WPF 在运行时才校验这个：把 TargetType="DataGridCell" 的样式拿去做 DataGridTextColumn 的
#    ElementStyle，或者把 TextBlock 的样式贴到 Button 上，都会在加载该页面时抛异常。
#    编译期、资源键检查都发现不了。（这条规则同样来自真实事故。）
$typeMap = @{}
foreach ($file in $files) {
    $lines = Get-Content $file.FullName -Encoding UTF8
    foreach ($line in $lines) {
        $km = [regex]::Match($line, 'x:Key\s*=\s*"([^"]+)"')
        $tm = [regex]::Match($line, 'TargetType\s*=\s*"(?:\{x:Type\s+)?([\w:]+)\}?"')
        if ($km.Success -and $tm.Success -and $line -match '<Style') {
            $typeMap[$km.Groups[1].Value] = $tm.Groups[1].Value
        }
    }
}

$typeIssues = 0
foreach ($file in $files) {
    $lines = Get-Content $file.FullName -Encoding UTF8
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $rel = $file.FullName.Replace($Root + '\', '')

        # 3a) ElementStyle / HeaderStyle 的类型必须与 DataGrid 列的种类匹配
        foreach ($m in [regex]::Matches($line, '(ElementStyle|HeaderStyle)\s*=\s*"\{StaticResource\s+([^}]+?)\s*\}"')) {
            $slot = $m.Groups[1].Value
            $key = $m.Groups[2].Value.Trim()
            if (-not $typeMap.ContainsKey($key)) { continue }
            $target = $typeMap[$key]

            $expected = if ($slot -eq 'ElementStyle') { @('TextBlock', 'FrameworkElement') } else { @('DataGridColumnHeader', 'FrameworkElement') }
            if ($expected -notcontains $target) {
                $typeIssues++
                $problems.Add(("{0}:{1}  {2} 用了 TargetType={3} 的样式『{4}』，类型不匹配会在运行时抛异常" -f $rel, ($i + 1), $slot, $target, $key))
            }
        }

        # 3b) 同一行上元素自带的 Style 必须与元素类型匹配（只覆盖单行开始标签，多行标签属于尽力而为）
        $em = [regex]::Match($line, '<([A-Za-z][\w:]*)\b[^>]*?\bStyle\s*=\s*"\{StaticResource\s+([^}]+?)\s*\}"')
        if ($em.Success) {
            $element = $em.Groups[1].Value
            $key = $em.Groups[2].Value.Trim()
            if ($typeMap.ContainsKey($key)) {
                $target = $typeMap[$key]
                # 只比对明确的已知类型，且允许派生关系（例如 Button 用 TargetType=ButtonBase 无妨）
                if ($target -ne $element -and $target -ne 'FrameworkElement') {
                    $typeIssues++
                    $problems.Add(("{0}:{1}  <{2}> 用了 TargetType={3} 的样式『{4}』" -f $rel, ($i + 1), $element, $target, $key))
                }
            }
        }
    }
}
Write-Host ("检查样式类型匹配，发现 {0} 处不匹配" -f $typeIssues)

if ($problems.Count -gt 0) {
    Write-Host ""
    Write-Host "发现 XAML 运行时才会暴露的问题：" -ForegroundColor Red
    foreach ($p in $problems) { Write-Host ("  " + $p) -ForegroundColor Red }
    exit 1
}

Write-Host "通过：资源引用、BasedOn 用法、样式类型匹配全部正常" -ForegroundColor Green
exit 0
