using System;
using System.IO;

namespace WinKit.Core.Configuration
{
    /// <summary>数据目录的解析结果。</summary>
    public sealed class DataLocation
    {
        public DataLocation(string directory, bool isPortable)
        {
            Directory = directory ?? throw new ArgumentNullException(nameof(directory));
            IsPortable = isPortable;
        }

        public string Directory { get; }

        /// <summary>true = 配置跟随程序目录（便携模式）；false = 回退到 %LOCALAPPDATA%。</summary>
        public bool IsPortable { get; }

        public string ConfigFile => Path.Combine(Directory, "config.json");

        public string JournalFile => Path.Combine(Directory, "journal.jsonl");

        public string LogDirectory => Path.Combine(Directory, "logs");

        public string BackupDirectory => Path.Combine(Directory, "backups");

        public string UndoDirectory => Path.Combine(Directory, "undo");

        public string CacheDirectory => Path.Combine(Directory, "cache");

        public void EnsureCreated()
        {
            System.IO.Directory.CreateDirectory(Directory);
            System.IO.Directory.CreateDirectory(LogDirectory);
            System.IO.Directory.CreateDirectory(BackupDirectory);
            System.IO.Directory.CreateDirectory(UndoDirectory);
            System.IO.Directory.CreateDirectory(CacheDirectory);
        }
    }

    /// <summary>
    /// 便携模式探测：程序目录可写 → 配置写程序目录；否则回退 %LOCALAPPDATA%\WinKit。
    /// 解析逻辑是纯函数，便于单元测试。
    /// </summary>
    public static class PortablePaths
    {
        public const string DataFolderName = "WinKit-data";

        /// <summary>测试与自动化可用该环境变量强制指定数据目录。</summary>
        public const string DataDirEnvironmentVariable = "WINKIT_DATA_DIR";

        public static DataLocation Resolve(string programDirectory, string fallbackRoot)
        {
            if (string.IsNullOrWhiteSpace(programDirectory)) throw new ArgumentException("程序目录不能为空", nameof(programDirectory));
            if (string.IsNullOrWhiteSpace(fallbackRoot)) throw new ArgumentException("回退目录不能为空", nameof(fallbackRoot));

            var overrideDir = Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overrideDir))
            {
                return new DataLocation(Path.GetFullPath(overrideDir!), false);
            }

            var portableDir = Path.Combine(programDirectory, DataFolderName);
            if (IsWritable(portableDir))
            {
                return new DataLocation(portableDir, true);
            }

            return new DataLocation(Path.Combine(fallbackRoot, "WinKit"), false);
        }

        /// <summary>通过实际写入探测目录可写性（U 盘/Program Files 只读场景的判定依据）。</summary>
        public static bool IsWritable(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var probe = Path.Combine(directory, ".winkit-write-probe-" + Guid.NewGuid().ToString("N"));
                using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.WriteByte(0x57);
                }
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
