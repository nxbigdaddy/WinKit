using System;
using System.Collections.Generic;
using System.IO;
using WinKit.Core.Diagnostics;
using WinKit.Core.Rules;
using WinKit.Core.Tasks;

namespace WinKit.Core.Cleanup
{
    /// <summary>
    /// 清理扫描器。三条不可动摇的安全约束：
    ///
    /// 1. **只把目标目录的直接子项当作候选**，永远不动目标目录本身 ——
    ///    "清理 %TEMP%" 绝不可能演变成"删除 %TEMP%"。
    /// 2. **不跟随重解析点**（junction / 符号链接），避免目录循环和越界删除。
    /// 3. **跳过太新的项**（MinimumAge），临时文件被占用时删不掉，先天的年龄过滤能省掉大量失败。
    ///
    /// 本类只做扫描，不做任何删除 —— 删除在 WinKit.Platform 的 CleanupExecutor 里，
    /// 中间必须经过用户确认。
    /// </summary>
    public sealed class CleanupScanner
    {
        private readonly PathGuard _guard;

        public CleanupScanner(PathGuard guard)
        {
            _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        }

        public CleanupScanResult Scan(IReadOnlyList<CleanupRule> rules, TaskContext context, ScanOptions? options = null)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (context == null) throw new ArgumentNullException(nameof(context));

            var opts = options ?? new ScanOptions();
            var exclude = NormalizeExcludes(opts.ExcludeRoots);
            var result = new CleanupScanResult();
            var total = Math.Max(1, rules.Count);
            var index = 0;

            foreach (var rule in rules)
            {
                context.ThrowIfCancelled();
                index++;
                context.Report(new TaskProgress(index / (double)total, "正在扫描：" + rule.Name));

                var summary = new CleanupRuleSummary(rule);
                foreach (var template in rule.Targets)
                {
                    context.ThrowIfCancelled();

                    var target = Expand(template);
                    if (string.IsNullOrEmpty(target))
                    {
                        // 展开失败 ≠ 目录不存在：前者是规则配置写错了，必须单独记，否则静默漏扫
                        summary.InvalidTargets.Add(template);
                        Log.Warn("清理规则的目标路径无法识别：" + rule.Id + " → " + template);
                        continue;
                    }

                    try
                    {
                        if (File.Exists(target))
                        {
                            AddSingleFile(rule, summary, target, opts, exclude);
                        }
                        else if (Directory.Exists(target))
                        {
                            ScanDirectory(rule, summary, target, opts, exclude, 0);
                        }
                        else
                        {
                            // 目标不存在是正常情况（例如从未产生过崩溃转储）
                            summary.MissingTargets.Add(target);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        summary.SkippedError++;
                        Log.Debug("清理规则扫描失败：" + rule.Id + " → " + target + " → " + ex.Message);
                    }
                }

                result.Summaries.Add(summary);
                context.Info(rule.Id + "：" + summary.SummaryText);
            }

            context.Report(TaskProgress.Completed("扫描完成：共 " + result.TotalItems + " 项，"
                + Text.ByteSize.Format(result.TotalSize)));
            return result;
        }

        private void ScanDirectory(CleanupRule rule, CleanupRuleSummary summary, string directory,
            ScanOptions opts, List<string> excludeRoots, int depth)
        {
            if (depth > opts.MaxDepth) return;

            List<string> children;
            try
            {
                children = new List<string>(Directory.EnumerateFileSystemEntries(directory, rule.SearchPattern));
            }
            catch (Exception ex)
            {
                summary.SkippedError++;
                Log.Debug("无法枚举目录：" + directory + " → " + ex.Message);
                return;
            }

            var truncated = false;
            var classified = 0;

            foreach (var child in children)
            {
                // 按"已检查数"计数：即使全部被跳过也能停下来，否则病态目录会拖死扫描
                if (classified >= opts.MaxItemsPerRule)
                {
                    truncated = true;
                    break;
                }

                var verdict = Classify(rule, child, opts, excludeRoots, out var isDirectory, out var size, out var lastWrite);
                classified++;

                switch (verdict)
                {
                    case CandidateVerdict.Accept:
                        summary.Candidates.Add(new CleanupCandidate(
                            rule.Id, rule.Name, child, isDirectory, size, lastWrite));
                        break;
                    case CandidateVerdict.TooNew: summary.SkippedTooNew++; break;
                    case CandidateVerdict.Protected: summary.SkippedProtected++; break;
                    case CandidateVerdict.ReparsePoint: summary.SkippedReparsePoint++; break;
                    case CandidateVerdict.Excluded: summary.SkippedExcluded++; break;
                    case CandidateVerdict.Error: summary.SkippedError++; break;
                    case CandidateVerdict.NotADirectory: break;
                }
            }

            if (truncated) summary.TruncatedByLimit++;
        }

