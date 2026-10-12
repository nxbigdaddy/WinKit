using System.IO;
using System.Text;
using WinKit.Core.Journal;
using Xunit;

namespace WinKit.Core.Tests
{
    public class JournalStoreTests
    {
        private static JournalEntry Entry(string batch, string target, string? undo = "file.move")
        {
            return new JournalEntry
            {
                BatchId = batch,
                TaskId = "cleanup.temp",
                Level = DangerLevel.Reversible,
                Action = "delete",
                Target = target,
                Size = 1024,
                Result = JournalResult.Ok,
                UndoKind = undo,
            };
        }

        [Fact]
        public void Append_then_ReadAll_roundtrips_entry()
        {
            using var tmp = new TempDir();
            var store = new JournalStore(tmp.File("journal.jsonl"));

            store.Append(Entry("b1", @"C:\Temp\a.tmp"));
            var all = store.ReadAll();

            Assert.Single(all);
            Assert.Equal(@"C:\Temp\a.tmp", all[0].Target);
            Assert.Equal("b1", all[0].BatchId);
            Assert.Equal(1024, all[0].Size);
            Assert.Equal("file.move", all[0].UndoKind);
            Assert.True(all[0].IsUndoable);
        }

        [Fact]
        public void ReadAll_skips_corrupted_lines_without_losing_valid_ones()
        {
            using var tmp = new TempDir();
            var path = tmp.File("journal.jsonl");
            var store = new JournalStore(path);

            store.Append(Entry("b1", "first"));
            File.AppendAllText(path, "这不是 JSON" + System.Environment.NewLine, new UTF8Encoding(false));
            store.Append(Entry("b1", "second"));

            var all = store.ReadAll();

            Assert.Equal(2, all.Count);
            Assert.Equal(1, store.CorruptedLineCount);
        }

        [Fact]
        public void ReadRecent_returns_tail_in_chronological_order()
        {
            using var tmp = new TempDir();
            var store = new JournalStore(tmp.File("journal.jsonl"));
            for (int i = 1; i <= 5; i++) store.Append(Entry("b1", "item" + i));

            var recent = store.ReadRecent(2);

            Assert.Equal(2, recent.Count);
            Assert.Equal("item4", recent[0].Target);
            Assert.Equal("item5", recent[1].Target);
        }

        [Fact]
        public void ReadBatch_filters_by_batch_id()
        {
            using var tmp = new TempDir();
            var store = new JournalStore(tmp.File("journal.jsonl"));
            store.Append(Entry("b1", "a"));
            store.Append(Entry("b2", "b"));
            store.Append(Entry("b1", "c"));

            var batch = store.ReadBatch("b1");

            Assert.Equal(2, batch.Count);
            Assert.Equal("a", batch[0].Target);
            Assert.Equal("c", batch[1].Target);
        }

        [Fact]
        public void ListBatches_returns_newest_first()
        {
            using var tmp = new TempDir();
            var store = new JournalStore(tmp.File("journal.jsonl"));
            store.Append(Entry("old", "a"));
            store.Append(Entry("new", "b"));

            var batches = store.ListBatches();

            Assert.Equal(2, batches.Count);
            Assert.Equal("new", batches[0]);
            Assert.Equal("old", batches[1]);
        }

        [Fact]
        public void NewBatchId_is_short_and_unique()
        {
            var a = JournalStore.NewBatchId();
            var b = JournalStore.NewBatchId();

            Assert.Equal(8, a.Length);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Entry_is_not_undoable_when_operation_failed()
        {
            var entry = Entry("b1", "target");
            entry.Result = JournalResult.Failed;

            Assert.False(entry.IsUndoable);
        }
    }
}
