using System;
using System.Collections.Generic;
using System.Text;

namespace WinKit.Core.Journal
{
    public sealed class UndoResult
    {
        private UndoResult(bool success, string message, int affected)
        {
            Success = success;
            Message = message;
            Affected = affected;
        }

        public bool Success { get; }
        public string Message { get; }
        public int Affected { get; }

        public static UndoResult Ok(int affected, string message) => new UndoResult(true, message, affected);
        public static UndoResult Fail(string message) => new UndoResult(false, message, 0);

        /// <summary>部分成功：整体判定为失败，但保留已经撤销成功的数量供界面展示。</summary>
        public static UndoResult Partial(int affected, string message) => new UndoResult(false, message, affected);
    }

    /// <summary>
    /// 撤销处理器。Core 只定义契约；具体实现放在 WinKit.Platform（可以碰文件系统/注册表），
    /// 这样 Core 保持无副作用、可纯单测。
    /// </summary>
    public interface IUndoHandler
    {
        /// <summary>与 JournalEntry.UndoKind 对应，例如 "file.move"、"registry.value"。</summary>
        string Kind { get; }

        bool CanUndo(JournalEntry entry);

        UndoResult Undo(JournalEntry entry);
    }

    /// <summary>
    /// 撤销中心。按批次倒序回滚：先撤销最后一步，避免同一批次内的操作互相踩到中间状态。
    /// </summary>
    public sealed class UndoEngine
    {
        private readonly Dictionary<string, IUndoHandler> _handlers =
            new Dictionary<string, IUndoHandler>(StringComparer.OrdinalIgnoreCase);

        public UndoEngine(JournalStore store)
        {
            Store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public JournalStore Store { get; }

        public void Register(IUndoHandler handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (string.IsNullOrWhiteSpace(handler.Kind)) throw new ArgumentException("撤销处理器必须声明 Kind", nameof(handler));
            if (_handlers.ContainsKey(handler.Kind))
            {
                throw new InvalidOperationException("撤销处理器重复注册：" + handler.Kind);
            }
            _handlers.Add(handler.Kind, handler);
        }

        public bool CanUndo(JournalEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (!entry.IsUndoable) return false;
            IUndoHandler? handler;
            if (!_handlers.TryGetValue(entry.UndoKind!, out handler)) return false;
            try
            {
                return handler!.CanUndo(entry);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public UndoResult UndoEntry(JournalEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (!entry.IsUndoable) return UndoResult.Fail("该操作没有记录撤销方式，无法撤销");
            if (string.IsNullOrEmpty(entry.UndoKind)) return UndoResult.Fail("该操作没有记录撤销方式，无法撤销");

            IUndoHandler? handler;
            if (!_handlers.TryGetValue(entry.UndoKind!, out handler))
            {
                return UndoResult.Fail("缺少撤销处理器：" + entry.UndoKind);
            }

            try
            {
                return handler!.Undo(entry);
            }
            catch (Exception ex)
            {
                return UndoResult.Fail("撤销失败：" + ex.Message);
            }
        }

        /// <summary>整体撤销一个批次中的所有可撤销条目（倒序执行）。</summary>
        public UndoResult UndoBatch(string batchId)
        {
            var entries = Store.ReadBatch(batchId);
            if (entries.Count == 0) return UndoResult.Fail("找不到该批次的记录");

            int affected = 0;
            var failures = new StringBuilder();

            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i];
                if (!CanUndo(entry)) continue;

                var result = UndoEntry(entry);
                if (result.Success)
                {
                    affected += Math.Max(result.Affected, 1);
                }
                else
                {
                    failures.Append(entry.Target).Append("：").Append(result.Message).Append("； ");
                }
            }

            if (failures.Length > 0)
            {
                return UndoResult.Partial(affected, "部分项目撤销失败（已撤销 " + affected + " 项）：" + failures.ToString().TrimEnd());
            }
            if (affected == 0) return UndoResult.Fail("该批次没有可撤销的项目");

            return UndoResult.Ok(affected, "已撤销 " + affected + " 项");
        }

        public int CountUndoable(IEnumerable<JournalEntry> entries)
        {
            int count = 0;
            foreach (var e in entries)
            {
                if (CanUndo(e)) count++;
            }
            return count;
        }
    }
}
