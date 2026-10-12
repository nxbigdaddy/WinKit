using System.Collections.Generic;
using System.Runtime.Serialization;

namespace WinKit.Core.Cleanup
{
    /// <summary>提权清理任务里的单个待清理项。</summary>
    [DataContract]
    public sealed class ElevatedCleanupItem
    {
        [DataMember(Name = "path", Order = 0)]
        public string Path { get; set; } = string.Empty;

        [DataMember(Name = "size", Order = 1)]
        public long Size { get; set; }

        [DataMember(Name = "dir", Order = 2)]
        public bool IsDirectory { get; set; }

        [DataMember(Name = "rule", Order = 3)]
        public string RuleId { get; set; } = string.Empty;
    }

    /// <summary>
    /// 提权清理任务的载荷（父进程 → 提权子进程）。
    ///
    /// 为什么只有"路径 + 处置方式"这么少：提权侧**只接受结构化参数、绝不接受任意命令**，
    /// 否则这个通道本身就是一个自带的 UAC 绕过后门。提权子进程据此构造 CleanupCandidate
    /// 并走同一个 CleanupExecutor，因此提权路径与普通路径用的是同一套逻辑与同一份审计。
    /// </summary>
    [DataContract]
    public sealed class ElevatedCleanupPayload
    {
        /// <summary>提权任务标识，与 App 的 headless 分发器约定一致。</summary>
        public const string TaskId = "cleanup.elevated";

        [DataMember(Name = "items", Order = 0)]
        public List<ElevatedCleanupItem> Items { get; set; } = new List<ElevatedCleanupItem>();

        /// <summary>处置方式名（CleanupDisposal 的枚举名）。提权清理只允许 Quarantine 与 RecycleBin。</summary>
        [DataMember(Name = "disposal", Order = 1)]
        public string Disposal { get; set; } = nameof(CleanupDisposal.Quarantine);
    }
}
