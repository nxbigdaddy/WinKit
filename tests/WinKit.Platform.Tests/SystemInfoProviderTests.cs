using System;
using WinKit.Platform.Wmi;
using Xunit;

namespace WinKit.Platform.Tests
{
    /// <summary>
    /// 系统/硬件信息（WMI + 注册表）。WMI 是冷路径，本类接受秒级耗时。
    /// </summary>
    public class SystemInfoProviderTests
    {
        private const string EmDash = "\u2014";

        [Fact]
        public void Get_reports_os_cpu_and_memory_display_texts()
        {
            var info = new SystemInfoProvider().Get();

            Assert.False(string.IsNullOrWhiteSpace(info.OsDisplay), "OsDisplay 不应为空");
            Assert.False(string.IsNullOrWhiteSpace(info.CpuDisplay), "CpuDisplay 不应为空");
            Assert.False(string.IsNullOrWhiteSpace(info.MemoryDisplay), "MemoryDisplay 不应为空");

            // Windows 机器上这些数据必然存在，出现占位符说明 WMI 读取整体失败了
            Assert.DoesNotContain(EmDash, info.OsDisplay);
            Assert.DoesNotContain(EmDash, info.CpuDisplay);
            Assert.DoesNotContain(EmDash, info.MemoryDisplay);
        }

        [Fact]
        public void Get_reports_a_positive_physical_memory_total()
        {
            var info = new SystemInfoProvider().Get();

            Assert.True(info.TotalPhysicalMemoryBytes > 0, "总物理内存应大于 0");
        }

        [Fact]
        public void Get_reports_machine_and_user_names()
        {
            var info = new SystemInfoProvider().Get();

            Assert.False(string.IsNullOrWhiteSpace(info.MachineName));
            Assert.False(string.IsNullOrWhiteSpace(info.UserName));
        }

        [Fact]
        public void Cpu_metadata_is_read_from_wmi()
        {
            var info = new SystemInfoProvider().Get();

            Assert.True(info.CpuCores > 0, "WMI 未返回 CPU 核心数");
            Assert.True(info.CpuLogicalProcessors > 0, "WMI 未返回 CPU 逻辑处理器数");
            Assert.True(info.CpuLogicalProcessors >= info.CpuCores);
        }

        [Fact]
        public void Second_Get_returns_the_cached_instance()
        {
            var provider = new SystemInfoProvider();

            var first = provider.Get();
            var second = provider.Get();

            // 缓存命中必须是同一个实例 —— 比用 Stopwatch 断言"更快"稳定得多
            Assert.Same(first, second);
        }

        [Fact]
        public void ForceRefresh_loads_a_new_instance()
        {
            var provider = new SystemInfoProvider();

            var first = provider.Get();
            var refreshed = provider.Get(forceRefresh: true);

            Assert.NotNull(refreshed);
            Assert.NotSame(first, refreshed);
            Assert.True(refreshed.TotalPhysicalMemoryBytes > 0);
            // 强制刷新后新结果也应进入缓存
            Assert.Same(refreshed, provider.Get());
        }

        [Fact]
        public void An_expired_ttl_causes_a_reload()
        {
            // TTL 设为 0，任何一次读取都已过期
            var provider = new SystemInfoProvider(TimeSpan.Zero);

            var first = provider.Get();
            var second = provider.Get();

            Assert.NotSame(first, second);
        }

        [Fact]
        public void Os_build_is_enriched_from_the_registry_ubr_value()
        {
            var info = new SystemInfoProvider().Get();

            Assert.False(string.IsNullOrWhiteSpace(info.OsBuild), "OsBuild 不应为空");
            Assert.DoesNotContain("—", info.OsBuild);
            // Win10+ 的注册表 UBR 会让内部版本形如 19045.4046；
            // 这里只断言"形如数字[.数字]"，不强制要求读到 UBR（32 位视图下该值可能缺失）
            var head = info.OsBuild.Split('.')[0];
            int buildNumber;
            Assert.True(int.TryParse(head, out buildNumber) && buildNumber > 0,
                "OsBuild 应以正整数内部版本开头，实际 " + info.OsBuild);
        }

        [Fact]
        public void Gpu_list_never_contains_blank_entries()
        {
            var info = new SystemInfoProvider().Get();

            foreach (var gpu in info.Gpus)
            {
                Assert.False(string.IsNullOrWhiteSpace(gpu));
            }
            Assert.False(string.IsNullOrWhiteSpace(info.GpuDisplay));
        }
    }

    /// <summary>注册表读取小工具（只读）。</summary>
    public class RegistryReaderTests
    {
        [Fact]
        public void GetString_reads_a_well_known_value()
        {
            var product = RegistryReader.GetString(
                Microsoft.Win32.RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                "ProductName");

            Assert.False(string.IsNullOrWhiteSpace(product));
        }

        [Fact]
        public void GetInt_reads_the_ubr_dword()
        {
            var ubr = RegistryReader.GetInt(
                Microsoft.Win32.RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                "UBR");

            Assert.True(ubr.HasValue, "UBR 应可读");
            Assert.True(ubr!.Value >= 0);
        }

        [Fact]
        public void Missing_key_or_value_returns_null_instead_of_throwing()
        {
            Assert.Null(RegistryReader.GetValue(
                Microsoft.Win32.RegistryHive.LocalMachine,
                @"SOFTWARE\WinKit\NoSuchKeyForTest",
                "Nope"));
            Assert.Null(RegistryReader.GetString(
                Microsoft.Win32.RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                "NoSuchValueForTest"));
            Assert.Null(RegistryReader.GetInt(
                Microsoft.Win32.RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                "NoSuchValueForTest"));
        }
    }
}
