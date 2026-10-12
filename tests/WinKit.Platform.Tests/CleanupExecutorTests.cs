using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using WinKit.Core.Cleanup;
using WinKit.Core.Journal;
using WinKit.Core.Tasks;
using WinKit.Platform.Cleanup;
using WinKit.Platform.Undo;
using Xunit;

namespace WinKit.Platform.Tests
{
    /// <summary>
    /// 清理执行器。重点不是"删掉了"，而是"清理之后还能原样还原" ——
    /// 这是整个模块存在的意义，因此这里做的是真实的搬移 + 真实的撤销，不做任何桩替换。
    /// </summary>
    public class CleanupExecutorTests
    {
        /// <summary>
        /// 同步收集进度。xunit 下 SynchronizationContext 可能为空，Progress&lt;T&gt; 会把回调丢到线程池，
        /// 断言时进度可能还没到齐，所以直接在调用线程上记录（同 TempDir.cs 里的说明）。
        /// </summary>
        private sealed class ProgressCollector : IProgress<TaskProgress>
        {
            private readonly List<TaskProgress> _items = new List<TaskProgress>();

            public IReadOnlyList<TaskProgress> Items => _items;

            public void Report(TaskProgress value) => _items.Add(value);
        }

        private static CleanupCandidate Candidate(string path, bool isDirectory, long size)
        {
            return new CleanupCandidate("temp.user", "用户临时文件", path, isDirectory, size, DateTime.UtcNow.AddHours(-2));
        }

        private static JournalStore NewJournal(TempDir tmp)
        {
            return new JournalStore(Path.Combine(tmp.Path, "data", "journal.jsonl"));
        }

        private static QuarantineStore NewQuarantine(TempDir tmp)
        {
            var store = new QuarantineStore(Path.Combine(tmp.Path, "data", "quarantine"));
            store.EnsureCreated();
            return store;
        }

        private static UndoEngine NewUndoEngine(JournalStore journal)
        {
            var engine = new UndoEngine(journal);
            engine.Register(new MoveFileUndoHandler());
            return engine;
        }

        private static TaskContext Context(CancellationToken token = default)
        {
            return new TaskContext("cleanup", token);
        }

        [Fact]
        public void Quarantine_run_is_fully_undoable_end_to_end()
        {
            using var tmp = new TempDir();
            var payload = tmp.Dir("payload");
            var fileA = Path.Combine(payload, "a.txt");
            var fileB = Path.Combine(payload, "b.txt");
            File.WriteAllText(fileA, "内容 A");
            File.WriteAllText(fileB, "内容 B");
            var sizeA = new FileInfo(fileA).Length;
            var sizeB = new FileInfo(fileB).Length;

            var quarantine = NewQuarantine(tmp);
            var journal = NewJournal(tmp);
            var undo = NewUndoEngine(journal);

            var candidates = new List<CleanupCandidate>
            {
                Candidate(fileA, false, sizeA),
                Candidate(fileB, false, sizeB),
            };

            var result = CleanupExecutor.Execute(
                candidates, CleanupDisposal.Quarantine, quarantine, journal, Context());

            // ---- 第一步：文件确实被搬进了隔离区 ----
            Assert.Equal(2, result.SuccessCount);
            Assert.Equal(0, result.FailedCount);
            Assert.False(result.Cancelled);
            Assert.True(result.FreedBytes > 0);
            Assert.Equal(2, result.Items.Count);
            Assert.All(result.Items, item => Assert.True(item.Success, item.Error));

            Assert.False(File.Exists(fileA), "原文件应已被搬走");
            Assert.False(File.Exists(fileB), "原文件应已被搬走");
            Assert.Equal(2, quarantine.GetItemCount());
            // 只数批次目录里的文件：隔离区根目录下还有 manifest.jsonl
            var batchDirectory = Path.Combine(quarantine.Root, result.BatchId);
            Assert.True(Directory.Exists(batchDirectory), "项应落在 <Root>\\<batchId> 之下");
            Assert.Equal(2, Directory.GetFiles(batchDirectory, "*", SearchOption.AllDirectories).Length);
            Assert.True(Directory.Exists(payload), "只清目标项，目录本身必须留着");

            // 日志同时是撤销的数据源：每条都得是可撤销的 file.move
            var entries = journal.ReadBatch(result.BatchId);
            Assert.Equal(result.SuccessCount, entries.Count);
            foreach (var entry in entries)
            {
                Assert.Equal("cleanup", entry.TaskId);
                Assert.Equal("quarantine", entry.Action);
                Assert.Equal(DangerLevel.Reversible, entry.Level);
                Assert.Equal(JournalResult.Ok, entry.Result);
                Assert.Equal(MoveFileUndoHandler.UndoKind, entry.UndoKind);
                Assert.True(entry.IsUndoable);
                Assert.False(string.IsNullOrEmpty(entry.Before));
                Assert.False(string.IsNullOrEmpty(entry.After));
                Assert.True(File.Exists(entry.After), "After 指向隔离区里真实存在的文件");
            }

            // ---- 第二步：撤销必须把内容一字不差地还原 ----
            var undoResult = undo.UndoBatch(result.BatchId);

            Assert.True(undoResult.Success, undoResult.Message);
            Assert.Equal(2, undoResult.Affected);
            Assert.True(File.Exists(fileA), "a.txt 必须回到原路径");
            Assert.True(File.Exists(fileB), "b.txt 必须回到原路径");
            Assert.Equal("内容 A", File.ReadAllText(fileA));
            Assert.Equal("内容 B", File.ReadAllText(fileB));

            // 隔离区里不应再留副本，否则用户会以为空间没释放
            foreach (var item in quarantine.ListItems())
            {
                Assert.False(File.Exists(item.StoredPath), "还原之后隔离区里不应再留副本：" + item.StoredPath);
            }
        }

