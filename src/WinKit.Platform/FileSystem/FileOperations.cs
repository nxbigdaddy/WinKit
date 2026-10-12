using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using WinKit.Core.Diagnostics;
// 本文件位于 WinKit.Platform.FileSystem 命名空间内，直接写 FileSystem 会被解析成命名空间
// 而不是 Microsoft.VisualBasic.FileIO.FileSystem 类型，因此用别名固定指向类型。
using VbFileSystem = Microsoft.VisualBasic.FileIO.FileSystem;
using VbRecycleOption = Microsoft.VisualBasic.FileIO.RecycleOption;
using VbUIOption = Microsoft.VisualBasic.FileIO.UIOption;

namespace WinKit.Platform.FileSystem
{
    /// <summary>
    /// 文件操作。删除一律优先走回收站（可撤销），彻底删除需要显式调用。
    /// 回收站通过 Microsoft.VisualBasic.FileIO 实现 —— 它是 .NET Framework 自带程序集，
    /// 省掉了自己 P/Invoke SHFileOperation 的一堆细节（长路径、多选、进度回调）。
    /// </summary>
    public static class FileOperations
    {
        /// <summary>移入回收站（文件）。失败抛异常，由调用方记录到操作日志。</summary>
        public static void MoveFileToRecycleBin(string path)
        {
            VbFileSystem.DeleteFile(path, VbUIOption.OnlyErrorDialogs, VbRecycleOption.SendToRecycleBin);
        }

        /// <summary>移入回收站（目录）。</summary>
        public static void MoveDirectoryToRecycleBin(string path)
        {
            VbFileSystem.DeleteDirectory(path, VbUIOption.OnlyErrorDialogs, VbRecycleOption.SendToRecycleBin);
        }

        /// <summary>彻底删除（不进回收站）。危险操作，调用前必须经过危险分级确认。</summary>
        public static void DeletePermanently(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>
        /// 移动/重命名。跨卷时 File.Move 会失败，这里退化为"复制 + 删除"以保证成功。
        /// </summary>
        public static void SafeMove(string source, string destination, bool overwrite = false)
        {
            if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("源路径不能为空", nameof(source));
            if (string.IsNullOrWhiteSpace(destination)) throw new ArgumentException("目标路径不能为空", nameof(destination));

            var targetDir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

            if (overwrite && File.Exists(destination)) File.Delete(destination);

            try
            {
                if (Directory.Exists(source)) Directory.Move(source, destination);
                else File.Move(source, destination);
            }
            catch (IOException)
            {
                // 跨卷或目标已存在：退化为复制后删除
                if (Directory.Exists(source))
                {
                    CopyDirectory(source, destination);
                    Directory.Delete(source, recursive: true);
                }
                else
                {
                    File.Copy(source, destination, overwrite);
                    File.Delete(source);
                }
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
            }
            foreach (var dir in Directory.GetDirectories(source))
            {
                CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
            }
        }

        /// <summary>
        /// 计算文件哈希。用 SHA256（net48 自带且支持硬件加速）；
        /// 分块读取并回报进度，避免大文件读取期间界面看起来卡死。
        /// </summary>
        public static string ComputeSha256(string path, CancellationToken token, IProgress<double>? progress = null)
        {
            const int BufferSize = 1024 * 1024;
            var buffer = new byte[BufferSize];

            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan))
            {
                var length = stream.Length;
                long processed = 0;
                int read;

                while ((read = stream.Read(buffer, 0, BufferSize)) > 0)
                {
                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    processed += read;
                    if (progress != null && length > 0)
                    {
                        progress.Report(processed / (double)length);
                    }
                }

                sha.TransformFinalBlock(buffer, 0, 0);
                return ToHex(sha.Hash ?? new byte[0]);
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var chars = new char[bytes.Length * 2];
            const string Hex = "0123456789abcdef";
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = Hex[bytes[i] >> 4];
                chars[i * 2 + 1] = Hex[bytes[i] & 0xF];
            }
            return new string(chars);
        }

        /// <summary>判断文件是否被其他进程占用（用于清理前跳过被锁文件）。</summary>
        public static bool IsFileLocked(string path)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("占用检测异常：" + path + " → " + ex.Message);
                return false;
            }
        }
    }
}
