using System.IO;
using WinKit.Core.Configuration;
using WinKit.Core.Diagnostics;
using Xunit;

namespace WinKit.Core.Tests
{
    public class PortablePathsTests
    {
        [Fact]
        public void Resolve_prefers_program_directory_when_writable()
        {
            using var tmp = new TempDir();
            var programDir = tmp.Dir("app");
            var fallback = tmp.Dir("appdata");

            var location = PortablePaths.Resolve(programDir, fallback);

            Assert.True(location.IsPortable);
            Assert.Equal(Path.Combine(programDir, PortablePaths.DataFolderName), location.Directory);
        }

        [Fact]
        public void Resolve_falls_back_when_program_directory_is_not_writable()
        {
            using var tmp = new TempDir();
            var fallback = tmp.Dir("appdata");
            // 用一个不存在的盘符模拟只读/不可写安装位置
            var unwritable = @"Q:\win-kit-readonly-probe\app";

            var location = PortablePaths.Resolve(unwritable, fallback);

            Assert.False(location.IsPortable);
            Assert.Equal(Path.Combine(fallback, "WinKit"), location.Directory);
        }

        [Fact]
        public void Resolve_honours_environment_override()
        {
            using var tmp = new TempDir();
            var forced = tmp.Dir("forced");
            var previous = System.Environment.GetEnvironmentVariable(PortablePaths.DataDirEnvironmentVariable);
            try
            {
                System.Environment.SetEnvironmentVariable(PortablePaths.DataDirEnvironmentVariable, forced);

                var location = PortablePaths.Resolve(tmp.Dir("app"), tmp.Dir("appdata"));

                Assert.False(location.IsPortable);
                Assert.Equal(Path.GetFullPath(forced), location.Directory);
            }
            finally
            {
                System.Environment.SetEnvironmentVariable(PortablePaths.DataDirEnvironmentVariable, previous);
            }
        }

        [Fact]
        public void IsWritable_detects_writable_and_unwritable_locations()
        {
            using var tmp = new TempDir();

            Assert.True(PortablePaths.IsWritable(tmp.Dir("ok")));
            Assert.False(PortablePaths.IsWritable(@"Q:\definitely-not-here\sub"));
        }

        [Fact]
        public void EnsureCreated_builds_the_expected_subdirectories()
        {
            using var tmp = new TempDir();
            var location = new DataLocation(tmp.Dir("data"), isPortable: true);

            location.EnsureCreated();

            Assert.True(Directory.Exists(location.LogDirectory));
            Assert.True(Directory.Exists(location.BackupDirectory));
            Assert.True(Directory.Exists(location.UndoDirectory));
            Assert.True(Directory.Exists(location.CacheDirectory));
            Assert.EndsWith("journal.jsonl", location.JournalFile);
        }

        [Fact]
        public void Resolve_rejects_blank_arguments()
        {
            Assert.Throws<System.ArgumentException>(() => PortablePaths.Resolve("", @"C:\fallback"));
            Assert.Throws<System.ArgumentException>(() => PortablePaths.Resolve(@"C:\app", "  "));
        }
    }
}
