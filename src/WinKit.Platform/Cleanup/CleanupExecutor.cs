using System;
using System.Collections.Generic;
using System.IO;
using WinKit.Core.Cleanup;
using WinKit.Core.Diagnostics;
using WinKit.Core.Journal;
using WinKit.Core.Tasks;
using WinKit.Core.Text;
using WinKit.Platform.FileSystem;
using WinKit.Platform.Security;
using WinKit.Platform.Undo;

namespace WinKit.Platform.Cleanup
{
    /// <summary>单个候选的处理结果。</summary>
    public sealed class CleanupItemResult
    {
        public string Path { get; set; } = string.Empty;

        public bool Success { get; set; }

        /// <summary>该项的大小（来自扫描结果）。</summary>
        public long Size { get; set; }

        /// <summary>失败原因，成功时为 null。面向用户，必须是中文。</summary>
        public string? Error { get; set; }
    }

    /// <summary>一次清理的完整结果。</summary>
    public sealed class CleanupExecutionResult
    {
        /// <summary>批次号。界面拿它调 UndoEngine.UndoBatch 做"撤销上一次清理"。</summary>
        public string BatchId { get; set; } = string.Empty;

        public CleanupDisposal Disposal { get; set; }

        public int SuccessCount { get; set; }

        public int FailedCount { get; set; }

        /// <summary>
        /// 成功处理项的大小合计（不是磁盘真正释放的字节数）：
        /// 隔离区和回收站仍然占着磁盘，只有彻底删除才真正释放。
        /// </summary>
        public long FreedBytes { get; set; }

        public bool Cancelled { get; set; }

        public List<CleanupItemResult> Items { get; set; } = new List<CleanupItemResult>();

        /// <summary>面向用户的一句话结论。</summary>
        public string SummaryText
        {
            get
            {
                var text = "已清理 " + SuccessCount + " 项，释放 " + ByteSize.Format(FreedBytes);
                if (FailedCount > 0) text += "（" + FailedCount + " 项失败）";
                if (Cancelled) text += "（已取消）";
                return text;
            }
        }
    }

    /// <summary>
    /// 清理执行层。设计上的三条硬性要求：
    ///
    /// 1. **单项失败不影响其他项**：每个候选独立 try/catch，被占用、权限不足、路径消失都只让那一项失败。
    /// 2. **可撤销的项一落地就写日志**：搬移之后立刻 Append 一条 JournalEntry，
    ///    这样即使进程随后崩溃或被取消，已经搬走的文件仍然能被 UndoEngine 找回来。
    /// 3. **取消是正常收尾而不是异常**：取消时把 Cancelled 置真后返回，绝不抛 OperationCanceledException ——
    ///    异常会让界面丢掉 BatchId，而已经写入日志的项本来是可以撤销的。
    /// </summary>
    public static class CleanupExecutor
    {
        /// <summary>清理类日志统一使用这个 TaskId，界面按它过滤。</summary>
        public const string TaskId = "cleanup";

        public static CleanupExecutionResult Execute(
            IReadOnlyList<CleanupCandidate> candidates,
            CleanupDisposal disposal,
            QuarantineStore quarantine,
            JournalStore journal,
            TaskContext context)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            if (journal == null) throw new ArgumentNullException(nameof(journal));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (disposal == CleanupDisposal.Quarantine && quarantine == null)
            {
                throw new ArgumentNullException(nameof(quarantine), "隔离处置必须提供隔离区");
            }

            var result = new CleanupExecutionResult
            {
                BatchId = JournalStore.NewBatchId(),
                Disposal = disposal,
                Items = new List<CleanupItemResult>(candidates.Count),
            };

            // 提权状态对整个批次是常量，查一次即可（内部要走 WindowsIdentity）
            var elevated = Elevation.IsElevated;
            var total = candidates.Count;

            for (int i = 0; i < total; i++)
            {
                // 处理前检查取消：这里本可以调 context.ThrowIfCancelled()，但取消必须是正常收尾，
                // 所以只判断不抛。
                if (context.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    break;
                }

                var candidate = candidates[i];
                context.Report(total == 0 ? 1d : i / (double)total, "正在清理：" + DisplayName(candidate.Path));

                var item = ProcessOne(candidate, disposal, quarantine, journal, result.BatchId, elevated);
                result.Items.Add(item);
                if (item.Success)
                {
                    result.SuccessCount++;
                    result.FreedBytes += item.Size;
                }
                else
                {
                    result.FailedCount++;
                }

                // 处理后再查一次：取消发生在本项完成之后时，本项已经搬移且日志已经落盘，
                // 也就是说它仍然可以被撤销。因此这里同样只收尾、不抛异常。
                if (context.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    break;
                }
            }

            context.Report(TaskProgress.Completed(result.SummaryText));
            context.Info(result.SummaryText);
            return result;
        }

