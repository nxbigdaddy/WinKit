using System;
using System.IO;
using WinKit.Core.Rules;
using WinKit.Platform.Cleanup;
using Xunit;

namespace WinKit.Platform.Tests
{
    /// <summary>
    /// 隔离区。存储布局是 &lt;Root&gt;\&lt;batchId&gt;\&lt;序号&gt;_&lt;原文件名&gt; 加一份 manifest.jsonl，
    /// 测试全部在临时目录内进行，不使用回收站。
    /// </summary>
    public class QuarantineStoreTests
    {
        /// <summary>隔离区固定放在临时目录的 data\quarantine 下，保证与被清理的项不在同一棵树里。</summary>
        private static QuarantineStore NewStore(TempDir tmp)
        {
            var store = new QuarantineStore(Path.Combine(tmp.Path, "data", "quarantine"));
            store.EnsureCreated();
            return store;
        }

        [Fact]
        public void EnsureCreated_creates_the_root_directory()
        {
            using var tmp = new TempDir();
            var root = Path.Combine(tmp.Path, "nested", "quarantine");
            var store = new QuarantineStore(root);

            Assert.False(Directory.Exists(root));
            store.EnsureCreated();

            Assert.True(Directory.Exists(root));
            Assert.Equal(root, store.Root, ignoreCase: true);
        }

        [Fact]
        public void Move_relocates_a_file_and_records_it_in_the_manifest()
        {
            using var tmp = new TempDir();
            var store = NewStore(tmp);
            var source = tmp.File("payload.txt");
            File.WriteAllText(source, "payload-content");
            var size = new FileInfo(source).Length;

            var item = store.Move(source, "batch1", size, isDirectory: false);

            Assert.False(File.Exists(source), "原路径不应再存在");
            Assert.True(File.Exists(item.StoredPath), "隔离区里必须有这个文件");
            Assert.Equal("payload-content", File.ReadAllText(item.StoredPath));
            Assert.Equal(size, item.Size);
            Assert.Equal(size, new FileInfo(item.StoredPath).Length);
            Assert.Equal("batch1", item.BatchId);
            Assert.False(item.IsDirectory);
            Assert.False(string.IsNullOrWhiteSpace(item.MovedAt));
            Assert.True(PathGuard.IsUnder(item.StoredPath, store.Root), "存放位置必须在隔离区之下");

            Assert.Equal(1, store.GetItemCount());
            Assert.True(store.GetTotalSize() > 0);

            var items = store.ListItems();
            Assert.Single(items);
            Assert.Equal(source, items[0].OriginalPath, ignoreCase: true);
            Assert.Equal(item.StoredPath, items[0].StoredPath, ignoreCase: true);
            Assert.Equal(size, items[0].Size);
        }

        [Fact]
        public void Move_relocates_a_directory_with_its_children()
        {
            using var tmp = new TempDir();
            var store = NewStore(tmp);
            var source = tmp.Dir("cache");
            File.WriteAllText(Path.Combine(source, "a.txt"), "a");
            Directory.CreateDirectory(Path.Combine(source, "sub"));
            File.WriteAllText(Path.Combine(source, "sub", "b.txt"), "b");

            var item = store.Move(source, "batch1", 2, isDirectory: true);

            Assert.False(Directory.Exists(source), "原目录不应再存在");
            Assert.True(Directory.Exists(item.StoredPath), "隔离区里必须有这个目录");
            Assert.True(item.IsDirectory);
            Assert.Equal("a", File.ReadAllText(Path.Combine(item.StoredPath, "a.txt")));
            Assert.Equal("b", File.ReadAllText(Path.Combine(item.StoredPath, "sub", "b.txt")));

            var items = store.ListItems();
            Assert.Single(items);
            Assert.True(items[0].IsDirectory);
        }

        [Fact]
        public void Move_disambiguates_same_named_files_within_one_batch()
        {
            using var tmp = new TempDir();
            var store = NewStore(tmp);
            var first = Path.Combine(tmp.Dir("chrome"), "data_0");
            var second = Path.Combine(tmp.Dir("edge"), "data_0");
            File.WriteAllText(first, "chrome");
            File.WriteAllText(second, "edge");

            var a = store.Move(first, "batch1", 6, isDirectory: false);
            var b = store.Move(second, "batch1", 4, isDirectory: false);

            Assert.False(string.Equals(a.StoredPath, b.StoredPath, StringComparison.OrdinalIgnoreCase),
                "同名文件必须落到不同位置，否则第二次搬移会失败");
            Assert.True(File.Exists(a.StoredPath));
            Assert.True(File.Exists(b.StoredPath));
            Assert.Equal("chrome", File.ReadAllText(a.StoredPath));
            Assert.Equal("edge", File.ReadAllText(b.StoredPath));

            var dirA = Path.GetDirectoryName(a.StoredPath) ?? string.Empty;
            var dirB = Path.GetDirectoryName(b.StoredPath) ?? string.Empty;
            Assert.False(string.IsNullOrEmpty(dirA));
            Assert.Equal(dirA, dirB, ignoreCase: true);

            Assert.Equal(2, store.GetItemCount());
            Assert.Equal(10L, store.GetTotalSize());
        }

