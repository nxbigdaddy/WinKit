using System;
using System.Collections.Generic;
using WinKit.Core.Diagnostics;
using Xunit;

namespace WinKit.Core.Tests
{
    /// <summary>
    /// HealthCheck 是纯函数：输入是一台"虚构的机器"，输出是分档结论。
    /// 因此这里把重点放在**阈值边界**（恰好等于阈值时必须落到下一档）和**扣分单调性**上。
    /// </summary>
    public class HealthCheckTests
    {
        private const long Gigabyte = 1024L * 1024 * 1024;
        private const long Megabyte = 1024L * 1024;

        /// <summary>100 GB 总容量，便于把百分比写成人能读的形式。</summary>
        private const ulong Total100Gb = 100UL * 1024 * 1024 * 1024;

        /// <summary>16 GB 内存总量。</summary>
        private const ulong Memory16Gb = 16UL * 1024 * 1024 * 1024;

        /// <summary>一台"没有毛病"的机器：磁盘 50% 空闲、内存用 30%、管理员、回收站与隔离区为空。</summary>
        private static HealthInputs PerfectMachine()
        {
            return new HealthInputs
            {
                SystemDriveName = "C:\\",
                SystemDriveTotalBytes = Total100Gb,
                SystemDriveFreeBytes = Total100Gb / 2,
                MemoryTotalBytes = Memory16Gb,
                MemoryUsedBytes = (ulong)(Memory16Gb * 0.30d),
                ProcessCount = 120,
                TopCpuProcessName = "Idle",
                TopCpuProcessPercent = 3d,
                CleanupCandidateBytes = 0,
                CleanupCandidateCount = 0,
                RecycleBinBytes = 0,
                QuarantineBytes = 0,
                RunningAsAdmin = true,
                RecentFailedOperations = 0,
            };
        }

        private static HealthItem? Find(HealthReport report, string id)
        {
            foreach (var item in report.Items)
            {
                if (string.Equals(item.Id, id, StringComparison.Ordinal)) return item;
            }
            return null;
        }

        private static HealthStatus StatusOf(HealthReport report, string id)
        {
            var item = Find(report, id);
            Assert.True(item != null, "报告里应当出现 Id == " + id + " 的项");
            return item!.Status;
        }

        private static HealthInputs WithDiskFreePercent(double percent)
        {
            var inputs = PerfectMachine();
            inputs.SystemDriveFreeBytes = (ulong)(Total100Gb * percent / 100d);
            return inputs;
        }

        private static HealthInputs WithMemoryUsedPercent(double percent)
        {
            var inputs = PerfectMachine();
            inputs.MemoryUsedBytes = (ulong)(Memory16Gb * percent / 100d);
            return inputs;
        }

        // ---------------- 1. 完美机器 ----------------

        [Fact]
        public void Perfect_machine_scores_full_marks()
        {
            var report = HealthCheck.Evaluate(PerfectMachine());

            Assert.Equal(100, report.Score);
            Assert.Equal(0, report.CriticalCount);
            Assert.Equal(0, report.WarningCount);
            Assert.Equal(0, report.NoticeCount);
            Assert.Contains("状态良好", report.Verdict);
        }

        // ---------------- 2. 系统盘空间：四档 + 三个恰好等于阈值 ----------------

        [Theory]
        [InlineData(1.0, HealthStatus.Critical)]
        [InlineData(4.0, HealthStatus.Critical)]
        [InlineData(5.0, HealthStatus.Warning)]   // 恰好 5%：严格小于，所以不是 Critical
        [InlineData(9.0, HealthStatus.Warning)]
        [InlineData(10.0, HealthStatus.Notice)]   // 恰好 10%：不是 Warning
        [InlineData(15.0, HealthStatus.Notice)]
        [InlineData(20.0, HealthStatus.Ok)]       // 恰好 20%：不是 Notice
        [InlineData(25.0, HealthStatus.Ok)]
        public void Disk_free_percent_maps_to_expected_status(double freePercent, HealthStatus expected)
        {
            var report = HealthCheck.Evaluate(WithDiskFreePercent(freePercent));

            Assert.Equal(expected, StatusOf(report, "disk.free"));
        }