        private static CleanupItemResult ProcessOne(
            CleanupCandidate candidate,
            CleanupDisposal disposal,
            QuarantineStore quarantine,
            JournalStore journal,
            string batchId,
            bool elevated)
        {
            var path = candidate.Path;
            var item = new CleanupItemResult { Path = path, Size = candidate.Size };

            if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
            {
                // 扫描到确认之间文件被程序自己删掉是常态（临时目录尤其如此），
                // 这不是错误，所以只记 Debug，界面把它算作"未处理"。
                item.Success = false;
                item.Error = "已不存在";
                Log.Debug("清理时跳过已不存在的项：" + path);
                AppendFailure(journal, batchId, elevated, disposal, path, "已不存在", candidate.Size);
                return item;
            }

            string? storedPath = null;
            try
            {
                JournalEntry entry;

                switch (disposal)
                {
                    case CleanupDisposal.Quarantine:
                        // 隔离：一次文件移动，因而 100% 可撤销
                        var moved = quarantine.Move(path, batchId, candidate.Size, Directory.Exists(path));
                        storedPath = moved.StoredPath;
                        entry = NewEntry(batchId, elevated, "quarantine", DangerLevel.Reversible,
                            path, candidate.Size, before: path, after: moved.StoredPath);
                        entry.UndoKind = MoveFileUndoHandler.UndoKind;
                        break;

                    case CleanupDisposal.RecycleBin:
                        // 交给系统回收站：WinKit 内无法撤销，只能去回收站还原，所以 UndoKind 留空
                        if (Directory.Exists(path)) FileOperations.MoveDirectoryToRecycleBin(path);
                        else FileOperations.MoveFileToRecycleBin(path);
                        entry = NewEntry(batchId, elevated, "recycle", DangerLevel.Reversible,
                            path, candidate.Size, before: null, after: null);
                        entry.Note = "已移入 Windows 回收站，请到回收站还原";
                        break;

                    default:
                        FileOperations.DeletePermanently(path);
                        entry = NewEntry(batchId, elevated, "delete", DangerLevel.HighRisk,
                            path, candidate.Size, before: null, after: null);
                        entry.Note = "彻底删除，不可撤销";
                        break;
                }

                // 立刻落盘：先写日志再处理下一项，进程中途退出也不会丢掉已经搬走的文件的线索
                journal.Append(entry);
                item.Success = true;
            }
            catch (Exception ex)
            {
                item.Success = false;
                // 隔离搬移成功后日志写入失败时，必须把文件的实际位置告诉用户，否则文件就"失踪"了
                item.Error = storedPath == null
                    ? ex.Message
                    : ex.Message + "（文件已移入隔离区：" + storedPath + "）";

                Log.Warn("清理失败：" + path + " → " + ex.Message);
                AppendFailure(journal, batchId, elevated, disposal, path, item.Error, candidate.Size);
            }

            return item;
        }

        private static JournalEntry NewEntry(string batchId, bool elevated, string action, string level,
            string target, long size, string? before, string? after)
        {
            return new JournalEntry
            {
                BatchId = batchId,
                TaskId = TaskId,
                Level = level,
                Action = action,
                Target = target,
                Before = before,
                After = after,
                Size = size,
                Result = JournalResult.Ok,
                UndoKind = null,
                Elevated = elevated,
            };
        }

        /// <summary>
        /// 失败项同样入账：审计时要能看到"当时试图清理什么、为什么没成功"。
        /// 失败条目的 UndoKind 一律为 null —— 失败意味着状态不确定，标成可撤销只会误导撤销引擎。
        /// </summary>
        private static void AppendFailure(JournalStore journal, string batchId, bool elevated,
            CleanupDisposal disposal, string path, string? note, long size)
        {
            var entry = NewEntry(batchId, elevated, ActionOf(disposal), LevelOf(disposal),
                path, size, before: null, after: null);
            entry.Result = JournalResult.Failed;
            entry.Note = note;

            try
            {
                journal.Append(entry);
            }
            catch (Exception ex)
            {
                // 连失败记录都写不进去时只能记进程日志，绝不能让异常从执行器里逃出去
                Log.Warn("写入清理失败记录时出错：" + path + " → " + ex.Message);
            }
        }

        private static string ActionOf(CleanupDisposal disposal) => disposal switch
        {
            CleanupDisposal.Quarantine => "quarantine",
            CleanupDisposal.RecycleBin => "recycle",
            _ => "delete",
        };

        private static string LevelOf(CleanupDisposal disposal) => disposal switch
        {
            CleanupDisposal.Quarantine => DangerLevel.Reversible,
            CleanupDisposal.RecycleBin => DangerLevel.Reversible,
            _ => DangerLevel.HighRisk,
        };

        /// <summary>进度文案里只显示文件名，完整路径太长会把界面撑爆。</summary>
        private static string DisplayName(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "未知项";
            var name = Path.GetFileName(path);
            return string.IsNullOrEmpty(name) ? path : name;
        }
    }
}
