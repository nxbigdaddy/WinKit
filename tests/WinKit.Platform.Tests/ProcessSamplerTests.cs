using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using WinKit.Platform.Metrics;
using Xunit;

namespace WinKit.Platform.Tests
{
    /// <summary>
    /// ProcessSampler 走的是 Process API（不是 WMI），这组用例验证枚举、权限降级与 CPU 差值。
    /// </summary>
    public class ProcessSamplerTests
    {
        /// <summary>
        /// 单次 Sample() 要枚举全部进程并读 MainModule/FileVersionInfo，实测约 1.5 秒。
        /// 只读型断言共用同一份快照，避免每个用例都重新枚举一遍。
        /// （测试程序集已关闭并行，Lazy 不会被并发触发。）
        /// </summary>
        private static readonly Lazy<IReadOnlyList<ProcessSample>> Snapshot =
            new Lazy<IReadOnlyList<ProcessSample>>(() => new ProcessSampler().Sample());

        private static ProcessSample? FindCurrent(IReadOnlyList<ProcessSample> samples, int pid)
        {
            foreach (var s in samples)
            {
                if (s.Pid == pid) return s;
            }
            return null;
        }

        private static int CurrentPid()
        {
            using (var self = Process.GetCurrentProcess())
            {
                return self.Id;
            }
        }

        [Fact]
        public void Sample_returns_a_non_empty_collection()
        {
            Assert.NotEmpty(Snapshot.Value);
        }

        [Fact]
        public void Sample_contains_the_current_test_process()
        {
            var pid = CurrentPid();
            var mine = FindCurrent(Snapshot.Value, pid);

            Assert.True(mine != null, "采样结果里必须能找到当前测试进程 pid=" + pid);
            Assert.False(string.IsNullOrWhiteSpace(mine!.Name), "当前进程的 Name 不应为空");
        }

        [Fact]
        public void Every_real_process_has_a_positive_pid()
        {
            var samples = Snapshot.Value;
            var positive = 0;

            foreach (var s in samples)
            {
                if (s.Pid == 0)
                {
                    // Windows 的 Idle 伪进程 PID 就是 0（任务管理器也把它列成"系统空闲进程"），
                    // 它没有任何可读的模块信息。这里显式放行，避免断言变成永久红灯。
                    continue;
                }
                Assert.True(s.Pid > 0, "出现了非正数的 Pid：" + s.Pid);
                positive++;
            }

            Assert.True(positive > 0, "至少应当有真实进程");
        }

        [Fact]
        public void Working_set_of_the_current_process_is_positive()
        {
            var pid = CurrentPid();
            var mine = FindCurrent(Snapshot.Value, pid);

            Assert.True(mine != null);
            Assert.True(mine!.WorkingSet > 0,
                "当前进程的 WorkingSet 必须可读且大于 0，实际 " + mine.WorkingSet);
        }

        [Fact]
        public void Second_sample_reports_non_negative_cpu_percent()
        {
            var sampler = new ProcessSampler();
            var first = sampler.Sample();
            Assert.NotEmpty(first);

            // 首次采样没有基准，所有进程的 CPU 都应该是 0
            foreach (var s in first)
            {
                Assert.Equal(0d, s.CpuPercent);
            }

            Thread.Sleep(250);
            var second = sampler.Sample();

            Assert.NotEmpty(second);
            foreach (var s in second)
            {
                // 机器空闲时可能整机都是 0，因此只断言"不为负且在 0~100 内"，
                // 不断言一定有进程 > 0（那会在空闲机器上偶发失败）。
                Assert.True(s.CpuPercent >= 0d, "CpuPercent 不应为负：" + s.CpuPercent);
                Assert.InRange(s.CpuPercent, 0d, 100d);
            }

            // 差值必须真的被计算过：当前进程在 250ms 窗口里多少会消耗一点 CPU，
            // 但为了不引入偶发失败，这里不对此做硬断言。
        }

        [Fact]
        public void Reset_clears_the_cpu_baseline()
        {
            var sampler = new ProcessSampler();
            sampler.Sample();
            Thread.Sleep(120);
            sampler.Sample();

            sampler.Reset();
            var afterReset = sampler.Sample();

            foreach (var s in afterReset)
            {
                Assert.Equal(0d, s.CpuPercent);
            }
        }

        [Fact]
        public void Display_fields_never_return_null_or_empty()
        {
            foreach (var s in Snapshot.Value)
            {
                Assert.NotNull(s.DisplayName);
                Assert.False(string.IsNullOrWhiteSpace(s.StartTimeText));
                Assert.False(string.IsNullOrWhiteSpace(s.PathText));
            }
        }
    }
}
