using System;
using WinKit.Core.Rules;
using Xunit;

namespace WinKit.Core.Tests
{
    public class PathGuardTests
    {
        [Fact]
        public void Normalize_resolves_parent_directory_segments()
        {
            Assert.Equal(@"C:\Users", PathGuard.Normalize(@"C:\Windows\..\Users"));
        }

        [Fact]
        public void Normalize_uppercases_drive_letter_and_strips_trailing_separator()
        {
            Assert.Equal(@"C:\Temp", PathGuard.Normalize(@"c:\Temp\"));
        }

        [Fact]
        public void Normalize_keeps_drive_root_intact()
        {
            Assert.Equal(@"C:\", PathGuard.Normalize(@"C:\"));
        }

        [Fact]
        public void Normalize_returns_empty_for_blank_input()
        {
            Assert.Equal(string.Empty, PathGuard.Normalize("   "));
        }

        [Fact]
        public void IsUnder_does_not_confuse_sibling_with_similar_prefix()
        {
            // 这是前缀比较最容易出错的地方：C:\WindowsApps 不属于 C:\Windows
            Assert.False(PathGuard.IsUnder(@"C:\WindowsApps\app.exe", @"C:\Windows"));
        }

        [Fact]
        public void IsUnder_matches_the_directory_itself_and_its_children()
        {
            Assert.True(PathGuard.IsUnder(@"C:\Windows", @"C:\Windows"));
            Assert.True(PathGuard.IsUnder(@"C:\Windows\System32\kernel32.dll", @"C:\Windows"));
        }

        [Fact]
        public void Evaluate_protects_system_directory()
        {
            var guard = new PathGuard().Protect(@"C:\Windows", "Windows 系统目录");

            var result = guard.Evaluate(@"C:\Windows\System32\ntoskrnl.exe");

            Assert.False(result.IsAllowed);
            Assert.Equal("Windows 系统目录", result.Reason);
        }

        [Fact]
        public void Evaluate_allows_user_temp_directory()
        {
            var guard = new PathGuard().Protect(@"C:\Windows", "Windows 系统目录");

            var result = guard.Evaluate(@"C:\Users\tester\AppData\Local\Temp\a.tmp");

            Assert.True(result.IsAllowed);
            Assert.Null(result.Reason);
        }

        [Fact]
        public void Allow_overrides_protection()
        {
            var guard = new PathGuard()
                .Protect(@"C:\Windows", "Windows 系统目录")
                .Allow(@"C:\Windows\Temp");

            Assert.True(guard.Evaluate(@"C:\Windows\Temp\a.tmp").IsAllowed);
            Assert.False(guard.Evaluate(@"C:\Windows\System32\a.dll").IsAllowed);
        }

        [Fact]
        public void Evaluate_is_case_insensitive_and_traversal_safe()
        {
            var guard = new PathGuard().Protect(@"C:\Windows", "Windows 系统目录");

            Assert.False(guard.Evaluate(@"c:\WINDOWS\system32\a.dll").IsAllowed);
            // 通过 .. 从受保护目录绕出去，规范化后应判定为允许
            Assert.True(guard.Evaluate(@"C:\Windows\..\Temp\a.tmp").IsAllowed);
        }

        [Fact]
        public void CreateDefault_protects_real_windows_directory()
        {
            var guard = PathGuard.CreateDefault();
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            Assert.True(guard.ProtectedCount >= 3);
            Assert.False(guard.Evaluate(System.IO.Path.Combine(windows, "System32", "kernel32.dll")).IsAllowed);
        }
    }
}
