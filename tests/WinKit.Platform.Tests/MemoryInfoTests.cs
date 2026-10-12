using WinKit.Platform.Metrics;
using Xunit;

namespace WinKit.Platform.Tests
{
    /// <summary>GlobalMemoryStatusEx 的真实调用与不变量校验。</summary>
    public class MemoryInfoTests
    {
        [Fact]
        public void Query_reports_physical_memory()
        {
            var status = MemoryInfo.Query();

            Assert.True(status.TotalBytes > 0, "TotalBytes 应大于 0（GlobalMemoryStatusEx 未生效？）");
            Assert.True(status.AvailableBytes <= status.TotalBytes, "可用内存不应超过总内存");
        }

        [Fact]
        public void Used_percent_is_within_zero_to_hundred()
        {
            var status = MemoryInfo.Query();

            Assert.InRange(status.UsedPercent, 0d, 100d);
        }

        [Fact]
        public void Used_plus_available_equals_total()
        {
            var status = MemoryInfo.Query();

            Assert.Equal(status.TotalBytes, status.UsedBytes + status.AvailableBytes);
        }

        [Fact]
        public void Load_percent_reported_by_windows_is_a_percentage()
        {
            var status = MemoryInfo.Query();

            Assert.InRange(status.LoadPercent, 0u, 100u);
        }
    }
}