        [Fact]
        public void A_missing_candidate_fails_alone_and_does_not_stop_the_rest()
        {
            using var tmp = new TempDir();
            var goodA = tmp.File("good-a.txt");
            var goodB = tmp.File("good-b.txt");
            File.WriteAllText(goodA, "A");
            File.WriteAllText(goodB, "B");
            var ghost = tmp.File("ghost.txt");

            var quarantine = NewQuarantine(tmp);
            var journal = NewJournal(tmp);
            var undo = NewUndoEngine(journal);

            var result = CleanupExecutor.Execute(new List<CleanupCandidate>
            {
                Candidate(goodA, false, 1),
                Candidate(ghost, false, 123),
                Candidate(goodB, false, 1),
            }, CleanupDisposal.Quarantine, quarantine, journal, Context());

            Assert.Equal(2, result.SuccessCount);
            Assert.Equal(1, result.FailedCount);
            Assert.False(result.Cancelled);

            var failed = result.Items.Find(i => string.Equals(i.Path, ghost, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(failed);
            Assert.False(failed!.Success);
            Assert.Equal("已不存在", failed.Error);

            Assert.False(File.Exists(goodA), "前一项失败不应影响后续项");
            Assert.False(File.Exists(goodB), "前一项失败不应影响后续项");
            Assert.Equal(2, quarantine.GetItemCount());

            // 失败项同样入账，但不可撤销；成功项不受影响，仍然能整体还原
            var entries = journal.ReadBatch(result.BatchId);
            Assert.Equal(3, entries.Count);
            Assert.Equal(1, entries.Count(e => e.Result == JournalResult.Failed));
            var ghostEntry = entries.First(e => string.Equals(e.Target, ghost, StringComparison.OrdinalIgnoreCase));
            Assert.False(ghostEntry.IsUndoable, "失败项绝不能标成可撤销");
            Assert.NotNull(ghostEntry.Note);

            var undoResult = undo.UndoBatch(result.BatchId);
            Assert.True(undoResult.Success, undoResult.Message);
            Assert.True(File.Exists(goodA));
            Assert.True(File.Exists(goodB));
        }

        [Fact]
        public void Permanent_run_deletes_the_file_and_is_not_undoable()
        {
            using var tmp = new TempDir();
            var junk = tmp.File("junk.bin");
            File.WriteAllBytes(junk, new byte[1024]);
            var size = new FileInfo(junk).Length;

            var journal = NewJournal(tmp);
            var undo = NewUndoEngine(journal);

            var result = CleanupExecutor.Execute(new List<CleanupCandidate> { Candidate(junk, false, size) },
                CleanupDisposal.Permanent, NewQuarantine(tmp), journal, Context());

            Assert.Equal(1, result.SuccessCount);
            Assert.Equal(0, result.FailedCount);
            Assert.False(File.Exists(junk), "彻底删除后文件不能还在");

            var entries = journal.ReadBatch(result.BatchId);
            Assert.Single(entries);
            Assert.Equal("delete", entries[0].Action);
            Assert.Equal(DangerLevel.HighRisk, entries[0].Level);
            Assert.Equal(JournalResult.Ok, entries[0].Result);
            Assert.Null(entries[0].UndoKind);
            Assert.False(entries[0].IsUndoable, "彻底删除必须是不可撤销的");

            Assert.False(undo.UndoBatch(result.BatchId).Success, "L3 操作必须无法撤销");
            Assert.False(File.Exists(junk), "撤销失败之后更不能凭空造出文件");
        }

        [Fact]
        public void An_already_cancelled_token_returns_a_cancelled_result_instead_of_throwing()
        {
            using var tmp = new TempDir();
            var file = tmp.File("untouched.txt");
            File.WriteAllText(file, "keep me");

            var journal = NewJournal(tmp);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            CleanupExecutionResult? result = null;
            var thrown = Record.Exception(() => result = CleanupExecutor.Execute(
                new List<CleanupCandidate> { Candidate(file, false, 7) },
                CleanupDisposal.Quarantine, NewQuarantine(tmp), journal,
                new TaskContext("cleanup", cts.Token)));

            Assert.Null(thrown);
            Assert.NotNull(result);
            Assert.True(result!.Cancelled, "取消必须体现在结果里，而不是抛异常");
            Assert.Equal(0, result.SuccessCount);
            Assert.Equal(0, result.FailedCount);
            Assert.True(File.Exists(file), "取消后不得动任何文件");
            Assert.Contains("已取消", result.SummaryText);
            Assert.Empty(journal.ReadBatch(result.BatchId));
        }

        [Fact]
        public void An_empty_candidate_list_produces_a_clean_summary()
        {
            using var tmp = new TempDir();
            var journal = NewJournal(tmp);

            var result = CleanupExecutor.Execute(new List<CleanupCandidate>(),
                CleanupDisposal.Quarantine, NewQuarantine(tmp), journal, Context());

            Assert.Equal(0, result.SuccessCount);
            Assert.Equal(0, result.FailedCount);
            Assert.False(result.Cancelled);
            Assert.Empty(result.Items);
            Assert.False(string.IsNullOrWhiteSpace(result.SummaryText));
            Assert.Contains("0 项", result.SummaryText);
            Assert.DoesNotContain("异常", result.SummaryText);
            Assert.Empty(journal.ReadBatch(result.BatchId));
        }

        [Fact]
        public void Progress_is_reported_per_item_and_ends_at_completion()
        {
            using var tmp = new TempDir();
            var candidates = new List<CleanupCandidate>();
            for (int i = 0; i < 3; i++)
            {
                var path = tmp.File("p" + i + ".tmp");
                File.WriteAllText(path, "x");
                candidates.Add(Candidate(path, false, 1));
            }

            var progress = new ProgressCollector();
            var result = CleanupExecutor.Execute(candidates, CleanupDisposal.Quarantine,
                NewQuarantine(tmp), NewJournal(tmp),
                new TaskContext("cleanup", CancellationToken.None, progress));

            Assert.Equal(3, result.SuccessCount);
            Assert.True(progress.Items.Count >= 3, "每项至少上报一次进度");
            Assert.All(progress.Items, p => Assert.InRange(p.Fraction, 0d, 1d));
            Assert.Equal(1d, progress.Items[progress.Items.Count - 1].Fraction);
            Assert.Contains(progress.Items, p => p.Message != null && p.Message!.StartsWith("正在清理：", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// 回收站查询。**绝不测 Empty()** —— 那会真的清空用户的回收站，测试不允许有这种副作用。
    /// 放在本文件里是因为清理模块只允许新增两个测试文件。
    /// </summary>
    public class RecycleBinTests
    {
        [Fact]
        public void GetSizeBytes_returns_a_size_or_minus_one_without_throwing()
        {
            var size = RecycleBin.GetSizeBytes();

            Assert.True(size >= -1, "查不到时应返回 -1，而不是别的负数，实际 " + size);
        }
    }
}
