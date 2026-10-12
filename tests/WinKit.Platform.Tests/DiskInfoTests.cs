using System;
using System.IO;
using WinKit.Platform.Metrics;
using Xunit;

namespace WinKit.Platform.Tests
{
    /// <summary>DriveInfo 枚举的真实结果校验（系统盘必须能被读到）。</summary>
    public class DiskInfoTests
    {
        [Fact]
        public void Query_contains_the_system_drive()
        {
            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
            Assert.False(string.IsNullOrEmpty(systemRoot), "拿不到系统盘根路径，环境异常");

            var volumes = DiskInfo.Query();

            Assert.NotEmpty(volumes);
            var found = false;
            foreach (var v in volumes)
            {
                if (string.Equals(v.Name, systemRoot, StringComparison.OrdinalIgnoreCase)) found = true;
            }
            Assert.True(found, "结果里应包含系统盘 " + systemRoot);
        }

        [Fact]
        public void Every_volume_has_consistent_byte_counters()
        {
            var volumes = DiskInfo.Query();

            Assert.NotEmpty(volumes);
            foreach (var v in volumes)
            {
                Assert.True(v.TotalBytes >= v.FreeBytes,
                    string.Format("{0}: TotalBytes({1}) 应 >= FreeBytes({2})", v.Name, v.TotalBytes, v.FreeBytes));
                Assert.InRange(v.UsedPercent, 0d, 100d);
            }
        }

        [Fact]
        public void Volumes_carry_a_name_and_kind()
        {
            foreach (var v in DiskInfo.Query())
            {
                Assert.False(string.IsNullOrWhiteSpace(v.Name));
                Assert.False(string.IsNullOrWhiteSpace(v.Kind));
                // Display 不应因为空卷标而出现悬空的括号
                Assert.DoesNotContain("()", v.Display);
            }
        }
    }
}
