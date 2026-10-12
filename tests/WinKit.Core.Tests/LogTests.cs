using System;
using System.Collections.Generic;
using System.IO;
using WinKit.Core.Diagnostics;
using Xunit;

namespace WinKit.Core.Tests
{
    public class LogTests
    {
        private static string ReadAllText(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }

        [Fact]
        public void Initialize_creates_file_and_writes_entries()
        {
            using var tmp = new TempDir();
            Log.Initialize(tmp.Dir("logs"), LogLevel.Info);

            Log.Info("单元测试消息");
            Log.Warn("警告消息");

            var file = Log.CurrentFile;
            Assert.NotNull(file);
            Assert.True(File.Exists(file));

            var text = ReadAllText(file!);
            Assert.Contains("日志系统已启动", text);
            Assert.Contains("单元测试消息", text);
            Assert.Contains("[WARN ]", text);

            Log.Shutdown();
        }

        [Fact]
        public void MinimumLevel_filters_lower_levels_from_file_and_ring()
        {
            using var tmp = new TempDir();
            Log.Initialize(tmp.Dir("logs"), LogLevel.Warn);

            var before = Log.Recent.Count;
            Log.Debug("调试消息应被过滤");
            Log.Info("信息消息应被过滤");

            Assert.Equal(before, Log.Recent.Count);
            // 全部被过滤时日志文件可能尚未创建，因此这里要容忍文件不存在
            var file = Log.CurrentFile!;
            var content = File.Exists(file) ? ReadAllText(file) : string.Empty;
            Assert.DoesNotContain("应被过滤", content);

            Log.Shutdown();
        }

        [Fact]
        public void EntryWritten_event_is_raised_with_entry_payload()
        {
            using var tmp = new TempDir();
            Log.Initialize(tmp.Dir("logs"), LogLevel.Debug);

            LogEntry? captured = null;
            void Handler(LogEntry e) => captured = e;
            Log.EntryWritten += Handler;
            try
            {
                Log.Error("出错了");
            }
            finally
            {
                Log.EntryWritten -= Handler;
                Log.Shutdown();
            }

            Assert.NotNull(captured);
            Assert.Equal(LogLevel.Error, captured!.Level);
            Assert.Equal("出错了", captured.Message);
        }

        [Fact]
        public void Exception_details_are_recorded()
        {
            using var tmp = new TempDir();
            Log.Initialize(tmp.Dir("logs"), LogLevel.Info);
            var file = Log.CurrentFile!;

            Log.Error("操作失败", new InvalidOperationException("设备未就绪"));

            var text = ReadAllText(file);
            Assert.Contains("操作失败", text);
            Assert.Contains("设备未就绪", text);

            Log.Shutdown();
        }

        [Fact]
        public void Recent_ring_keeps_most_recent_entries_and_stays_bounded()
        {
            using var tmp = new TempDir();
            Log.Initialize(tmp.Dir("logs"), LogLevel.Info);

            for (int i = 0; i < 600; i++) Log.Info("批量消息 " + i);

            var recent = Log.Recent;
            Assert.True(recent.Count <= 500);
            Assert.Equal("批量消息 599", recent[recent.Count - 1].Message);

            Log.Shutdown();
        }

        [Fact]
        public void Subscriber_exception_does_not_break_logging()
        {
            using var tmp = new TempDir();
            Log.Initialize(tmp.Dir("logs"), LogLevel.Info);
            var file = Log.CurrentFile!;

            void BadHandler(LogEntry e) => throw new InvalidOperationException("订阅者炸了");
            Log.EntryWritten += BadHandler;
            try
            {
                Log.Info("订阅者异常后仍应写入");
            }
            finally
            {
                Log.EntryWritten -= BadHandler;
                Log.Shutdown();
            }

            Assert.Contains("订阅者异常后仍应写入", ReadAllText(file));
        }
    }
}
