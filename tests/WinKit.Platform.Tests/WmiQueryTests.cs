using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using WinKit.Platform.Wmi;
using Xunit;

namespace WinKit.Platform.Tests
{
    /// <summary>
    /// WMI 真实查询。关键断言是"失败必须返回空集合而不是抛异常" —— 这是容错设计的核心。
    /// WMI 单次 50~300ms，本类允许秒级耗时。
    /// </summary>
    public class WmiQueryTests
    {
        [Fact]
        public void Query_operating_system_returns_one_row_with_a_caption()
        {
            // 回归保护：曾经 Query 有一个 (wql, scope, params properties) 重载，
            // 导致这里的 "Caption" 被重载决议当成命名空间，所有 WMI 查询静默返回空集合。
            // 只要这个用例还是绿的，就说明属性名没有被误当成 scope。
            var rows = WmiQuery.Query("SELECT Caption FROM Win32_OperatingSystem", "Caption");

            Assert.Single(rows);
            var caption = WmiQuery.AsString(rows[0], "Caption");
            Assert.False(string.IsNullOrWhiteSpace(caption), "Win32_OperatingSystem.Caption 不应为空");
        }

        [Fact]
        public void Query_with_several_properties_returns_all_of_them()
        {
            var rows = WmiQuery.Query("SELECT Caption,Version,BuildNumber FROM Win32_OperatingSystem",
                "Caption", "Version", "BuildNumber");

            Assert.Single(rows);
            Assert.False(string.IsNullOrWhiteSpace(WmiQuery.AsString(rows[0], "Caption")));
            Assert.False(string.IsNullOrWhiteSpace(WmiQuery.AsString(rows[0], "Version")));
            Assert.False(string.IsNullOrWhiteSpace(WmiQuery.AsString(rows[0], "BuildNumber")));
        }

        [Fact]
        public void QueryInScope_accepts_both_a_bare_namespace_and_a_full_path()
        {
            var bare = WmiQuery.QueryInScope("SELECT Caption FROM Win32_OperatingSystem", WmiQuery.CimV2, "Caption");
            var full = WmiQuery.QueryInScope("SELECT Caption FROM Win32_OperatingSystem", @"\\.\" + WmiQuery.CimV2, "Caption");

            Assert.Single(bare);
            Assert.Single(full);
            Assert.Equal(WmiQuery.AsString(bare[0], "Caption"), WmiQuery.AsString(full[0], "Caption"));
        }

        [Fact]
        public void QueryInScope_with_an_unknown_namespace_returns_empty_instead_of_throwing()
        {
            var rows = WmiQuery.QueryInScope("SELECT Caption FROM Win32_OperatingSystem",
                @"root\no_such_namespace_for_test", "Caption");

            Assert.Empty(rows);
        }

        [Fact]
        public void Query_processor_returns_a_name()
        {
            var rows = WmiQuery.Query("SELECT Name FROM Win32_Processor", "Name");

            Assert.NotEmpty(rows);
            Assert.False(string.IsNullOrWhiteSpace(WmiQuery.AsString(rows[0], "Name")));
        }

        [Fact]
        public void Query_unknown_class_returns_empty_instead_of_throwing()
        {
            var rows = WmiQuery.Query("SELECT * FROM Win32_NoSuchClassForTest", "Name");

            Assert.Empty(rows);
        }

        [Fact]
        public void Query_with_invalid_wql_returns_empty_instead_of_throwing()
        {
            var rows = WmiQuery.Query("THIS IS NOT WQL AT ALL", "Name");

            Assert.Empty(rows);
        }

        [Fact]
        public void Query_with_blank_wql_returns_empty()
        {
            Assert.Empty(WmiQuery.Query(string.Empty, "Name"));
            Assert.Empty(WmiQuery.Query("   ", "Name"));
        }

        [Fact]
        public void Query_with_unknown_property_yields_null_value_but_keeps_the_row()
        {
            var rows = WmiQuery.Query("SELECT Caption FROM Win32_OperatingSystem", "NoSuchPropertyHere");

            Assert.Single(rows);
            Assert.True(rows[0].ContainsKey("NoSuchPropertyHere"));
            Assert.Null(WmiQuery.AsString(rows[0], "NoSuchPropertyHere"));
            Assert.Equal(0UL, WmiQuery.AsUInt64(rows[0], "NoSuchPropertyHere"));
            Assert.Equal(0, WmiQuery.AsInt32(rows[0], "NoSuchPropertyHere"));
        }

        [Fact]
        public void GetProcessCommandLine_for_the_current_process_mentions_its_executable()
        {
            int pid;
            string? exeName;
            using (var self = Process.GetCurrentProcess())
            {
                pid = self.Id;
                try { exeName = self.MainModule?.FileName; }
                catch (Exception) { exeName = null; }
            }

            string? commandLine = null;
            var threw = false;
            try
            {
                commandLine = WmiQuery.GetProcessCommandLine(pid);
            }
            catch (Exception)
            {
                threw = true;
            }

            Assert.False(threw, "GetProcessCommandLine 不应抛异常");

            // 某些环境（WMI 被禁用、权限受限）下会拿不到命令行，
            // 这时只断言"不抛异常"就够，避免制造 flaky 测试。
            if (commandLine == null) return;

            Assert.False(string.IsNullOrWhiteSpace(commandLine));
            if (!string.IsNullOrEmpty(exeName))
            {
                var name = Path.GetFileName(exeName!);
                Assert.True(
                    commandLine!.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0,
                    "命令行里应包含测试进程可执行文件名 " + name + "，实际：" + commandLine);
            }
        }

        [Fact]
        public void GetProcessCommandLine_for_a_bogus_pid_returns_null_without_throwing()
        {
            // 0 号 pid 在 Win32_Process 里查不到任何行
            var result = WmiQuery.GetProcessCommandLine(0);

            Assert.Null(result);
        }

        [Fact]
        public void AsString_trims_and_treats_whitespace_as_null()
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                { "a", "  hello  " },
                { "b", "   " },
                { "c", null },
                { "d", 42 },
            };

            Assert.Equal("hello", WmiQuery.AsString(row, "a"));
            Assert.Null(WmiQuery.AsString(row, "b"));
            Assert.Null(WmiQuery.AsString(row, "c"));
            Assert.Equal("42", WmiQuery.AsString(row, "d"));
            Assert.Null(WmiQuery.AsString(row, "missing"));
        }

        [Fact]
        public void AsUInt64_and_AsInt32_handle_native_wmi_types()
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                { "ul", 17179869184UL },
                { "l", 4096L },
                { "negative", -5L },
                { "i", 8 },
                { "u", 16u },
                { "s", "2048" },
                { "junk", "not-a-number" },
                { "nil", null },
            };

            Assert.Equal(17179869184UL, WmiQuery.AsUInt64(row, "ul"));
            Assert.Equal(4096UL, WmiQuery.AsUInt64(row, "l"));
            Assert.Equal(0UL, WmiQuery.AsUInt64(row, "negative"));
            Assert.Equal(2048UL, WmiQuery.AsUInt64(row, "s"));
            Assert.Equal(0UL, WmiQuery.AsUInt64(row, "junk"));
            Assert.Equal(0UL, WmiQuery.AsUInt64(row, "nil"));

            Assert.Equal(8, WmiQuery.AsInt32(row, "i"));
            Assert.Equal(16, WmiQuery.AsInt32(row, "u"));
            Assert.Equal(0, WmiQuery.AsInt32(row, "junk"));
            Assert.Equal(0, WmiQuery.AsInt32(row, "nil"));
        }
    }
}
