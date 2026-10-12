using System;
using System.IO;
using WinKit.Core.Diagnostics;
using WinKit.Core.Journal;
using WinKit.Platform.FileSystem;

namespace WinKit.Platform.Undo
{
    /// <summary>
    /// 移动/重命名类操作的撤销。
    /// 约定：JournalEntry.Before = 操作前路径，JournalEntry.After = 操作后路径。
    /// 撤销即把 After 搬回 Before。
    /// </summary>
    public sealed class MoveFileUndoHandler : IUndoHandler
    {
        public const string UndoKind = "file.move";

        public string Kind => UndoKind;

        public bool CanUndo(JournalEntry entry)
        {
            if (entry == null) return false;
            if (string.IsNullOrEmpty(entry.Before) || string.IsNullOrEmpty(entry.After)) return false;

            // 源（当前所在位置）必须存在，且目标位置不能已被占用
            var sourceExists = File.Exists(entry.After!) || Directory.Exists(entry.After!);
            var targetFree = !File.Exists(entry.Before!) && !Directory.Exists(entry.Before!);
            return sourceExists && targetFree;
        }

        public UndoResult Undo(JournalEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrEmpty(entry.Before) || string.IsNullOrEmpty(entry.After))
            {
                return UndoResult.Fail("记录缺少路径信息，无法撤销");
            }

            if (!CanUndo(entry))
            {
                return UndoResult.Fail("原文件已不在记录位置，或原路径已被占用");
            }

            try
            {
                FileOperations.SafeMove(entry.After!, entry.Before!);
                Log.Info("已撤销移动：" + entry.After + " → " + entry.Before);
                return UndoResult.Ok(1, "已还原 " + Path.GetFileName(entry.Before!));
            }
            catch (Exception ex)
            {
                Log.Warn("撤销移动失败：" + ex.Message);
                return UndoResult.Fail(ex.Message);
            }
        }
    }
}
