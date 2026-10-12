using System;
using System.Collections.Generic;
using System.Text;

namespace WinKit.Core.Cleanup
{
    /// <summary>待清理项的处置方式。</summary>
    public enum CleanupDisposal
    {
        /// <summary>移入 WinKit 隔离区。**100% 可撤销**（就是一次文件移动），推荐默认。</summary>
        Quarantine,

        /// <summary>移入 Windows 回收站。磁盘空间要等回收站清空才真正释放，且 WinKit 内无法撤销。</summary>
        RecycleBin,

        /// <summary>彻底删除。不可撤销（L3）。</summary>
        Permanent,
    }

    /// <summary>
    /// 一条清理规则。
    ///
    /// 设计要点：规则的 <see cref="Targets"/> 是它**声明的全部活动范围**。
    /// 扫描器只会把目标目录的**直接子项**作为候选，永远不动目标目录本身 ——
    /// 这样"清理 %TEMP%"绝不可能演变成"删除 %TEMP% 目录"。
    /// </summary>
    public sealed class CleanupRule
    {
        public CleanupRule(
            string id,
            string name,
            string description,
            string impact,
            IReadOnlyList<string> targets,
            string dangerLevel,
            bool requiresAdmin,
            bool defaultEnabled,
            bool allowProtected = false,
            TimeSpan? minimumAge = null,
            string searchPattern = "*",
            bool includeDirectories = true)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Description = description ?? string.Empty;
            Impact = impact ?? string.Empty;
            Targets = targets ?? throw new ArgumentNullException(nameof(targets));
            DangerLevel = dangerLevel ?? Journal.DangerLevel.Reversible;
            RequiresAdmin = requiresAdmin;
            DefaultEnabled = defaultEnabled;
            AllowProtected = allowProtected;
            MinimumAge = minimumAge ?? TimeSpan.FromHours(1);
            SearchPattern = string.IsNullOrWhiteSpace(searchPattern) ? "*" : searchPattern;
            IncludeDirectories = includeDirectories;
        }

        public string Id { get; }
        public string Name { get; }

        /// <summary>这是什么（面向用户，必须写得让人看得懂）。</summary>
        public string Description { get; }

        /// <summary>清了有什么影响。</summary>
        public string Impact { get; }

        /// <summary>目标路径模板，支持 %ENV% 展开。可以是文件，也可以是目录。</summary>
        public IReadOnlyList<string> Targets { get; }

        public string DangerLevel { get; }
        public bool RequiresAdmin { get; }
        public bool DefaultEnabled { get; }

        /// <summary>
        /// 允许触碰受保护目录（例如 C:\Windows\Temp 位于 Windows 目录之下）。
        /// 置为 true 的规则**必须**同时是 RequiresAdmin + L2/L3 —— 由单元测试强制校验。
        /// </summary>
        public bool AllowProtected { get; }

        /// <summary>只清理"至少这么久没被修改过"的项，避免删掉正在使用的临时文件。</summary>
        public TimeSpan MinimumAge { get; }

        public string SearchPattern { get; }
        public bool IncludeDirectories { get; }

