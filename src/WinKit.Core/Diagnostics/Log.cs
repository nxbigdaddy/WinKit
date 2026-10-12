using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace WinKit.Core.Diagnostics
{
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3,
    }

    public sealed class LogEntry
    {
        public LogEntry(DateTime timestamp, LogLevel level, string message, string? exception)
        {
            Timestamp = timestamp;
            Level = level;
            Message = message;
            Exception = exception;
        }

        public DateTime Timestamp { get; }
        public LogLevel Level { get; }
        public string Message { get; }
        public string? Exception { get; }

        public string Formatted
        {
            get
            {
                var sb = new StringBuilder();
                sb.Append(Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.Append(" [").Append(Level.ToString().ToUpperInvariant().PadRight(5)).Append("] ");
                sb.Append(Message);
                if (!string.IsNullOrEmpty(Exception))
                {
                    sb.AppendLine();
                    sb.Append(Exception);
                }
                return sb.ToString();
            }
        }

        public override string ToString() => Formatted;
    }

    /// <summary>
    /// 进程内日志。写文件 + 内存环形缓冲 + 事件通知（UI 可订阅）。
    /// 不联网、不上报：所有日志只落在本地文件。
    /// </summary>
    public static class Log
    {
        private const int RingCapacity = 500;
        private static readonly object Gate = new object();
        private static readonly Queue<LogEntry> Ring = new Queue<LogEntry>();
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static LogLevel _minimum = LogLevel.Info;
        private static string? _filePath;

        public static event Action<LogEntry>? EntryWritten;

        public static string? CurrentFile
        {
            get { lock (Gate) { return _filePath; } }
        }

        public static LogLevel MinimumLevel
        {
            get { lock (Gate) { return _minimum; } }
            set { lock (Gate) { _minimum = value; } }
        }

        /// <summary>初始化文件日志。目录不存在会自动创建；重复调用会切换文件。</summary>
        public static void Initialize(string logDirectory, LogLevel minimum = LogLevel.Info, string fileNamePrefix = "winkit")
        {
            if (string.IsNullOrWhiteSpace(logDirectory)) throw new ArgumentException("日志目录不能为空", nameof(logDirectory));
            Directory.CreateDirectory(logDirectory);
            var file = Path.Combine(logDirectory, fileNamePrefix + "-" + DateTime.Now.ToString("yyyyMMdd") + ".log");

            lock (Gate)
            {
                _minimum = minimum;
                _filePath = file;
                // 重新初始化视为一次新的日志会话，清空环形缓冲，避免界面显示上一次会话的残留
                Ring.Clear();
            }
            Write(LogLevel.Info, "日志系统已启动 → " + file, null);
        }

        public static void Shutdown()
        {
            lock (Gate)
            {
                _filePath = null;
            }
        }

        public static IReadOnlyList<LogEntry> Recent
        {
            get { lock (Gate) { return Ring.ToArray(); } }
        }

        public static void Debug(string message) => Write(LogLevel.Debug, message, null);
        public static void Info(string message) => Write(LogLevel.Info, message, null);
        public static void Warn(string message, Exception? ex = null) => Write(LogLevel.Warn, message, ex);
        public static void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);

        private static void Write(LogLevel level, string message, Exception? ex)
        {
            LogEntry entry;
            string? file;
            lock (Gate)
            {
                if (level < _minimum) return;
                entry = new LogEntry(DateTime.Now, level, message ?? string.Empty, ex?.ToString());
                Ring.Enqueue(entry);
                while (Ring.Count > RingCapacity) Ring.Dequeue();
                file = _filePath;
            }

            if (file != null)
            {
                try
                {
                    File.AppendAllText(file, entry.Formatted + Environment.NewLine, Utf8NoBom);
                }
                catch (Exception)
                {
                    // 日志写盘失败绝不能影响主流程（磁盘满/文件被占用等）
                }
            }

            var handler = EntryWritten;
            if (handler != null)
            {
                try
                {
                    handler(entry);
                }
                catch (Exception)
                {
                    // 订阅者异常同样不能反噬日志系统
                }
            }
        }
    }
}
