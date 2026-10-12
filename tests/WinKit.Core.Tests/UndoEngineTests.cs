using System;
using System.Collections.Generic;
using WinKit.Core.Journal;
using Xunit;

namespace WinKit.Core.Tests
{
    public class UndoEngineTests
    {
        private sealed class RecordingHandler : IUndoHandler
        {
            private readonly List<string> _order;

            public RecordingHandler(List<string> order, string kind = "file.move")
            {
                _order = order;
                Kind = kind;
            }

            public string Kind { get; }

            public string FailOn { get; set; } = string.Empty;

            public Func<JournalEntry, bool>? CanUndoPredicate { get; set; }

            public bool CanUndo(JournalEntry entry) => CanUndoPredicate?.Invoke(entry) ?? true;

            public UndoResult Undo(JournalEntry entry)
            {
                _order.Add(entry.Target);
                if (entry.Target == FailOn) return UndoResult.Fail("模拟失败");
                return UndoResult.Ok(1, "ok");
            }
        }

        private static JournalEntry Entry(string batch, string target, string? undoKind = "file.move", string result = JournalResult.Ok)
        {
            return new JournalEntry
            {
                BatchId = batch,
                Target = target,
                UndoKind = undoKind,
                Result = result,
                Action = "delete",
            };
        }

        [Fact]
        public void Register_rejects_duplicate_handler_kind()
        {
            using var tmp = new TempDir();
            var engine = new UndoEngine(new JournalStore(tmp.File("journal.jsonl")));
            var order = new List<string>();
            engine.Register(new RecordingHandler(order));

            var ex = Assert.Throws<InvalidOperationException>(() => engine.Register(new RecordingHandler(order)));

            Assert.Contains("重复注册", ex.Message);
        }

        [Fact]
        public void CanUndo_is_false_when_entry_has_no_undo_kind()
        {
            using var tmp = new TempDir();
            var engine = new UndoEngine(new JournalStore(tmp.File("journal.jsonl")));
            engine.Register(new RecordingHandler(new List<string>()));

            Assert.False(engine.CanUndo(Entry("b1", "a", undoKind: null)));
        }

        [Fact]
        public void CanUndo_is_false_for_unregistered_kind()
        {
            using var tmp = new TempDir();
            var engine = new UndoEngine(new JournalStore(tmp.File("journal.jsonl")));
            engine.Register(new RecordingHandler(new List<string>()));

            Assert.False(engine.CanUndo(Entry("b1", "a", undoKind: "registry.value")));
        }

        [Fact]
        public void CanUndo_is_false_when_operation_had_failed()
        {
            using var tmp = new TempDir();
            var engine = new UndoEngine(new JournalStore(tmp.File("journal.jsonl")));
            engine.Register(new RecordingHandler(new List<string>()));

            Assert.False(engine.CanUndo(Entry("b1", "a", result: JournalResult.Failed)));
        }

        [Fact]
        public void UndoEntry_returns_failure_instead_of_throwing_when_handler_throws()
        {
            var tmp = new TempDir();
            using (tmp)
            {
                var store = new JournalStore(tmp.File("journal.jsonl"));
                var engine = new UndoEngine(store);
                engine.Register(new ThrowingHandler());

                var result = engine.UndoEntry(Entry("b1", "a", undoKind: "boom"));

                Assert.False(result.Success);
                Assert.Contains("撤销失败", result.Message);
            }
        }

        [Fact]
        public void UndoBatch_reverts_entries_in_reverse_order()
        {
            var tmp = new TempDir();
            using (tmp)
            {
                var store = new JournalStore(tmp.File("journal.jsonl"));
                var order = new List<string>();
                var engine = new UndoEngine(store);
                engine.Register(new RecordingHandler(order));

                store.Append(Entry("b1", "first"));
                store.Append(Entry("b1", "second"));
                store.Append(Entry("b1", "third"));

                var result = engine.UndoBatch("b1");

                Assert.True(result.Success);
                Assert.Equal(3, result.Affected);
                Assert.Equal(new[] { "third", "second", "first" }, order);
            }
        }

        [Fact]
        public void UndoBatch_reports_partial_failures()
        {
            var tmp = new TempDir();
            using (tmp)
            {
                var store = new JournalStore(tmp.File("journal.jsonl"));
                var order = new List<string>();
                var engine = new UndoEngine(store);
                engine.Register(new RecordingHandler(order) { FailOn = "second" });

                store.Append(Entry("b1", "first"));
                store.Append(Entry("b1", "second"));

                var result = engine.UndoBatch("b1");

                Assert.False(result.Success);
                Assert.Contains("second", result.Message);
                Assert.Equal(1, result.Affected);
            }
        }

        [Fact]
        public void UndoBatch_fails_clearly_for_unknown_batch()
        {
            var tmp = new TempDir();
            using (tmp)
            {
                var engine = new UndoEngine(new JournalStore(tmp.File("journal.jsonl")));

                var result = engine.UndoBatch("nope");

                Assert.False(result.Success);
                Assert.Contains("找不到", result.Message);
            }
        }

        [Fact]
        public void CountUndoable_only_counts_supported_entries()
        {
            var tmp = new TempDir();
            using (tmp)
            {
                var engine = new UndoEngine(new JournalStore(tmp.File("journal.jsonl")));
                engine.Register(new RecordingHandler(new List<string>()));

                var entries = new[]
                {
                    Entry("b1", "a"),
                    Entry("b1", "b", undoKind: "registry.value"),
                    Entry("b1", "c", result: JournalResult.Failed),
                };

                Assert.Equal(1, engine.CountUndoable(entries));
            }
        }

        private sealed class ThrowingHandler : IUndoHandler
        {
            public string Kind => "boom";
            public bool CanUndo(JournalEntry entry) => true;
            public UndoResult Undo(JournalEntry entry) => throw new InvalidOperationException("设备未就绪");
        }
    }
}
