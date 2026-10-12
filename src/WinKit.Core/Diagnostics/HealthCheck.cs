using System;
using System.Collections.Generic;
using System.Text;

namespace WinKit.Core.Diagnostics
{
    public enum HealthStatus
    {
        /// <summary>正常。</summary>
        Ok = 0,

        /// <summary>可以留意，但不影响使用。</summary>
        Notice = 1,

        /// <summary>建议处理。</summary>
        Warning = 2,

        /// <summary>需要尽快处理。</summary>
        Critical = 3,
    }

    public sealed class HealthItem
    {
        public HealthItem(string id, string title, HealthStatus status, string detail, string suggestion)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Title = title ?? throw new ArgumentNullException(nameof(title));
            Status = status;
            Detail = detail ?? string.Empty;
            Suggestion = suggestion ?? string.Empty;
        }

        public string Id { get; }
        public string Title { get; }
        public HealthStatus Status { get; }

        /// <summary>实测到的数值，例如"系统盘剩余 12.3 GB（占 4.2%）"。</summary>
        public string Detail { get; }

        /// <summary>建议动作。为空表示无需动作。</summary>
        public string Suggestion { get; }

        public bool NeedsAction => Status == HealthStatus.Warning || Status == HealthStatus.Critical;

        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case HealthStatus.Critical: return "需尽快处理";
                    case HealthStatus.Warning: return "建议处理";
                    case HealthStatus.Notice: return "可以留意";
                    default: return "正常";
                }
            }
        }
    }

    public sealed class HealthReport
    {
        public HealthReport(IReadOnlyList<HealthItem> items, int score)
        {
            Items = items ?? throw new ArgumentNullException(nameof(items));
            Score = Math.Max(0, Math.Min(100, score));
        }

        public IReadOnlyList<HealthItem> Items { get; }

        /// <summary>0~100 的体检分。**只做扣分不做加分**：没有问题时就是 100。</summary>
        public int Score { get; }

        public int CriticalCount
        {
            get
            {
                var n = 0;
                foreach (var item in Items) if (item.Status == HealthStatus.Critical) n++;
                return n;
            }
        }

        public int WarningCount
        {
            get
            {
                var n = 0;
                foreach (var item in Items) if (item.Status == HealthStatus.Warning) n++;
                return n;
            }
        }

        public int NoticeCount
        {
            get
            {
                var n = 0;
                foreach (var item in Items) if (item.Status == HealthStatus.Notice) n++;
                return n;
            }
        }

        public string Verdict
        {
            get
            {
                if (CriticalCount > 0) return "发现问题，建议尽快处理";
                if (WarningCount > 0) return "基本正常，有几项建议处理";
                if (NoticeCount > 0) return "状态良好，有几项可以留意";
                return "状态良好";
            }
        }

        public string SummaryText
        {
            get
            {
                var sb = new StringBuilder();
                sb.Append("体检得分 ").Append(Score).Append(" 分 · ").Append(Verdict);
                if (WarningCount + CriticalCount > 0)
                {
                    sb.Append("（").Append(WarningCount).Append(" 项建议处理，")
                      .Append(CriticalCount).Append(" 项需尽快处理）");
                }
                return sb.ToString();
            }
        }
    }

    /// <summary>
    /// 体检的输入数据。Core 层不碰系统，数据由 App 层从 Platform 收集后填进来 ——
    /// 这样评估逻辑可以在毫秒级单测里跑遍各种边界，而不必伪造一台机器。
    /// 未知/未实现的项用 -1 表示。
    /// </summary>
    public sealed class HealthInputs
    {
        public string SystemDriveName { get; set; } = "C:\\";
        public ulong SystemDriveTotalBytes { get; set; }
        public ulong SystemDriveFreeBytes { get; set; }

        public ulong MemoryTotalBytes { get; set; }
        public ulong MemoryUsedBytes { get; set; }

        /// <summary>
        /// 当前进程总数。
        ///
        /// **注意：<see cref="HealthCheck.Evaluate"/> 目前不读这个字段** —— 进程数量本身
        /// 不构成"问题"，报出来只会制造噪音。它被保留是因为界面上会展示它，
        /// 且未来若要加"进程数异常膨胀"这类启发式判断需要它。
        /// 留这个注释是为了不让下一个人误以为它是已接线的输入。
        /// </summary>
        public int ProcessCount { get; set; }

        /// <summary>占用 CPU 最高的进程名（用于提示）。可为空。</summary>
        public string? TopCpuProcessName { get; set; }

        /// <summary>占用 CPU 最高的进程的百分比（占整机）。</summary>
        public double TopCpuProcessPercent { get; set; }

        /// <summary>可清理体积（只读扫描的结果）。</summary>
        public long CleanupCandidateBytes { get; set; }
        public int CleanupCandidateCount { get; set; }

        /// <summary>回收站占用。-1 表示未知。</summary>
        public long RecycleBinBytes { get; set; } = -1;

        /// <summary>隔离区占用。</summary>
        public long QuarantineBytes { get; set; }

        public bool RunningAsAdmin { get; set; }

        /// <summary>操作日志里记录过的失败次数（用于提示"有操作失败过"）。-1 表示未知。</summary>
        public int RecentFailedOperations { get; set; } = -1;
    }

    /// <summary>
    /// 一键体检的评估逻辑。**只读、只判断、绝不动手** —— 这是刻意的：
    /// 体检的价值是告诉用户"哪里有问题、该去哪一页处理"，而不是替用户做决定。
    /// </summary>
    public static class HealthCheck
    {
        private const long Gigabyte = 1024L * 1024 * 1024;

        public static HealthReport Evaluate(HealthInputs inputs)
        {
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));

            var items = new List<HealthItem>();
            var score = 100;

            // ---- 1. 系统盘空间 ----
            if (inputs.SystemDriveTotalBytes > 0)
            {
                var freePercent = inputs.SystemDriveFreeBytes * 100d / inputs.SystemDriveTotalBytes;
                var freeText = Text.ByteSize.Format((long)inputs.SystemDriveFreeBytes);
                var detail = inputs.SystemDriveName + " 剩余 " + freeText
                    + "（占 " + freePercent.ToString("0.0") + "%）";

                if (freePercent < 5d)
                {
                    score -= 30;
                    items.Add(new HealthItem("disk.free", "系统盘空间", HealthStatus.Critical, detail,
                        "空间已接近耗尽，建议立即到「垃圾清理」处理，或卸载不再使用的软件。"));
                }
                else if (freePercent < 10d)
                {
                    score -= 20;
                    items.Add(new HealthItem("disk.free", "系统盘空间", HealthStatus.Warning, detail,
                        "建议到「垃圾清理」扫描一次，并检查是否有大文件可清理。"));
                }
                else if (freePercent < 20d)
                {
                    score -= 8;
                    items.Add(new HealthItem("disk.free", "系统盘空间", HealthStatus.Notice, detail,
                        "空间还算够用，可以定期清理临时文件保持余量。"));
                }
                else
                {
                    items.Add(new HealthItem("disk.free", "系统盘空间", HealthStatus.Ok, detail, string.Empty));
                }
            }

            // ---- 2. 内存占用 ----
            if (inputs.MemoryTotalBytes > 0)
            {
                var usedPercent = inputs.MemoryUsedBytes * 100d / inputs.MemoryTotalBytes;
                var detail = "已用 " + Text.ByteSize.Format((long)inputs.MemoryUsedBytes)
                    + " / " + Text.ByteSize.Format((long)inputs.MemoryTotalBytes)
                    + "（占 " + usedPercent.ToString("0.0") + "%）";

                if (usedPercent >= 95d)
                {
                    score -= 25;
                    items.Add(new HealthItem("memory.usage", "内存占用", HealthStatus.Critical, detail,
                        "内存几乎耗尽，建议到「进程管理」按内存排序，结束不需要的进程。"));
                }
                else if (usedPercent >= 85d)
                {
                    score -= 12;
                    items.Add(new HealthItem("memory.usage", "内存占用", HealthStatus.Warning, detail,
                        "建议到「进程管理」查看占用最高的进程。"));
                }
                else if (usedPercent >= 75d)
                {
                    score -= 4;
                    items.Add(new HealthItem("memory.usage", "内存占用", HealthStatus.Notice, detail, string.Empty));
                }
                else
                {
                    items.Add(new HealthItem("memory.usage", "内存占用", HealthStatus.Ok, detail, string.Empty));
                }
            }

            // ---- 3. 单个进程 CPU 异常 ----
            if (inputs.TopCpuProcessPercent >= 50d && !string.IsNullOrEmpty(inputs.TopCpuProcessName))
            {
                var detail = "「" + inputs.TopCpuProcessName + "」占用 CPU "
                    + inputs.TopCpuProcessPercent.ToString("0.0") + "%";
                if (inputs.TopCpuProcessPercent >= 90d)
                {
                    score -= 12;
                    items.Add(new HealthItem("cpu.top", "CPU 占用", HealthStatus.Warning, detail,
                        "该进程几乎占满了一个核心。若持续如此，可到「进程管理」确认它是否正常。"));
                }
                else
                {
                    score -= 4;
                    items.Add(new HealthItem("cpu.top", "CPU 占用", HealthStatus.Notice, detail,
                        "若长期如此可到「进程管理」确认。"));
                }
            }

            // ---- 4. 可清理的垃圾 ----
            if (inputs.CleanupCandidateCount > 0)
            {
                var detail = "扫描到 " + inputs.CleanupCandidateCount + " 项、共 "
                    + Text.ByteSize.Format(inputs.CleanupCandidateBytes) + " 可清理";
                if (inputs.CleanupCandidateBytes >= Gigabyte)
                {
                    score -= 10;
                    items.Add(new HealthItem("cleanup.pending", "可清理内容", HealthStatus.Warning, detail,
                        "建议到「垃圾清理」预览后清理。清理默认进入隔离区，可一键撤销。"));
                }
                else
                {
                    score -= 3;
                    items.Add(new HealthItem("cleanup.pending", "可清理内容", HealthStatus.Notice, detail,
                        "可以到「垃圾清理」处理。"));
                }
            }
            else if (inputs.CleanupCandidateCount == 0 && inputs.CleanupCandidateBytes >= 0)
            {
                items.Add(new HealthItem("cleanup.pending", "可清理内容", HealthStatus.Ok,
                    "未发现可清理内容", string.Empty));
            }

            // ---- 5. 回收站 ----
            if (inputs.RecycleBinBytes > 5L * Gigabyte)
            {
                score -= 6;
                items.Add(new HealthItem("recyclebin", "回收站", HealthStatus.Notice,
                    "回收站占用 " + Text.ByteSize.Format(inputs.RecycleBinBytes),
                    "回收站里的内容仍占用磁盘空间，可到「垃圾清理」清空。"));
            }
            else if (inputs.RecycleBinBytes >= 0)
            {
                items.Add(new HealthItem("recyclebin", "回收站", HealthStatus.Ok,
                    "回收站占用 " + Text.ByteSize.Format(inputs.RecycleBinBytes), string.Empty));
            }

            // ---- 6. 隔离区占用 ----
            if (inputs.QuarantineBytes > 2L * Gigabyte)
            {
                score -= 4;
                items.Add(new HealthItem("quarantine", "WinKit 隔离区", HealthStatus.Notice,
                    "隔离区占用 " + Text.ByteSize.Format(inputs.QuarantineBytes),
                    "隔离区里的内容还占着磁盘。确认不需要还原后，可到「垃圾清理」清空隔离区。"));
            }
            else if (inputs.QuarantineBytes > 0)
            {
                items.Add(new HealthItem("quarantine", "WinKit 隔离区", HealthStatus.Ok,
                    "隔离区占用 " + Text.ByteSize.Format(inputs.QuarantineBytes), string.Empty));
            }

            // ---- 7. 权限提示（不是问题，但会影响可用功能）----
            items.Add(inputs.RunningAsAdmin
                ? new HealthItem("permission", "当前权限", HealthStatus.Ok, "管理员权限", string.Empty)
                : new HealthItem("permission", "当前权限", HealthStatus.Notice, "标准权限",
                    "系统临时文件、系统转储等清理项需要管理员权限，需要时在对应页面按提示提权。"));

            // ---- 8. 历史操作失败 ----
            if (inputs.RecentFailedOperations > 0)
            {
                score -= 3;
                items.Add(new HealthItem("journal.failed", "历史操作", HealthStatus.Notice,
                    "操作日志里有 " + inputs.RecentFailedOperations + " 条失败记录",
                    "到「撤销中心」查看具体原因（文件被占用、权限不足等）。"));
            }

            return new HealthReport(items, score);
        }
    }
}
