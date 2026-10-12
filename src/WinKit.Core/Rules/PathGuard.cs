using System;
using System.Collections.Generic;
using System.IO;

namespace WinKit.Core.Rules
{
    public enum GuardVerdict
    {
        Allowed,
        Protected,
    }

    public sealed class GuardResult
    {
        internal GuardResult(GuardVerdict verdict, string normalizedPath, string? reason)
        {
            Verdict = verdict;
            NormalizedPath = normalizedPath;
            Reason = reason;
        }

        public GuardVerdict Verdict { get; }
        public string NormalizedPath { get; }
        public string? Reason { get; }
        public bool IsAllowed => Verdict == GuardVerdict.Allowed;

        public override string ToString() => Verdict + (Reason == null ? "" : " (" + Reason + ")") + " → " + NormalizedPath;
    }

    /// <summary>
    /// 路径守卫：清理/删除类操作的第一道闸门。默认拒绝系统关键目录，
    /// 用户白名单（Allow）优先级高于保护规则。
    ///
    /// 关键点是**边界比较**：C:\WindowsApps 不属于 C:\Windows，
    /// 否则一个前缀比较就会把用户的合法目录误判成系统目录。
    /// </summary>
    public sealed class PathGuard
    {
        private readonly List<KeyValuePair<string, string>> _protected =
            new List<KeyValuePair<string, string>>();

        private readonly List<string> _allowed = new List<string>();

        /// <summary>按 Windows / Program Files / ProgramData 等系统目录构建默认守卫。</summary>
        public static PathGuard CreateDefault()
        {
            var guard = new PathGuard();

            guard.Protect(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Windows 系统目录");
            guard.Protect(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Program Files");
            guard.Protect(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Program Files (x86)");
            guard.Protect(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProgramData");

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrEmpty(desktop)) guard.Protect(desktop, "桌面");
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(documents)) guard.Protect(documents, "文档");
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (!string.IsNullOrEmpty(pictures)) guard.Protect(pictures, "图片");

            return guard;
        }

        public PathGuard Protect(string path, string label)
        {
            if (string.IsNullOrWhiteSpace(path)) return this;
            _protected.Add(new KeyValuePair<string, string>(Normalize(path), label ?? "受保护目录"));
            return this;
        }

        /// <summary>白名单。用户显式允许的目录优先于保护规则（例如允许清理 Windows\Temp）。</summary>
        public PathGuard Allow(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return this;
            _allowed.Add(Normalize(path));
            return this;
        }

        public int ProtectedCount => _protected.Count;

        public GuardResult Evaluate(string path)
        {
            var normalized = Normalize(path);
            if (string.IsNullOrEmpty(normalized))
            {
                return new GuardResult(GuardVerdict.Protected, normalized, "路径无效");
            }

            foreach (var allow in _allowed)
            {
                if (IsUnder(normalized, allow))
                {
                    return new GuardResult(GuardVerdict.Allowed, normalized, null);
                }
            }

            foreach (var item in _protected)
            {
                if (IsUnder(normalized, item.Key))
                {
                    return new GuardResult(GuardVerdict.Protected, normalized, item.Value);
                }
            }

            return new GuardResult(GuardVerdict.Allowed, normalized, null);
        }

        public bool IsAllowed(string path) => Evaluate(path).IsAllowed;

        /// <summary>
        /// 规范化：展开环境变量 → 解析相对路径与 ".." → 去掉结尾分隔符 → 盘符统一大写。
        /// 纯函数，单元测试重点覆盖。
        /// </summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;

            var expanded = path;
            try
            {
                expanded = Environment.ExpandEnvironmentVariables(path);
                var full = Path.GetFullPath(expanded);

                // 只去掉多余的结尾分隔符，根目录（C:\）保留
                if (full.Length > 3)
                {
                    full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }

                if (full.Length >= 2 && full[1] == ':')
                {
                    full = char.ToUpperInvariant(full[0]) + full.Substring(1);
                }
                return full;
            }
            catch (Exception)
            {
                // 非法路径：原样返回，交给 IsAllowed 判定为不可用
                return expanded.Trim();
            }
        }

        /// <summary>带分隔符边界的"是否位于某目录之下"判断（含目录自身）。</summary>
        public static bool IsUnder(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) return true;

            var prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;

            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