        [Fact]
        public void Disk_item_is_absent_when_total_size_is_unknown()
        {
            // TotalBytes == 0 表示"拿不到容量"，此时不该凭空报一条磁盘问题
            var inputs = PerfectMachine();
            inputs.SystemDriveTotalBytes = 0;
            inputs.SystemDriveFreeBytes = 0;

            Assert.Null(Find(HealthCheck.Evaluate(inputs), "disk.free"));
        }

        // ---------------- 3. 内存：三档 + 恰好等于阈值 ----------------

        [Theory]
        [InlineData(99.0, HealthStatus.Critical)]
        [InlineData(96.0, HealthStatus.Critical)]
        [InlineData(86.0, HealthStatus.Warning)]
        [InlineData(76.0, HealthStatus.Notice)]
        [InlineData(30.0, HealthStatus.Ok)]
        public void Memory_used_percent_maps_to_expected_status(double usedPercent, HealthStatus expected)
        {
            var report = HealthCheck.Evaluate(WithMemoryUsedPercent(usedPercent));

            Assert.Equal(expected, StatusOf(report, "memory.usage"));
        }

        [Fact]
        public void Memory_thresholds_are_inclusive_at_their_exact_values()
        {
            // 阈值判定用的是 >= 95 / >= 85 / >= 75。
            // 注意：用 (ulong)(总量 * 百分比 / 100) 造数据时会因为截断而落在阈值下方
            // （例如 95% 算出来是 94.999999995343387%），所以这里直接把字节数写成精确值：
            // 15.2 GiB / 16 GiB 恰好是 95%，必须判成 Critical 而不是 Warning。
            var inputs = PerfectMachine();

            inputs.MemoryUsedBytes = 16320875724UL; // 15.2 GiB，实测 94.9999999953%
            Assert.Equal(HealthStatus.Warning, StatusOf(HealthCheck.Evaluate(inputs), "memory.usage"));

            inputs.MemoryUsedBytes = 16320875725UL; // 再多一个字节，就跨过 95% 线
            Assert.Equal(HealthStatus.Critical, StatusOf(HealthCheck.Evaluate(inputs), "memory.usage"));

            inputs.MemoryUsedBytes = 16492674416UL; // 95.37%（精确的"96%"档）
            Assert.Equal(HealthStatus.Critical, StatusOf(HealthCheck.Evaluate(inputs), "memory.usage"));
        }

        // ---------------- 4. Top CPU ----------------

        [Theory]
        [InlineData(95.0, HealthStatus.Warning)]
        [InlineData(90.0, HealthStatus.Warning)]
        [InlineData(89.0, HealthStatus.Notice)]
        [InlineData(60.0, HealthStatus.Notice)]
        [InlineData(50.0, HealthStatus.Notice)]
        public void Top_cpu_percent_maps_to_expected_status(double percent, HealthStatus expected)
        {
            var inputs = PerfectMachine();
            inputs.TopCpuProcessName = "busy.exe";
            inputs.TopCpuProcessPercent = percent;

            Assert.Equal(expected, StatusOf(HealthCheck.Evaluate(inputs), "cpu.top"));
        }

        [Fact]
        public void Top_cpu_item_is_hidden_below_fifty_percent()
        {
            var inputs = PerfectMachine();
            inputs.TopCpuProcessName = "busy.exe";
            inputs.TopCpuProcessPercent = 49.9d;

            Assert.Null(Find(HealthCheck.Evaluate(inputs), "cpu.top"));
        }

        [Fact]
        public void Top_cpu_item_is_hidden_when_process_name_is_unavailable()
        {
            // 防呆：拿不到进程名时不能报"某某进程占用高"，否则界面会出现「」这种空括号
            var both = PerfectMachine();
            both.TopCpuProcessName = null;
            both.TopCpuProcessPercent = 99d;
            Assert.Null(Find(HealthCheck.Evaluate(both), "cpu.top"));

            var empty = PerfectMachine();
            empty.TopCpuProcessName = string.Empty;
            empty.TopCpuProcessPercent = 99d;
            Assert.Null(Find(HealthCheck.Evaluate(empty), "cpu.top"));
        }

        // ---------------- 5. 可清理体积 ----------------

