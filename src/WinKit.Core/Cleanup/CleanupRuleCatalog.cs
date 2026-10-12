using System;
using System.Collections.Generic;
using WinKit.Core.Journal;

namespace WinKit.Core.Cleanup
{
    /// <summary>
    /// 内置清理规则库。
    ///
    /// **每一条规则都必须写清楚"这是什么"和"清了有什么影响"** —— 这是本工具区别于
    /// "一键清理"的核心：用户有权知道自己在删什么。这件事由单元测试强制校验
    /// （见 CleanupRuleCatalogTests：描述/影响不得为空）。
    ///
    /// 刻意**不包含**的规则及原因：
    /// - Prefetch：清了会让下次启动变慢，属于反效果。
    /// - 缩略图缓存（thumbcache_*.db）：Explorer 运行时基本都处于占用状态，成功率极低，
    ///   加进来只会让用户以为工具坏了。
    /// - Windows 更新缓存（SoftwareDistribution\Download）：要停 wuauserv 服务，风险高，
    ///   放到 L2/L3 的进阶清理里单独做，不进默认清单。
    /// </summary>
    public static class CleanupRuleCatalog
    {
        public static IReadOnlyList<CleanupRule> CreateDefault()
        {
            return new List<CleanupRule>
            {
                new CleanupRule(
                    id: "temp.user",
                    name: "用户临时文件",
                    description: "程序运行时产生的临时文件，位于当前用户的 Temp 目录。正常退出时多数会被自己删掉，残留的部分可以安全清理。",
                    impact: "正在运行的程序可能正占用其中某些文件，被占用的会自动跳过，不会强制删除；清理后个别程序首次启动会稍慢。",
                    targets: new[] { @"%TEMP%" },
                    dangerLevel: DangerLevel.Reversible,
                    requiresAdmin: false,
                    defaultEnabled: true,
                    minimumAge: TimeSpan.FromHours(1)),

                new CleanupRule(
                    id: "dumps.user",
                    name: "应用崩溃转储",
                    description: "程序崩溃时生成的 .dmp 内存快照，仅对开发调试有用。",
                    impact: "清理后无法再用这些转储分析崩溃原因；不影响任何程序的正常运行。",
                    targets: new[] { @"%LOCALAPPDATA%\CrashDumps" },
                    dangerLevel: DangerLevel.Reversible,
                    requiresAdmin: false,
                    defaultEnabled: true,
                    minimumAge: TimeSpan.FromHours(1)),

                new CleanupRule(
                    id: "wer.user",
                    name: "Windows 错误报告缓存",
                    description: "系统收集的错误报告存档，位于用户目录下的 WER\\ReportArchive 与 WER\\ReportQueue。",
                    impact: "不影响系统运行；清空后历史错误报告的本地记录会丢失，无法再回看。",
                    targets: new[]
                    {
                        @"%LOCALAPPDATA%\Microsoft\Windows\WER\ReportArchive",
                        @"%LOCALAPPDATA%\Microsoft\Windows\WER\ReportQueue",
                    },
                    dangerLevel: DangerLevel.Reversible,
                    requiresAdmin: false,
                    defaultEnabled: true,
                    minimumAge: TimeSpan.FromHours(1)),

                new CleanupRule(
                    id: "d3d.cache",
                    name: "DirectX 着色器缓存",
                    description: "显卡着色器编译缓存（D3DSCache），删除后系统与程序会自动重建。",
                    impact: "清理后部分游戏和图形程序首次启动会稍慢，重建后恢复正常。",
                    targets: new[] { @"%LOCALAPPDATA%\D3DSCache" },
                    dangerLevel: DangerLevel.Reversible,
                    requiresAdmin: false,
                    defaultEnabled: true,
                    minimumAge: TimeSpan.FromHours(1)),

                // ---- 以下规则需要管理员权限，默认关闭，必须由用户显式勾选 ----

                new CleanupRule(
                    id: "temp.system",
                    name: "系统临时文件",
                    description: "系统组件与服务写入的临时文件，位于 C:\\Windows\\Temp。",
                    impact: "需要管理员权限；正在被系统服务占用的文件会跳过，不会被强制删除。",
                    targets: new[] { @"%SystemRoot%\Temp" },
                    dangerLevel: DangerLevel.HardToReverse,
                    requiresAdmin: true,
                    defaultEnabled: false,
                    allowProtected: true,   // 位于 C:\Windows 之下，必须显式放行
                    minimumAge: TimeSpan.FromHours(1)),

                new CleanupRule(
                    id: "dumps.system",
                    name: "系统小型转储",
                    description: "系统蓝屏时生成的小型内存转储，位于 C:\\Windows\\Minidump。",
                    impact: "需要管理员权限；清理后无法再分析历史蓝屏原因。",
                    targets: new[] { @"%SystemRoot%\Minidump" },
                    dangerLevel: DangerLevel.HardToReverse,
                    requiresAdmin: true,
                    defaultEnabled: false,
                    allowProtected: true,
                    minimumAge: TimeSpan.FromHours(1)),
            };
        }

        public static CleanupRule? FindById(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var rule in CreateDefault())
            {
                if (string.Equals(rule.Id, id, StringComparison.OrdinalIgnoreCase)) return rule;
            }
            return null;
        }
    }
}
