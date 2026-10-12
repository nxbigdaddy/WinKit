using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using WinKit.Platform.FileSystem;
using Xunit;

namespace WinKit.Platform.Tests
{
    /// <summary>
    /// 文件操作。全部在临时目录内进行；绝不调用 MoveFileToRecycleBin（会真删到回收站）。
    /// </summary>
    public class FileOperationsTests
    {
        /// <summary>用 .NET 自带实现算一遍作为交叉验证，避免"用被测实现验证被测实现"。</summary>
        private static string ReferenceSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                var hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        [Fact]
        public void ComputeSha256_matches_the_standard_vector_for_abc()
        {
            using var tmp = new TempDir();
            var file = tmp.File("abc.txt");
            File.WriteAllBytes(file, Encoding.ASCII.GetBytes("abc"));

            var hash = FileOperations.ComputeSha256(file, CancellationToken.None);

            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
        }

        [Fact]
        public void ComputeSha256_is_lowercase_hex_of_the_expected_length()
        {
            using var tmp = new TempDir();
            var file = tmp.File("empty.bin");
            File.WriteAllBytes(file, new byte[0]);

            var hash = FileOperations.ComputeSha256(file, CancellationToken.None);

            Assert.Equal(64, hash.Length);
            Assert.Equal(ReferenceSha256(file), hash);
            // 空文件的 SHA256 是标准已知值
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hash);
        }

        [Fact]
        public void ComputeSha256_matches_the_framework_implementation_for_random_data()
        {
            using var tmp = new TempDir();
            var file = tmp.File("random.bin");
            var data = new byte[3 * 1024 * 1024 + 7];
            new Random(20240517).NextBytes(data);
            File.WriteAllBytes(file, data);

            var hash = FileOperations.ComputeSha256(file, CancellationToken.None);

            Assert.Equal(ReferenceSha256(file), hash);
        }

        [Fact]
        public void ComputeSha256_throws_when_the_token_is_already_cancelled()
        {
            using var tmp = new TempDir();
            var file = tmp.File("cancel.bin");
            File.WriteAllBytes(file, new byte[4096]);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(
                () => FileOperations.ComputeSha256(file, cts.Token));
        }

        [Fact]
        public void ComputeSha256_reports_progress_up_to_about_one()
        {
            using var tmp = new TempDir();
            var file = tmp.File("progress.bin");
            var data = new byte[2 * 1024 * 1024];
            new Random(7).NextBytes(data);
            File.WriteAllBytes(file, data);

            var progress = new RecordingProgress();
            var hash = FileOperations.ComputeSha256(file, CancellationToken.None, progress);

            Assert.Equal(ReferenceSha256(file), hash);
            Assert.NotEmpty(progress.Values);
            Assert.True(progress.Values[progress.Values.Count - 1] > 0.99,
                "最后一次进度应接近 1.0，实际 " + progress.Values[progress.Values.Count - 1]);
            foreach (var v in progress.Values)
            {
                Assert.InRange(v, 0d, 1d);
            }
        }

        [Fact]
        public void ComputeSha256_throws_for_a_missing_file()
        {
            using var tmp = new TempDir();

            Assert.Throws<FileNotFoundException>(
                () => FileOperations.ComputeSha256(tmp.File("nope.bin"), CancellationToken.None));
        }

        [Fact]
        public void IsFileLocked_is_true_for_an_exclusively_opened_file()
        {
            using var tmp = new TempDir();
            var file = tmp.File("locked.bin");
            File.WriteAllBytes(file, new byte[16]);

            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.True(FileOperations.IsFileLocked(file));
            }

            // 独占句柄释放后应当恢复为未占用
            Assert.False(FileOperations.IsFileLocked(file));
        }

        [Fact]
        public void IsFileLocked_is_false_for_an_ordinary_file()
        {
            using var tmp = new TempDir();
            var file = tmp.File("free.bin");
            File.WriteAllBytes(file, new byte[16]);

            Assert.False(FileOperations.IsFileLocked(file));
        }

        [Fact]
        public void SafeMove_renames_within_the_same_directory()
        {
            using var tmp = new TempDir();
            var source = tmp.File("before.txt");
            var destination = tmp.File("after.txt");
            File.WriteAllText(source, "hello");

            FileOperations.SafeMove(source, destination);

            Assert.False(File.Exists(source));
            Assert.True(File.Exists(destination));
            Assert.Equal("hello", File.ReadAllText(destination));
        }

        [Fact]
        public void SafeMove_moves_across_directories_and_creates_the_target_directory()
        {
            using var tmp = new TempDir();
            var source = tmp.File("payload.txt");
            File.WriteAllText(source, "cross-dir");
            var destination = Path.Combine(tmp.Dir("nested"), "deeper", "payload-moved.txt");

            FileOperations.SafeMove(source, destination);

            Assert.False(File.Exists(source));
            Assert.True(File.Exists(destination));
            Assert.Equal("cross-dir", File.ReadAllText(destination));
        }

        [Fact]
        public void SafeMove_moves_a_directory()
        {
            using var tmp = new TempDir();
            var source = tmp.Dir("src-tree");
            File.WriteAllText(Path.Combine(source, "a.txt"), "a");
            Directory.CreateDirectory(Path.Combine(source, "sub"));
            File.WriteAllText(Path.Combine(source, "sub", "b.txt"), "b");
            var destination = tmp.File("dst-tree");

            FileOperations.SafeMove(source, destination);

            Assert.False(Directory.Exists(source));
            Assert.Equal("a", File.ReadAllText(Path.Combine(destination, "a.txt")));
            Assert.Equal("b", File.ReadAllText(Path.Combine(destination, "sub", "b.txt")));
        }

        [Fact]
        public void SafeMove_overwrites_only_when_asked_to()
        {
            using var tmp = new TempDir();
            var source = tmp.File("s.txt");
            var destination = tmp.File("d.txt");
            File.WriteAllText(source, "new");
            File.WriteAllText(destination, "old");

            Assert.ThrowsAny<IOException>(() => FileOperations.SafeMove(source, destination));

            FileOperations.SafeMove(source, destination, overwrite: true);

            Assert.False(File.Exists(source));
            Assert.Equal("new", File.ReadAllText(destination));
        }

        [Fact]
        public void SafeMove_rejects_empty_paths()
        {
            using var tmp = new TempDir();
            var file = tmp.File("x.txt");
            File.WriteAllText(file, "x");

            Assert.Throws<ArgumentException>(() => FileOperations.SafeMove(string.Empty, file));
            Assert.Throws<ArgumentException>(() => FileOperations.SafeMove(file, "   "));
        }

        [Fact]
        public void DeletePermanently_removes_file_and_directory()
        {
            using var tmp = new TempDir();
            var file = tmp.File("gone.txt");
            File.WriteAllText(file, "bye");
            var dir = tmp.Dir("gone-dir");
            File.WriteAllText(Path.Combine(dir, "inner.txt"), "bye");

            FileOperations.DeletePermanently(file);
            FileOperations.DeletePermanently(dir);

            Assert.False(File.Exists(file));
            Assert.False(Directory.Exists(dir));
        }
    }
}