        [Theory]
        [InlineData(10L * 1024 * 1024 * 1024, HealthStatus.Warning)] // 10 GB
        [InlineData(2L * 1024 * 1024 * 1024, HealthStatus.Warning)]  // 2 GB
        [InlineData(1L * 1024 * 1024 * 1024, HealthStatus.Warning)]  // 恰好 1 GB 也是 Warning
        [InlineData(1024L * 1024 * 1024 - 1, HealthStatus.Notice)]   // 刚好差 1 字节：Notice
        [InlineData(100L * 1024 * 1024, HealthStatus.Notice)]        // 100 MB
        public void Cleanup_candidate_bytes_map_to_expected_status(long bytes, HealthStatus expected)
        {
            var inputs = PerfectMachine();
            inputs.CleanupCandidateBytes = bytes;
            inputs.CleanupCandidateCount = 7;

            Assert.Equal(expected, StatusOf(HealthCheck.Evaluate(inputs), "cleanup.pending"));
        }

        [Fact]
        public void Cleanup_with_zero_items_still_reports_an_ok_item()
        {
            var inputs = PerfectMachine();
            inputs.CleanupCandidateBytes = 0;
            inputs.CleanupCandidateCount = 0;

            var item = Find(HealthCheck.Evaluate(inputs), "cleanup.pending");
            Assert.True(item != null, "扫描过且什么都没发现时，应当有一条 Ok 文案，而不是整条消失");
            Assert.Equal(HealthStatus.Ok, item!.Status);
        }

        // ---------------- 6. 回收站 ----------------

        [Fact]
        public void Large_recycle_bin_produces_a_notice()
        {
            var inputs = PerfectMachine();
            inputs.RecycleBinBytes = 6L * Gigabyte;

            Assert.Equal(HealthStatus.Notice, StatusOf(HealthCheck.Evaluate(inputs), "recyclebin"));
        }

        [Theory]
        [InlineData(5L * 1024 * 1024 * 1024)]        // 恰好 5 GB 不触发（严格大于）
        [InlineData(0L)]
        [InlineData(100L * 1024 * 1024)]
        public void Recycle_bin_within_threshold_is_ok(long bytes)
        {
            var inputs = PerfectMachine();
            inputs.RecycleBinBytes = bytes;

            Assert.Equal(HealthStatus.Ok, StatusOf(HealthCheck.Evaluate(inputs), "recyclebin"));
        }

        [Fact]
        public void Unknown_recycle_bin_produces_no_item_at_all()
        {
            // -1 = 未知。未知不能被当成 0，也不能被当成"占用很大"，只能整条不出现。
            var inputs = PerfectMachine();
            inputs.RecycleBinBytes = -1;

            Assert.Null(Find(HealthCheck.Evaluate(inputs), "recyclebin"));
        }

        // ---------------- 7. 隔离区 ----------------

        [Fact]
        public void Large_quarantine_produces_a_notice()
        {
            var inputs = PerfectMachine();
            inputs.QuarantineBytes = 3L * Gigabyte;

            Assert.Equal(HealthStatus.Notice, StatusOf(HealthCheck.Evaluate(inputs), "quarantine"));
        }

        [Fact]
        public void Small_quarantine_produces_an_ok_item()
        {
            var inputs = PerfectMachine();
            inputs.QuarantineBytes = 500L * Megabyte;

            Assert.Equal(HealthStatus.Ok, StatusOf(HealthCheck.Evaluate(inputs), "quarantine"));
        }

        [Fact]
        public void Zero_quarantine_produces_no_item()
        {
            // 以代码为准：隔离区分支是 "> 0 才加 Ok 项"，所以 0 时整条不出现
            var inputs = PerfectMachine();
            inputs.QuarantineBytes = 0;

            Assert.Null(Find(HealthCheck.Evaluate(inputs), "quarantine"));
        }

        // ---------------- 8. 权限 ----------------

        [Fact]
        public void Standard_user_gets_a_notice_permission_item()
        {
            var inputs = PerfectMachine();
            inputs.RunningAsAdmin = false;

            var report = HealthCheck.Evaluate(inputs);

            var item = Find(report, "permission");
            Assert.True(item != null, "权限项应当始终存在");
            Assert.Equal(HealthStatus.Notice, item!.Status);
        }

