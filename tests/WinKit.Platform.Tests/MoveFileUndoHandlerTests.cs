using System;
using System.IO;
using WinKit.Core.Journal;
using WinKit.Platform.FileSystem;
using WinKit.Platform.Undo;
using Xunit;

namespace WinKit.Platform.Tests
{
    /// <summary>
    /// 撤销处理器。走真实的文件移动（临时目录内），不使用回收站。
    /// </summary>
    public class MoveFileUndoHandlerTests
    {
        private static JournalEntry MoveEntry(string before, string after)
        {
            return new JournalEntry
            {
                Before = before,
                After = after,
                UndoKind = MoveFileUndoHandler.UndoKind,
                Result = JournalResult.Ok,
                Action = "move",
                Target = before,
            };
        }

        [Fact]
        public void Handler_declares_the_move_kind()
        {
            Assert.Equal("file.move", new MoveFileUndoHandler().Kind);
        }

        [Fact]
        public void CanUndo_is_true_after_a_real_safe_move()
        {
            using var tmp = new TempDir();
            var before = tmp.File("before.txt");
            var after = tmp.File("after.txt");
            File.WriteAllText(before, "content");
            FileOperations.SafeMove(before, after);

            var handler = new MoveFileUndoHandler();
            var entry = MoveEntry(before, after);

            Assert.True(handler.CanUndo(entry));
        }

        [Fact]
        public void Undo_moves_the_file_back_and_removes_the_new_location()
        {
            using var tmp = new TempDir();
            var before = tmp.File("before.txt");
            var after = tmp.File("after.txt");
            File.WriteAllText(before, "content");
            FileOperations.SafeMove(before, after);

            var handler = new MoveFileUndoHandler();
            var entry = MoveEntry(before, after);

            var result = handler.Undo(entry);

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, result.Affected);
            Assert.True(File.Exists(before), "文件应回到原位置");
            Assert.False(File.Exists(after), "新位置不应再存在文件");
            Assert.Equal("content", File.ReadAllText(before));
        }

        [Fact]
        public void Undo_restores_a_renamed_file_after_a_cross_directory_move()
        {
            using var tmp = new TempDir();
            var before = tmp.File("payload.bin");
            File.WriteAllBytes(before, new byte[] { 1, 2, 3, 4 });
            var after = Path.Combine(tmp.Dir("moved"), "payload.bin");
            FileOperations.SafeMove(before, after);

            var handler = new MoveFileUndoHandler();
            var result = handler.Undo(MoveEntry(before, after));

            Assert.True(result.Success, result.Message);
            Assert.True(File.Exists(before));
            Assert.False(File.Exists(after));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(before));
        }

        [Fact]
        public void Undo_restores_a_moved_directory()
        {
            using var tmp = new TempDir();
            var before = tmp.Dir("tree");
            File.WriteAllText(Path.Combine(before, "a.txt"), "a");
            var after = tmp.File("tree-moved");
            FileOperations.SafeMove(before, after);

            var handler = new MoveFileUndoHandler();
            Assert.True(handler.CanUndo(MoveEntry(before, after)));

            var result = handler.Undo(MoveEntry(before, after));

            Assert.True(result.Success, result.Message);
            Assert.True(Directory.Exists(before));
            Assert.False(Directory.Exists(after));
            Assert.Equal("a", File.ReadAllText(Path.Combine(before, "a.txt")));
        }

        [Fact]
        public void CanUndo_is_false_when_the_after_path_does_not_exist()
        {
            using var tmp = new TempDir();
            var before = tmp.File("before.txt");
            var after = tmp.File("missing.txt");

            var handler = new MoveFileUndoHandler();

            Assert.False(handler.CanUndo(MoveEntry(before, after)));
        }

        [Fact]
        public void CanUndo_is_false_when_the_original_path_is_occupied_again()
        {
            using var tmp = new TempDir();
            var before = tmp.File("before.txt");
            var after = tmp.File("after.txt");
            File.WriteAllText(before, "original");
            FileOperations.SafeMove(before, after);

            // 别的操作又在原路径放了东西 —— 此时撤销会覆盖它，必须拒绝
            File.WriteAllText(before, "new occupant");

            var handler = new MoveFileUndoHandler();

            Assert.False(handler.CanUndo(MoveEntry(before, after)));
        }

        [Fact]
        public void CanUndo_is_false_when_the_original_path_is_occupied_by_a_directory()
        {
            using var tmp = new TempDir();
            var before = tmp.File("before.txt");
            var after = tmp.File("after.txt");
            File.WriteAllText(before, "original");
            FileOperations.SafeMove(before, after);

            Directory.CreateDirectory(before);

            var handler = new MoveFileUndoHandler();

            Assert.False(handler.CanUndo(MoveEntry(before, after)));
        }

        [Fact]
        public void Empty_paths_make_CanUndo_false_and_Undo_fail_without_throwing()
        {
            var handler = new MoveFileUndoHandler();

            var entry = MoveEntry(string.Empty, string.Empty);

            Assert.False(handler.CanUndo(entry));

            var result = handler.Undo(entry);

            Assert.False(result.Success);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
        }

        [Fact]
        public void Null_paths_make_CanUndo_false_and_Undo_fail_without_throwing()
        {
            var handler = new MoveFileUndoHandler();

            var entry = new JournalEntry
            {
                Before = null,
                After = null,
                UndoKind = MoveFileUndoHandler.UndoKind,
                Result = JournalResult.Ok,
            };

            Assert.False(handler.CanUndo(entry));
            Assert.False(handler.Undo(entry).Success);
        }

        [Fact]
        public void Undo_returns_failure_when_the_source_has_disappeared()
        {
            using var tmp = new TempDir();
            var entry = MoveEntry(tmp.File("before.txt"), tmp.File("after.txt"));

            var handler = new MoveFileUndoHandler();
            var result = handler.Undo(entry);

            Assert.False(result.Success);
            // 必须返回失败对象而不是抛异常
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
        }

        [Fact]
        public void CanUndo_is_false_for_a_null_entry()
        {
            Assert.False(new MoveFileUndoHandler().CanUndo(null!));
        }
    }
}
