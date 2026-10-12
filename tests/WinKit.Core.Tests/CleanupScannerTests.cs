using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using WinKit.Core.Cleanup;
using WinKit.Core.Journal;
using WinKit.Core.Rules;
using WinKit.Core.Tasks;
using Xunit;

namespace WinKit.Core.Tests
{
    /// <summary>
    /// 清理扫描器的行为测试。
    ///
    /// 扫描器只做扫描、不做删除，但它是"删什么"的唯一事实来源，
    /// 所以这里重点守住几条安全约束：
    /// 1. 只把目标目录的**直接子项**当候选，绝不动目标目录本身；
    /// 2. MinimumAge 过滤掉可能正在使用的项；
    /// 3. 排除根（避免清 %TEMP% 时把 WinKit 自己的数据目录删掉）；
    /// 4. 受保护目录默认拦截。
    /// </summary>
    public class CleanupScannerTests
    {
        /// <summary>
        /// 把文件的"最后写入时间"改到 <paramref name="ago"/> 之前。
        ///
        /// 必须显式设置：CleanupRule 的 MinimumAge 默认是 1 小时，
        /// 而测试里刚建出来的文件年龄只有几毫秒，不这样做会被判成"太新"而跳过候选。
        /// 同时把"最后访问时间"一起回拨，避免文件系统上的访问时间干扰判断。
        /// </summary>
        private static void Backdate(string path, TimeSpan? ago = null)
        {
            var when = DateTime.UtcNow - (ago ?? TimeSpan.FromMinutes(10));

            // 目录和文件必须走不同的 API：File.SetLastWriteTimeUtc 内部用 FileStream 打开路径，
            // 而 Windows 不允许把目录当文件打开，对目录调用会抛 UnauthorizedAccessException。
            if (Directory.Exists(path))
            {
                Directory.SetLastWriteTimeUtc(path, when);
                Directory.SetLastAccessTimeUtc(path, when);
            }
            else
            {
                File.SetLastWriteTimeUtc(path, when);
                File.SetLastAccessTimeUtc(path, when);
            }
        }

        /// <summary>默认规则：可撤销、不需要管理员、默认启用、最小年龄 1 小时（照抄生产构造函数顺序）。</summary>
        private static CleanupRule MakeRule(
            string id,
            string target,
            bool allowProtected = false,
            bool includeDirectories = true,
            string searchPattern = "*",
            TimeSpan? minimumAge = null)
        {
            return new CleanupRule(
                id: id,
                name: "测试规则 " + id,
                description: "单元测试用规则",
                impact: "无",
                targets: new[] { target },
                dangerLevel: DangerLevel.Reversible,
                requiresAdmin: false,
                defaultEnabled: true,
                allowProtected: allowProtected,
                // 默认**不**做年龄过滤：年龄过滤由专门的用例覆盖，
                // 其他用例不该被它干扰（生产规则的默认值是 1 小时，这里刻意不同）。
                // 之前这里的默认值是 1 小时，而 Backdate 只回拨 10 分钟，
                // 导致本该入选的项全被判成"太新"，8 个用例一起失败。
                minimumAge: minimumAge ?? TimeSpan.Zero,
                searchPattern: searchPattern,
                includeDirectories: includeDirectories);
        }

        private static TaskContext NewContext(CancellationToken token = default(CancellationToken))
        {
            // 注意 TaskContext 的参数顺序：taskId, token, progress
            return new TaskContext("cleanup-scan-test", token);
        }

        private static CleanupScanResult RunScan(
            IReadOnlyList<CleanupRule> rules,
            PathGuard? guard = null,
            ScanOptions? options = null,
            CancellationToken token = default(CancellationToken))
        {
            var scanner = new CleanupScanner(guard ?? new PathGuard());
            return scanner.Scan(rules, NewContext(token), options);
        }

        private static CleanupRuleSummary SummaryOf(CleanupScanResult result, string ruleId)
        {
            foreach (var summary in result.Summaries)
            {
                if (summary.Rule.Id == ruleId) return summary;
            }

            throw new InvalidOperationException("扫描结果里没有规则 " + ruleId);
        }

        [Fact]
        public void 只把目标目录的直接子项当候选_绝不动目标目录本身()
        {
            using var tmp = new TempDir();
            var target = tmp.Dir("target");
            File.WriteAllText(Path.Combine(target, "a.txt"), "a");
            File.WriteAllText(Path.Combine(target, "b.txt"), "b");
            Backdate(target);
            var sub = Directory.CreateDirectory(Path.Combine(target, "sub")).FullName;
            Backdate(sub);

            var result = RunScan(new[] { MakeRule("t1", target) });
            var summary = SummaryOf(result, "t1");

            Assert.Equal(3, summary.ItemCount);
            Assert.Equal(0, summary.SkippedTotal);

            var names = new List<string>();
            foreach (var candidate in summary.Candidates) names.Add(Path.GetFileName(candidate.Path));

            Assert.Contains("a.txt", names);
            Assert.Contains("b.txt", names);
            Assert.Contains("sub", names);

            // 关键安全约束：目标目录自身永远不是候选
            var normalizedTarget = PathGuard.Normalize(target);
            foreach (var candidate in summary.Candidates)
            {
                Assert.NotEqual(normalizedTarget, PathGuard.Normalize(candidate.Path));
            }
        }