        [Fact]
        public void Administrator_gets_an_ok_permission_item()
        {
            var report = HealthCheck.Evaluate(PerfectMachine());

            var item = Find(report, "permission");
            Assert.True(item != null);
            Assert.Equal(HealthStatus.Ok, item!.Status);
        }

        // ---------------- 9. 历史失败 ----------------

        [Theory]
        [InlineData(3, true)]
        [InlineData(1, true)]
        [InlineData(0, false)]
        [InlineData(-1, false)]
        public void Failed_operation_journal_item_appears_only_when_positive(int failures, bool shouldAppear)
        {
            var inputs = PerfectMachine();
            inputs.RecentFailedOperations = failures;

            var item = Find(HealthCheck.Evaluate(inputs), "journal.failed");
            Assert.Equal(shouldAppear, item != null);
        }

        // ---------------- 10. 评分单调性 ----------------

        [Fact]
        public void Score_is_monotonic_when_the_machine_gets_better()
        {
            var diskOnly = new Dictionary<double, int>();
            foreach (var percent in new[] { 1.0, 4.0, 9.0, 15.0, 25.0, 60.0 })
            {
                diskOnly[percent] = HealthCheck.Evaluate(WithDiskFreePercent(percent)).Score;
            }

            var memoryOnly = new Dictionary<double, int>();
            foreach (var percent in new[] { 99.0, 96.0, 86.0, 76.0, 30.0 })
            {
                memoryOnly[percent] = HealthCheck.Evaluate(WithMemoryUsedPercent(percent)).Score;
            }

            // 单变量递进：磁盘越来越空。
            // 注意 1% 和 4% 同属 Critical 档，扣分相同，所以这里用"不高于"而不是"严格小于"。
            Assert.True(diskOnly[1.0] <= diskOnly[4.0], "空闲 1% 不应比 4% 得分高");
            Assert.True(diskOnly[4.0] < diskOnly[9.0], "空闲 4%(Critical) 应当比 9%(Warning) 扣得多");
            Assert.True(diskOnly[9.0] < diskOnly[15.0], "空闲 9%(Warning) 应当比 15%(Notice) 扣得多");
            Assert.True(diskOnly[15.0] < diskOnly[25.0], "空闲 15%(Notice) 应当比 25%(Ok) 扣分");
            Assert.Equal(diskOnly[25.0], diskOnly[60.0]); // 都是 Ok 档，不扣分

            // 单变量递进：内存占用越来越低
            Assert.True(memoryOnly[99.0] <= memoryOnly[96.0], "99% 与 96% 同属 Critical");
            Assert.True(memoryOnly[96.0] < memoryOnly[86.0], "96% 应当比 86% 扣得多");
            Assert.True(memoryOnly[86.0] < memoryOnly[76.0], "86% 应当比 76% 扣得多");
            Assert.True(memoryOnly[76.0] < memoryOnly[30.0], "76% 应当比 30% 扣分");
        }

        [Fact]
        public void Score_never_increases_when_the_machine_gets_worse()
        {
            // 与上一条方向相反：所有指标同步变好 → 分数不降；反过来 → 分数不升
            var better = new[]
            {
                HealthCheck.Evaluate(WorstCase()).Score,
                HealthCheck.Evaluate(MediumCase()).Score,
                HealthCheck.Evaluate(PerfectMachine()).Score,
            };

            for (int i = 1; i < better.Length; i++)
            {
                Assert.True(better[i - 1] <= better[i],
                    "指标同步变好时分数不应下降：" + better[i - 1] + " -> " + better[i]);
            }

            // 反过来遍历同一组数据：从"完美"倒退到"最差"，分数只能持平或下降
            for (int i = better.Length - 2; i >= 0; i--)
            {
                Assert.True(better[i + 1] >= better[i],
                    "指标同步变差时分数不应上升：" + better[i] + " -> " + better[i + 1]);
            }
        }