        [Fact]
        public void Purge_frees_space_and_keeps_the_root_directory()
        {
            using var tmp = new TempDir();
            var store = NewStore(tmp);
            var first = tmp.File("one.bin");
            var second = tmp.File("two.bin");
            File.WriteAllBytes(first, new byte[2048]);
            File.WriteAllBytes(second, new byte[4096]);
            var contentsSize = new FileInfo(first).Length + new FileInfo(second).Length;

            store.Move(first, "b1", new FileInfo(first).Length, false);
            store.Move(second, "b1", new FileInfo(second).Length, false);
            Assert.Equal(2, store.GetItemCount());

            var ok = store.Purge(out var freed, out var failed);

            Assert.True(ok);
            Assert.Equal(0, failed);
            // 释放量按实际测量计算，因此至少覆盖两个文件（清单本身也在被删之列）
            Assert.True(freed >= contentsSize, "释放字节数应不少于被搬走的内容，实际 " + freed);
            Assert.Equal(0, store.GetItemCount());
            Assert.Equal(0, store.GetTotalSize());
            Assert.Empty(store.ListItems());
            Assert.True(Directory.Exists(store.Root), "隔离区目录本身必须保留");
            Assert.Empty(Directory.GetFileSystemEntries(store.Root));
        }

        [Fact]
        public void PurgeBatch_removes_only_the_requested_batch()
        {
            using var tmp = new TempDir();
            var store = NewStore(tmp);
            var keepSource = tmp.File("keep.txt");
            var dropSource = tmp.File("drop.txt");
            File.WriteAllText(keepSource, "keep");
            File.WriteAllText(dropSource, "drop");

            var keep = store.Move(keepSource, "keep-batch", 4, false);
            var drop = store.Move(dropSource, "drop-batch", 4, false);

            var ok = store.PurgeBatch("drop-batch", out var freed, out var failed);

            Assert.True(ok);
            Assert.Equal(0, failed);
            Assert.True(freed > 0);
            Assert.False(File.Exists(drop.StoredPath), "被清理批次的文件应已删除");
            Assert.True(File.Exists(keep.StoredPath), "其它批次必须原样保留");

            var items = store.ListItems();
            Assert.Single(items);
            Assert.Equal("keep-batch", items[0].BatchId);
            Assert.Equal(keep.StoredPath, items[0].StoredPath, ignoreCase: true);

            // 清空后的空批次目录也应该一起收掉
            Assert.False(Directory.Exists(Path.GetDirectoryName(drop.StoredPath) ?? string.Empty));
        }

        [Fact]
        public void Move_rejects_a_source_inside_the_quarantine_root()
        {
            using var tmp = new TempDir();
            var store = NewStore(tmp);
            var inner = Path.Combine(store.Root, "1_already-there.txt");
            File.WriteAllText(inner, "x");

            Assert.Throws<InvalidOperationException>(() => store.Move(inner, "b1", 1, false));

            Assert.True(File.Exists(inner), "被拒绝之后不得动到文件");
            Assert.Equal(0, store.GetItemCount());
        }

        [Fact]
        public void Move_throws_for_a_missing_source()
        {
            using var tmp = new TempDir();
            var store = NewStore(tmp);

            Assert.Throws<FileNotFoundException>(() => store.Move(tmp.File("ghost.txt"), "b1", 0, false));

            Assert.Equal(0, store.GetItemCount());
        }

        [Fact]
        public void ListItems_skips_a_corrupted_manifest_line_without_throwing()
        {
            using var tmp = new TempDir();
            var store = NewStore(tmp);
            var source = tmp.File("good.txt");
            File.WriteAllText(source, "good");
            var item = store.Move(source, "b1", 4, false);

            // 模拟断电/磁盘错误留下的半行 JSON
            File.AppendAllText(store.ManifestPath, "{ 这一行不是合法的 JSON" + Environment.NewLine);

            var items = store.ListItems();

            Assert.Single(items, x => string.Equals(x.StoredPath, item.StoredPath, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1, store.CorruptedLineCount);
            Assert.Equal(1, store.GetItemCount());
        }
    }
}