        [Fact]
        public void MinimumAge过滤掉太新的项()
        {
            using var tmp = new TempDir();
            var target = tmp.Dir("target");
            var oldFile = Path.Combine(target, "old.tmp");
            var newFile = Path.Combine(target, "new.tmp");
            File.WriteAllText(oldFile, "old");
            File.WriteAllText(newFile, "new");
            Backdate(oldFile, TimeSpan.FromDays(3));
            Backdate(target);

            var result = RunScan(new[] { MakeRule("t2", target, minimumAge: TimeSpan.FromHours(1)) });
            var summary = SummaryOf(result, "t2");

            Assert.Equal(1, summary.ItemCount);
            Assert.Equal(oldFile, summary.Candidates[0].Path);
            Assert.Equal(1, summary.SkippedTooNew);
        }

        [Fact]
        public void MinimumAgeOverride为零时不再做年龄过滤()
        {
            using var tmp = new TempDir();
            var target = tmp.Dir("target");
            var oldFile = Path.Combine(target, "old.tmp");
            var newFile = Path.Combine(target, "new.tmp");
            File.WriteAllText(oldFile, "old");
            File.WriteAllText(newFile, "new");
            Backdate(oldFile, TimeSpan.FromDays(3));
            Backdate(target);

            var options = new ScanOptions();
            options.MinimumAgeOverride = TimeSpan.Zero;

            var result = RunScan(new[] { MakeRule("t3", target, minimumAge: TimeSpan.FromHours(1)) }, options: options);
            var summary = SummaryOf(result, "t3");

            Assert.Equal(2, summary.ItemCount);
            Assert.Equal(0, summary.SkippedTooNew);
        }

        [Fact]
        public void 排除根之下的项全部被跳过()
        {
            // 防止"清理临时目录时把 WinKit 自己的数据目录删掉"的关键防线
            using var tmp = new TempDir();
            var target = tmp.Dir("target");
            File.WriteAllText(Path.Combine(target, "a.txt"), "a");
            File.WriteAllText(Path.Combine(target, "b.txt"), "b");
            Backdate(target);

            var options = new ScanOptions();
            options.ExcludeRoots.Add(target);

            var result = RunScan(new[] { MakeRule("t4", target) }, options: options);
            var summary = SummaryOf(result, "t4");

            Assert.Equal(0, summary.ItemCount);
            Assert.True(summary.SkippedExcluded > 0, "排除根之下的项应被计入 SkippedExcluded");
        }

        [Fact]
        public void 受保护路径默认被拦截_allowProtected_true时放行()
        {
            using var tmp = new TempDir();
            var target = tmp.Dir("target");
            File.WriteAllText(Path.Combine(target, "a.txt"), "a");
            Backdate(target);

            var guard = new PathGuard().Protect(target, "测试保护");

            var blocked = RunScan(new[] { MakeRule("t5", target, allowProtected: false) }, guard);
            var blockedSummary = SummaryOf(blocked, "t5");
            Assert.Equal(0, blockedSummary.ItemCount);
            Assert.True(blockedSummary.SkippedProtected > 0, "受保护目录下的项应被计入 SkippedProtected");

            var allowed = RunScan(new[] { MakeRule("t5", target, allowProtected: true) }, guard);
            var allowedSummary = SummaryOf(allowed, "t5");
            Assert.Equal(1, allowedSummary.ItemCount);
            Assert.Equal(0, allowedSummary.SkippedProtected);
        }

        [Fact]
        public void 目标不存在时记入MissingTargets且不产生候选()
        {
            using var tmp = new TempDir();
            var missing = Path.Combine(tmp.Path, "不存在的目录-" + Guid.NewGuid().ToString("N"));

            var result = RunScan(new[] { MakeRule("t6", missing) });
            var summary = SummaryOf(result, "t6");

            Assert.Equal(0, summary.ItemCount);
            Assert.NotEmpty(summary.MissingTargets);
            Assert.Contains("不存在", summary.SummaryText);
        }

        [Fact]
        public void 单规则数量上限生效并被标记为截断()
        {
            using var tmp = new TempDir();
            var target = tmp.Dir("target");
            for (int i = 0; i < 10; i++) File.WriteAllText(Path.Combine(target, "f" + i + ".tmp"), "x");
            Backdate(target);

            var options = new ScanOptions();
            options.MaxItemsPerRule = 3;

            var result = RunScan(new[] { MakeRule("t7", target) }, options: options);
            var summary = SummaryOf(result, "t7");

            Assert.Equal(3, summary.ItemCount);
            Assert.True(summary.TruncatedByLimit >= 1, "达到上限后应置 TruncatedByLimit");
        }

