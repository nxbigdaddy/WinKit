using System;
using System.IO;
using WinKit.Platform.Security;
using Xunit;

namespace WinKit.Platform.Tests
{
    /// <summary>
    /// 提权支持的可读属性。
    /// 明确不调用 RunElevatedTask —— 它会弹 UAC 对话框。
    /// </summary>
    public class ElevationTests
    {
        [Fact]
        public void IsElevated_does_not_throw()
        {
            // 结果取决于运行环境（标准用户 / 管理员），这里只要求它能安全返回一个布尔值
            var elevated = Elevation.IsElevated;
            Assert.IsType<bool>(elevated);
        }

        [Fact]
        public void CurrentUser_is_not_empty()
        {
            var user = Elevation.CurrentUser;

            Assert.False(string.IsNullOrWhiteSpace(user));
        }

        [Fact]
        public void ExecutablePath_points_at_an_existing_file()
        {
            var path = Elevation.ExecutablePath;

            // 在测试宿主里这通常是 testhost.exe，这是正常的
            Assert.False(string.IsNullOrWhiteSpace(path), "ExecutablePath 不应为空");
            Assert.True(File.Exists(path), "ExecutablePath 应指向真实存在的文件：" + path);
            Assert.EndsWith(".exe", path, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ExecutablePath_is_stable_across_calls()
        {
            Assert.Equal(Elevation.ExecutablePath, Elevation.ExecutablePath);
        }

        [Fact]
        public void WriteResult_round_trips_through_JsonSerialization()
        {
            // RunElevatedTask 无法在测试里调用，但结果文件的写入侧是纯本地逻辑，可以验证
            using var tmp = new TempDir();
            var resultFile = tmp.File("elev-result.json");

            Elevation.WriteResult(resultFile, "token-123", true, "done", null);

            Assert.True(File.Exists(resultFile));
            var text = File.ReadAllText(resultFile);
            Assert.Contains("token-123", text);
            Assert.Contains("done", text);
        }

        [Fact]
        public void WriteResult_creates_the_target_directory()
        {
            using var tmp = new TempDir();
            var resultFile = Path.Combine(tmp.Path, "nested", "deep", "elev-result.json");

            Elevation.WriteResult(resultFile, "t", false, null, "boom");

            Assert.True(File.Exists(resultFile));
            Assert.Contains("boom", File.ReadAllText(resultFile));
        }
    }
}
