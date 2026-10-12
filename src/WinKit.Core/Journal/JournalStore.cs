using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WinKit.Core.Diagnostics;
using WinKit.Core.Serialization;

namespace WinKit.Core.Journal
{
    /// <summary>
    /// 追加写 JSONL 的操作日志。容忍坏行：解析失败的那一行被跳过并计数，不影响其余记录。
    /// </summary>
    public sealed class JournalStore
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private readonly object _gate = new object();
        private readonly string _path;

        public JournalStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("日志路径不能为空", nameof(path));
            _path = path;
        }

        public string FilePath => _path;

        /// <summary>读取时被跳过的坏行数量，用于自检。</summary>
        public int CorruptedLineCount { get; private set; }

        public void Append(JournalEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            lock (_gate)
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(_path, Json.Serialize(entry) + Environment.NewLine, Utf8NoBom);
            }
        }

        public void AppendRange(IEnumerable<JournalEntry> entries)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            foreach (var e in entries) Append(e);
        }

        public IReadOnlyList<JournalEntry> ReadAll()
        {
            var result = new List<JournalEntry>();
            CorruptedLineCount = 0;
            if (!File.Exists(_path)) return result;

            string[] lines;
            lock (_gate)
            {
                try
                {
                    lines = File.ReadAllLines(_path, Utf8NoBom);
                }
                catch (Exception ex)
                {
                    Log.Warn("读取操作日志失败：" + ex.Message);
                    return result;
                }
            }

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var entry = Json.TryDeserialize<JournalEntry>(line);
                if (entry == null)
                {
                    CorruptedLineCount++;
                    continue;
                }
                result.Add(entry);
            }
            return result;
        }

        /// <summary>取最近 n 条（按时间正序返回，便于界面直接显示）。</summary>
        public IReadOnlyList<JournalEntry> ReadRecent(int count)
        {
            var all = ReadAll();
            if (count <= 0 || all.Count <= count) return all;

            var slice = new List<JournalEntry>(count);
            for (int i = all.Count - count; i < all.Count; i++) slice.Add(all[i]);
            return slice;
        }

        public IReadOnlyList<JournalEntry> ReadBatch(string batchId)
        {
            var result = new List<JournalEntry>();
            if (string.IsNullOrEmpty(batchId)) return result;
            foreach (var e in ReadAll())
            {
                if (string.Equals(e.BatchId, batchId, StringComparison.Ordinal)) result.Add(e);
            }
            return result;
        }

        /// <summary>列出所有批次号，最新的在前。</summary>
        public IReadOnlyList<string> ListBatches()
        {
            var seen = new List<string>();
            var all = ReadAll();
            for (int i = all.Count - 1; i >= 0; i--)
            {
                var id = all[i].BatchId;
                if (string.IsNullOrEmpty(id)) continue;
                if (!seen.Contains(id)) seen.Add(id);
            }
            return seen;
        }

        public static string NewBatchId() => Guid.NewGuid().ToString("N").Substring(0, 8);
    }
}