        public string AdminHint => RequiresAdmin ? "需要管理员权限" : "标准权限即可";
    }

    /// <summary>扫描出来的一个待清理项。</summary>
    public sealed class CleanupCandidate
    {
        public CleanupCandidate(string ruleId, string ruleName, string path, bool isDirectory, long size, DateTime lastWriteTimeUtc)
        {
            RuleId = ruleId;
            RuleName = ruleName;
            Path = path;
            IsDirectory = isDirectory;
            Size = size;
            LastWriteTimeUtc = lastWriteTimeUtc;
            Selected = true;
        }

        public string RuleId { get; }
        public string RuleName { get; }
        public string Path { get; }
        public bool IsDirectory { get; }
        public long Size { get; }
        public DateTime LastWriteTimeUtc { get; }

        /// <summary>界面上的勾选状态。</summary>
        public bool Selected { get; set; }

        public string SizeText => Text.ByteSize.Format(Size);

        public string LastWriteText => LastWriteTimeUtc == DateTime.MinValue
            ? "—"
            : LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        public override string ToString() => Path;
    }

    /// <summary>按规则汇总的扫描结果。</summary>
    public sealed class CleanupRuleSummary
    {
        public CleanupRuleSummary(CleanupRule rule)
        {
            Rule = rule ?? throw new ArgumentNullException(nameof(rule));
        }

        public CleanupRule Rule { get; }

        public List<CleanupCandidate> Candidates { get; } = new List<CleanupCandidate>();

        /// <summary>目标目录不存在的模板（正常情况，例如从没用过崩溃转储）。</summary>
        public List<string> MissingTargets { get; } = new List<string>();

        /// <summary>
        /// 模板本身无效的项：环境变量展开失败或路径非法。
        /// **必须与 MissingTargets 分开** —— 前者是配置写错了，后者是"本来就没有"，
        /// 混在一起会把规则配置错误伪装成"正常无事发生"，导致静默漏扫。
        /// </summary>
        public List<string> InvalidTargets { get; } = new List<string>();

        public int SkippedTooNew { get; internal set; }
        public int SkippedProtected { get; internal set; }
        public int SkippedReparsePoint { get; internal set; }
        public int SkippedExcluded { get; internal set; }
        public int SkippedError { get; internal set; }
        public int TruncatedByLimit { get; internal set; }

        public int ItemCount => Candidates.Count;

        public long TotalSize
        {
            get
            {
                long total = 0;
                foreach (var c in Candidates) total += c.Size;
                return total;
            }
        }

        public int SkippedTotal => SkippedTooNew + SkippedProtected + SkippedReparsePoint + SkippedExcluded + SkippedError;

        /// <summary>给界面用的一行结论，把"跳过了什么、为什么"说清楚。</summary>
        public string SummaryText
        {
            get
            {
                if (ItemCount == 0)
                {
                    // 配置错误优先暴露：环境变量展不开必须说出来，不能与"目录不存在"混为一谈
                    if (InvalidTargets.Count > 0)
                    {
                        return "目标路径无法识别（环境变量可能不存在）：" + string.Join("、", InvalidTargets);
                    }
                    if (MissingTargets.Count > 0 && SkippedTotal == 0) return "没有找到（目标目录不存在，属于正常情况）";
                    var why = new List<string>();
                    if (SkippedTooNew > 0) why.Add(SkippedTooNew + " 项太新（可能正在使用）");
                    if (SkippedProtected > 0) why.Add(SkippedProtected + " 项在受保护目录");
                    if (SkippedReparsePoint > 0) why.Add(SkippedReparsePoint + " 项是链接");
                    if (SkippedExcluded > 0) why.Add(SkippedExcluded + " 项已排除");
                    if (SkippedError > 0) why.Add(SkippedError + " 项无法读取");
                    return why.Count > 0 ? "可清理 0 项（跳过 " + string.Join("、", why) + "）" : "可清理 0 项";
                }

                var sb = new StringBuilder();
                sb.Append("可清理 ").Append(ItemCount).Append(" 项，共 ").Append(Text.ByteSize.Format(TotalSize));
                if (SkippedTotal > 0) sb.Append("（另跳过 ").Append(SkippedTotal).Append(" 项）");
                if (TruncatedByLimit > 0) sb.Append("（已达单规则处理上限，实际更多）");
                if (InvalidTargets.Count > 0) sb.Append("（另有 ").Append(InvalidTargets.Count).Append(" 个目标路径无法识别）");
                return sb.ToString();
            }
        }
    }

    /// <summary>一次扫描的完整结果。</summary>
    public sealed class CleanupScanResult
    {
        public List<CleanupRuleSummary> Summaries { get; } = new List<CleanupRuleSummary>();

        public int TotalItems
        {
            get
            {
                int n = 0;
                foreach (var s in Summaries) n += s.ItemCount;
                return n;
            }
        }

        public long TotalSize
        {
            get
            {
                long n = 0;
                foreach (var s in Summaries) n += s.TotalSize;
                return n;
            }
        }

        public IEnumerable<CleanupCandidate> AllCandidates()
        {
            foreach (var s in Summaries)
            {
                foreach (var c in s.Candidates) yield return c;
            }
        }
    }

    public sealed class ScanOptions
    {
        /// <summary>禁止扫描这些根目录之下的任何东西（**必须**包含 WinKit 自己的数据目录，
        /// 否则清理临时目录时可能把隔离区自己删了）。</summary>
        public List<string> ExcludeRoots { get; } = new List<string>();

        /// <summary>
        /// 单条规则最多**检查**多少项。注意是"检查"不是"收集"：
        /// 如果一个目录里全是"太新/被排除"的项，按收集数计数的话上限永远达不到，
        /// 仍然会把整个目录枚举一遍（5 万个文件就是要 5 万次 GetAttributes）。
        /// </summary>
        public int MaxItemsPerRule { get; set; } = 20000;

        /// <summary>计算目录大小时的递归深度上限。</summary>
        public int MaxDepth { get; set; } = 4;

        /// <summary>覆盖所有规则的"最小年龄"（测试用；为 null 时用规则自身的设置）。</summary>
        public TimeSpan? MinimumAgeOverride { get; set; }
    }
}