        private static HealthInputs MediumCase()
        {
            var inputs = PerfectMachine();
            inputs.SystemDriveFreeBytes = (ulong)(Total100Gb * 0.12d);
            inputs.MemoryUsedBytes = (ulong)(Memory16Gb * 0.80d);
            inputs.TopCpuProcessName = "busy.exe";
            inputs.TopCpuProcessPercent = 70d;
            inputs.CleanupCandidateBytes = 300L * Megabyte;
            inputs.CleanupCandidateCount = 5;
            inputs.RecycleBinBytes = 1L * Gigabyte;
            inputs.QuarantineBytes = 100L * Megabyte;
            inputs.RunningAsAdmin = false;
            inputs.RecentFailedOperations = 0;
            return inputs;
        }

        private static HealthInputs WorstCase()
        {
            var inputs = PerfectMachine();
            inputs.SystemDriveFreeBytes = (ulong)(Total100Gb * 0.01d);
            inputs.MemoryUsedBytes = (ulong)(Memory16Gb * 0.99d);
            inputs.TopCpuProcessName = "hog.exe";
            inputs.TopCpuProcessPercent = 99d;
            inputs.CleanupCandidateBytes = 10L * Gigabyte;
            inputs.CleanupCandidateCount = 900;
            inputs.RecycleBinBytes = 50L * Gigabyte;
            inputs.QuarantineBytes = 50L * Gigabyte;
            inputs.RunningAsAdmin = false;
            inputs.RecentFailedOperations = 12;
            return inputs;
        }

        // ---------------- 11. 分数被夹在 [0,100] ----------------

        [Fact]
        public void Score_is_clamped_to_zero_hundred_and_verdict_says_act_now()
        {
            var report = HealthCheck.Evaluate(WorstCase());

            Assert.True(report.Score >= 0, "分数不能为负，实际 " + report.Score);
            Assert.True(report.Score <= 100, "分数不能超过 100，实际 " + report.Score);
            Assert.True(report.CriticalCount > 0, "全是 Critical 的输入必须报出 Critical 项");
            Assert.Contains("尽快", report.Verdict);
        }

        // ---------------- 12. 每一项都有可读文案 ----------------

        [Fact]
        public void Every_item_has_readable_text()
        {
            var scenarios = new[] { PerfectMachine(), MediumCase(), WorstCase() };

            foreach (var inputs in scenarios)
            {
                foreach (var item in HealthCheck.Evaluate(inputs).Items)
                {
                    Assert.False(string.IsNullOrWhiteSpace(item.Id), "Id 不能为空");
                    Assert.False(string.IsNullOrWhiteSpace(item.Title), item.Id + " 的 Title 不能为空");
                    Assert.False(string.IsNullOrWhiteSpace(item.Detail), item.Id + " 的 Detail 不能为空");
                    Assert.False(string.IsNullOrWhiteSpace(item.StatusText), item.Id + " 的 StatusText 不能为空");
                }
            }
        }

        [Fact]
        public void Needs_action_is_true_only_for_warning_and_critical()
        {
            var inputs = WorstCase();
            inputs.RecycleBinBytes = 0;

            foreach (var item in HealthCheck.Evaluate(inputs).Items)
            {
                var expected = item.Status == HealthStatus.Warning || item.Status == HealthStatus.Critical;
                Assert.Equal(expected, item.NeedsAction);
            }
        }

        // ---------------- 13. SummaryText ----------------

        [Fact]
        public void Summary_text_contains_score_and_verdict()
        {
            foreach (var inputs in new[] { PerfectMachine(), MediumCase(), WorstCase() })
            {
                var report = HealthCheck.Evaluate(inputs);

                Assert.Contains(report.Score.ToString(), report.SummaryText);
                Assert.Contains(report.Verdict.Substring(0, 4), report.SummaryText);
            }
        }

        // ---------------- 14. 空输入 ----------------

        [Fact]
        public void Evaluate_rejects_null_inputs()
        {
            Assert.Throws<ArgumentNullException>(() => HealthCheck.Evaluate(null!));
        }

        [Fact]
        public void Empty_inputs_do_not_throw_and_only_report_permission()
        {
            // 一个字段都没填（全是默认值）时也必须有结论，不能抛异常或给出空报告
            var report = HealthCheck.Evaluate(new HealthInputs());

            Assert.NotNull(report.Items);
            Assert.NotEmpty(report.Items);
            Assert.InRange(report.Score, 0, 100);
        }
    }
}
