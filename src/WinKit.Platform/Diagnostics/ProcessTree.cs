using System;
using System.Collections.Generic;
using WinKit.Core.Diagnostics;
using WinKit.Platform.Wmi;

namespace WinKit.Platform.Diagnostics
{
    /// <summary>
    /// 进程树。.NET 的 Process API 拿不到父进程 ID，只能问 WMI，因此这里是**冷路径**：
    /// 一次查询取回全部 (PID, 父PID) 再在内存里建树，只在用户点"结束进程树"时调用一次。
    ///
    /// 返回的顺序是"可以安全按序结束"的顺序：**子进程在前、父进程在后**。
    /// 反过来先杀父进程会让子进程变成孤儿继续跑，看起来像"没杀干净"。
    /// </summary>
    public static class ProcessTree
    {
        private const string QueryText = "SELECT ProcessId, ParentProcessId FROM Win32_Process";

        /// <summary>一次 WMI 查询取回 PID → 父PID 的映射。失败返回空字典（调用方按"没有子进程"处理）。</summary>
        public static IReadOnlyDictionary<int, int> GetParentMap()
        {
            var map = new Dictionary<int, int>();
            var rows = WmiQuery.Query(QueryText, "ProcessId", "ParentProcessId");

            foreach (var row in rows)
            {
                var pid = WmiQuery.AsInt32(row, "ProcessId");
                if (pid <= 0) continue;
                map[pid] = WmiQuery.AsInt32(row, "ParentProcessId");
            }

            if (map.Count == 0)
            {
                Log.Warn("进程树查询返回空结果，可能拿不到父进程信息（结束进程树会退化为只结束单个进程）");
            }
            return map;
        }

        /// <summary>
        /// 取某个进程的全部后代（**不含自身**），按"子先父后"排序。
        /// 用 BFS 层序展开再反转：层序保证父在子之前，反转后即为可安全结束的顺序。
        /// </summary>
        public static IReadOnlyList<int> GetDescendantsInKillOrder(int rootPid)
        {
            return GetDescendantsInKillOrder(rootPid, GetParentMap());
        }

        public static IReadOnlyList<int> GetDescendantsInKillOrder(int rootPid, IReadOnlyDictionary<int, int> parentMap)
        {
            if (parentMap == null || parentMap.Count == 0 || rootPid <= 0)
            {
                return new List<int>();
            }

            // 反向索引：父PID → 子PID 列表
            var children = new Dictionary<int, List<int>>();
            foreach (var pair in parentMap)
            {
                List<int>? list;
                if (!children.TryGetValue(pair.Value, out list))
                {
                    list = new List<int>();
                    children[pair.Value] = list;
                }
                list.Add(pair.Key);
            }

            var visited = new HashSet<int> { rootPid };
            var breadthFirst = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(rootPid);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();

                List<int>? kids;
                if (!children.TryGetValue(current, out kids)) continue;

                foreach (var child in kids)
                {
                    // visited 同时防住 PID 复用造成的环（理论上不该出现，但不值得赌）
                    if (!visited.Add(child)) continue;
                    breadthFirst.Add(child);
                    queue.Enqueue(child);
                }
            }

            // 反转：最深的子进程排最前
            breadthFirst.Reverse();
            return breadthFirst;
        }

        /// <summary>完整结束顺序：先所有后代（深的在前），最后是根进程自己。</summary>
        public static IReadOnlyList<int> GetFullKillOrder(int rootPid)
        {
            return GetFullKillOrder(rootPid, GetParentMap());
        }

        public static IReadOnlyList<int> GetFullKillOrder(int rootPid, IReadOnlyDictionary<int, int> parentMap)
        {
            var order = new List<int>(GetDescendantsInKillOrder(rootPid, parentMap));
            if (rootPid > 0) order.Add(rootPid);
            return order;
        }
    }
}