        [Fact]
        public void includeDirectories为false时子目录不进候选()
        {
            using var tmp = new TempDir();
            var target = tmp.Dir("target");
            var sub = Directory.CreateDirectory(Path.Combine(target, "sub")).FullName;
            File.WriteAllText(Path.Combine(target, "a.txt"), "a");
            Backdate(target);
            Backdate(sub);

            var result = RunScan(new[] { MakeRule("t8", target, includeDirectories: false) });
            var summary = SummaryOf(result, "t8");

            Assert.Equal(1, summary.ItemCount);
            Assert.False(summary.Candidates[0].IsDirectory);
            Assert.Equal("a.txt", Path.GetFileName(summary.Candidates[0].Path));
        }

        [Fact]
        public void searchPattern只匹配符合模式的文件()
        {
            using var tmp = new TempDir();
            var target = tmp.Dir("target");
            File.WriteAllText(Path.Combine(target, "a.log"), "a");
            File.WriteAllText(Path.Combine(target, "b.txt"), "b");
            Backdate(target);

            var result = RunScan(new[] { MakeRule("t9", target, searchPattern: "*.log") });
            var summary = SummaryOf(result, "t9");

            Assert.Equal(1, summary.ItemCount);
            Assert.Equal("a.log", Path.GetFileName(summary.Candidates[0].Path));
        }

        [Fact]
        public void 已取消的令牌会让扫描立刻抛出OperationCanceledException()
        {
            using var tmp = new TempDir();
            var target = tmp.Dir("target");
            File.WriteAllText(Path.Combine(target, "a.txt"), "a");
            Backdate(target);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(
                () => RunScan(new[] { MakeRule("t10", target) }, token: cts.Token));
        }

        [Fact]
        public void 目录候选的大小包含其内部文件()
        {
            using var tmp = new TempDir();
            var target = tmp.Dir("target");
            var sub = Directory.CreateDirectory(Path.Combine(target, "sub")).FullName;
            File.WriteAllBytes(Path.Combine(sub, "big.bin"), new byte[1000]);
            Backdate(target);
            Backdate(sub);

            var result = RunScan(new[] { MakeRule("t11", target, includeDirectories: true) });
            var summary = SummaryOf(result, "t11");

            Assert.Equal(1, summary.ItemCount);
            var candidate = summary.Candidates[0];
            Assert.True(candidate.IsDirectory);
            Assert.True(candidate.Size >= 1000, "目录大小应至少包含内部的 1000 字节文件，实际 " + candidate.Size);
        }

        [Fact]
        public void SummaryText在有条目时给出可清理数量与大小()
        {
            using var tmp = new TempDir();
            var target = tmp.Dir("target");
            File.WriteAllText(Path.Combine(target, "a.txt"), "a");
            Backdate(target);

            var result = RunScan(new[] { MakeRule("t12", target) });
            var summary = SummaryOf(result, "t12");

            Assert.Equal(1, summary.ItemCount);
            Assert.Contains("可清理", summary.SummaryText);
            Assert.Contains("项", summary.SummaryText);
        }

        [Fact]
        public void 多条规则各自汇总且总计正确()
        {
            using var tmp = new TempDir();
            var first = tmp.Dir("first");
            var second = tmp.Dir("second");
            File.WriteAllText(Path.Combine(first, "a.txt"), "a");
            File.WriteAllText(Path.Combine(second, "b.txt"), "b");
            File.WriteAllText(Path.Combine(second, "c.txt"), "c");
            Backdate(first);
            Backdate(second);

            var rules = new[]
            {
                MakeRule("m1", first),
                MakeRule("m2", second),
            };

            var result = RunScan(rules);
            var firstSummary = SummaryOf(result, "m1");
            var secondSummary = SummaryOf(result, "m2");

            Assert.Equal(2, result.Summaries.Count);
            Assert.Equal(1, firstSummary.ItemCount);
            Assert.Equal(2, secondSummary.ItemCount);
            Assert.Equal(3, result.TotalItems);
            Assert.True(result.TotalSize > 0);

            var all = new List<CleanupCandidate>();
            foreach (var candidate in result.AllCandidates()) all.Add(candidate);
            Assert.Equal(3, all.Count);
        }

        [Fact]
        public void 空的规则列表返回空结果且不抛异常()
        {
            var rules = new List<CleanupRule>();

            var result = RunScan(rules);

            Assert.Empty(result.Summaries);
            Assert.Equal(0, result.TotalItems);
            Assert.Equal(0, result.TotalSize);
        }
    }
}