        private void AddSingleFile(CleanupRule rule, CleanupRuleSummary summary, string file,
            ScanOptions opts, List<string> excludeRoots)
        {
            if (summary.ItemCount >= opts.MaxItemsPerRule)
            {
                summary.TruncatedByLimit++;
                return;
            }

            var verdict = Classify(rule, file, opts, excludeRoots, out var isDirectory, out var size, out var lastWrite);

            switch (verdict)
            {
                case CandidateVerdict.Accept:
                    summary.Candidates.Add(new CleanupCandidate(rule.Id, rule.Name, file, isDirectory, size, lastWrite));
                    break;
                case CandidateVerdict.TooNew: summary.SkippedTooNew++; break;
                case CandidateVerdict.Protected: summary.SkippedProtected++; break;
                case CandidateVerdict.ReparsePoint: summary.SkippedReparsePoint++; break;
                case CandidateVerdict.Excluded: summary.SkippedExcluded++; break;
                case CandidateVerdict.Error: summary.SkippedError++; break;
            }
        }

        private enum CandidateVerdict
        {
            Accept,
            TooNew,
            Protected,
            ReparsePoint,
            Excluded,
            NotADirectory,
            Error,
        }

        private CandidateVerdict Classify(CleanupRule rule, string path, ScanOptions opts,
            List<string> excludeRoots, out bool isDirectory, out long size, out DateTime lastWriteUtc)
        {
            isDirectory = false;
            size = 0;
            lastWriteUtc = DateTime.MinValue;

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception)
            {
                return CandidateVerdict.Error;
            }

            // 不跟随链接：否则可能顺着 junction 删到规则声明范围之外
            if ((attributes & FileAttributes.ReparsePoint) != 0) return CandidateVerdict.ReparsePoint;

            isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (isDirectory && !rule.IncludeDirectories) return CandidateVerdict.NotADirectory;

            if (IsUnderAny(path, excludeRoots)) return CandidateVerdict.Excluded;

            // 受保护目录：只有规则显式声明 AllowProtected 才放行（例如 C:\Windows\Temp）
            if (!rule.AllowProtected && !_guard.IsAllowed(path)) return CandidateVerdict.Protected;

            if (isDirectory)
            {
                try { lastWriteUtc = Directory.GetLastWriteTimeUtc(path); }
                catch (Exception) { lastWriteUtc = DateTime.MinValue; }
                size = DirectorySize(path, opts.MaxDepth);
            }
            else
            {
                try
                {
                    var info = new FileInfo(path);
                    lastWriteUtc = info.LastWriteTimeUtc;
                    size = info.Length;
                }
                catch (Exception)
                {
                    return CandidateVerdict.Error;
                }
            }

            var minimumAge = opts.MinimumAgeOverride ?? rule.MinimumAge;
            if (minimumAge > TimeSpan.Zero && lastWriteUtc != DateTime.MinValue)
            {
                if (DateTime.UtcNow - lastWriteUtc < minimumAge) return CandidateVerdict.TooNew;
            }

            return CandidateVerdict.Accept;
        }

        /// <summary>目录大小（尽力而为、有深度上限、不跟随链接）。只用于界面展示，读不到的项按 0 计。</summary>
        internal static long DirectorySize(string directory, int maxDepth)
        {
            if (maxDepth < 0) return 0;

            long total = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    try { total += new FileInfo(file).Length; } catch (Exception) { }
                }
            }
            catch (Exception) { }

            if (maxDepth == 0) return total;

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(directory))
                {
                    try
                    {
                        if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch (Exception) { continue; }

                    total += DirectorySize(sub, maxDepth - 1);
                }
            }
            catch (Exception) { }

            return total;
        }

        internal static string Expand(string template)
        {
            if (string.IsNullOrWhiteSpace(template)) return string.Empty;

            var expanded = Environment.ExpandEnvironmentVariables(template.Trim());
            if (expanded.IndexOf('%') >= 0)
            {
                // 环境变量没展开成功（例如 %ProgramData% 在某些精简系统上缺失）
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(expanded);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        internal static List<string> NormalizeExcludes(IEnumerable<string> roots)
        {
            var list = new List<string>();
            if (roots == null) return list;

            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                var normalized = PathGuard.Normalize(root);
                if (normalized.Length > 0) list.Add(normalized);
            }
            return list;
        }

        internal static bool IsUnderAny(string path, List<string> roots)
        {
            if (roots == null || roots.Count == 0) return false;
            var normalized = PathGuard.Normalize(path);
            foreach (var root in roots)
            {
                if (PathGuard.IsUnder(normalized, root)) return true;
            }
            return false;
        }
    }
}
