using System;
using System.Globalization;
using System.Runtime.Serialization;

namespace WinKit.Core.Journal
{
    /// <summary>操作危险分级，决定交互强度（见《功能规划》第 7.2 节）。</summary>
    public static class DangerLevel
    {
        public const string ReadOnly = "L0";
        public const string Reversible = "L1";
        public const string HardToReverse = "L2";
        public const string HighRisk = "L3";
    }

    public static class JournalResult
    {
        public const string Ok = "ok";
        public const string Failed = "failed";
        public const string Skipped = "skipped";
    }

    /// <summary>
    /// 一条操作记录。每行一条写入 journal.jsonl，既是审计日志，也是撤销引擎的数据来源。
    /// </summary>
    [DataContract]
    public sealed class JournalEntry
    {
        [DataMember(Name = "ts", Order = 0)]
        public string Timestamp { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        [DataMember(Name = "batch", Order = 1)]
        public string BatchId { get; set; } = string.Empty;

        [DataMember(Name = "task", Order = 2)]
        public string TaskId { get; set; } = string.Empty;

        [DataMember(Name = "level", Order = 3)]
        public string Level { get; set; } = DangerLevel.Reversible;

        [DataMember(Name = "action", Order = 4)]
        public string Action { get; set; } = string.Empty;

        [DataMember(Name = "target", Order = 5)]
        public string Target { get; set; } = string.Empty;

        [DataMember(Name = "before", Order = 6)]
        public string? Before { get; set; }

        [DataMember(Name = "after", Order = 7)]
        public string? After { get; set; }

        [DataMember(Name = "size", Order = 8)]
        public long Size { get; set; }

        [DataMember(Name = "result", Order = 9)]
        public string Result { get; set; } = JournalResult.Ok;

        /// <summary>撤销处理器标识；为 null 表示该操作不可撤销。</summary>
        [DataMember(Name = "undo", Order = 10)]
        public string? UndoKind { get; set; }

        [DataMember(Name = "elevated", Order = 11)]
        public bool Elevated { get; set; }

        /// <summary>
        /// 补充说明：失败原因、跳过原因、被测进程名等。
        /// 有了它，审计时不用再去日志里翻上下文。
        /// </summary>
        [DataMember(Name = "note", Order = 12)]
        public string? Note { get; set; }

        public bool IsUndoable => !string.IsNullOrEmpty(UndoKind) && Result == JournalResult.Ok;

        /// <summary>面向界面的单行摘要。</summary>
        public string Summary
        {
            get
            {
                var text = Action + " " + Target;
                if (Size > 0) text += "  (" + Text.ByteSize.Format(Size) + ")";
                if (Result != JournalResult.Ok) text += "  [" + Result + "]";
                return text;
            }
        }

        public override string ToString() => Timestamp + " " + Summary;
    }
}
