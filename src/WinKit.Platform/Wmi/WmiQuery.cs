using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using WinKit.Core.Diagnostics;

namespace WinKit.Platform.Wmi
{
    /// <summary>
    /// WMI 查询封装。三个要点：
    /// 1) 只把需要的属性读进字典，ManagementObject 立即释放，避免 COM 对象泄漏；
    /// 2) 查询失败只记日志并返回空集合，绝不因为某个 WMI 类不可用就崩掉界面；
    /// 3) 指定命名空间的重载必须换个方法名 —— 见 <see cref="QueryInScope"/> 的说明。
    /// </summary>
    public static class WmiQuery
    {
        public const string CimV2 = @"root\cimv2";

        /// <summary>在默认命名空间 root\cimv2 上查询，只取 <paramref name="properties"/> 里列出的属性。</summary>
        public static IReadOnlyList<Dictionary<string, object?>> Query(
            string wql, params string[] properties)
        {
            return QueryInScope(wql, CimV2, properties);
        }

        /// <summary>
        /// 在指定命名空间上查询。
        ///
        /// 注意：这里**故意**不叫 Query。曾经它是 <c>Query(string wql, string scope, params string[] properties)</c>，
        /// 于是 <c>Query(wql, "Caption")</c> 这种「WQL + 属性名」的调用会被重载决议选到这里
        /// （正常形式 string + 空 params 数组 优先于 params 展开），属性名被当成命名空间，
        /// ManagementScope 连不上 → 抛"无效命名空间" → 被 catch 吞掉 → **所有 WMI 查询都静默返回空集合**。
        /// 换成独立方法名后两种调用形态不可能再混淆。
        /// </summary>
        public static IReadOnlyList<Dictionary<string, object?>> QueryInScope(
            string wql, string scope, params string[] properties)
        {
            var rows = new List<Dictionary<string, object?>>();
            if (string.IsNullOrWhiteSpace(wql)) return rows;

            try
            {
                using (var searcher = new ManagementObjectSearcher(new ManagementScope(NormalizeScope(scope)), new ObjectQuery(wql)))
                using (var collection = searcher.Get())
                {
                    foreach (ManagementBaseObject item in collection)
                    {
                        using (item)
                        {
                            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                            foreach (var name in properties)
                            {
                                object? value = null;
                                try { value = item[name]; } catch (Exception) { }
                                row[name] = value;
                            }
                            rows.Add(row);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("WMI 查询失败（" + wql + "）：" + ex.Message);
            }

            return rows;
        }

        /// <summary>
        /// 兜底：命名空间为空时回落到 root\cimv2，避免 ManagementScope 收到 null 直接抛异常。
        /// 带反斜杠的完整路径（\\.\root\cimv2、\\机器名\root\cimv2）原样返回。
        /// </summary>
        internal static string NormalizeScope(string? scope)
        {
            var text = (scope ?? string.Empty).Trim().TrimEnd('\\');
            if (text.Length == 0) return CimV2;
            if (text.StartsWith(@"\\", StringComparison.Ordinal)) return text;
            return @"\\.\" + text;
        }

        public static string? AsString(IReadOnlyDictionary<string, object?> row, string key)
        {
            object? value;
            if (!row.TryGetValue(key, out value) || value == null) return null;
            var text = value.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
        }

        public static ulong AsUInt64(IReadOnlyDictionary<string, object?> row, string key)
        {
            object? value;
            if (!row.TryGetValue(key, out value) || value == null) return 0UL;
            if (value is ulong u) return u;
            if (value is long l && l >= 0) return (ulong)l;

            ulong parsed;
            return ulong.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : 0UL;
        }

        public static int AsInt32(IReadOnlyDictionary<string, object?> row, string key)
        {
            object? value;
            if (!row.TryGetValue(key, out value) || value == null) return 0;
            if (value is int i) return i;
            if (value is uint ui) return (int)ui;

            int parsed;
            return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : 0;
        }

        /// <summary>
        /// 取进程命令行。这是冷数据（WMI 单次 50~300ms），只在用户选中某一行时按需查询。
        /// </summary>
        public static string? GetProcessCommandLine(int pid)
        {
            var rows = Query("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid,
                "CommandLine");
            return rows.Count > 0 ? AsString(rows[0], "CommandLine") : null;
        }
    }
}
