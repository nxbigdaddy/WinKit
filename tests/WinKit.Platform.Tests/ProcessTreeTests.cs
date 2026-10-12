using System;
using System.Collections.Generic;
using System.Diagnostics;
using WinKit.Platform.Diagnostics;
using Xunit;

namespace WinKit.Platform.Tests
{
    /// <summary>
    /// ProcessTree 的排序语义是"子进程先结束、父进程后结束"。
    /// 带 parentMap 参数的重载是纯函数，这里不碰 WMI 就能覆盖各种树形；
    /// 真实 WMI 只留一个断言放宽的用例（见文件末尾）。
    ///
    /// 注意一个容易记错的语义：<see cref="ProcessTree.GetDescendantsInKillOrder(int, IReadOnlyDictionary{int, int})"/>
    /// 返回的是**后代，不含根自身**。所以"子先父后"只能断言"孙 → 子"，
    /// 不能拿根去比（根压根不在结果里）。
    /// </summary>
    public class ProcessTreeTests
    {
        private static IReadOnlyDictionary<int, int> Map(params int[] pidParentPairs)
        {
            if (pidParentPairs.Length % 2 != 0) throw new ArgumentException("必须是 (pid, parent) 成对出现");

            var map = new Dictionary<int, int>();
            for (int i = 0; i < pidParentPairs.Length; i += 2)
            {
                map[pidParentPairs[i]] = pidParentPairs[i + 1];
            }
            return map;
        }

        private static int IndexOf(IReadOnlyList<int> list, int value)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == value) return i;
            }
            return -1;
        }

        private static string Dump(IReadOnlyList<int> list)
        {
            var parts = new List<string>();
            foreach (var v in list) parts.Add(v.ToString());
            return "[" + string.Join(", ", parts) + "]";
        }

        /// <summary>断言 earlier 排在 later 之前（两者都必须存在）。</summary>
        private static void AssertAllBefore(IReadOnlyList<int> order, int later, int earlier)
        {
            var laterIndex = IndexOf(order, later);
            var earlierIndex = IndexOf(order, earlier);

            Assert.True(laterIndex >= 0, later + " 应当出现在结果里，实际顺序 " + Dump(order));
            Assert.True(earlierIndex >= 0, earlier + " 应当出现在结果里，实际顺序 " + Dump(order));
            Assert.True(laterIndex < earlierIndex,
                earlier + " 必须排在 " + later + " 之前，实际顺序 " + Dump(order));
        }

        private static int CountOf(IReadOnlyList<int> list, int value)
        {
            var n = 0;
            foreach (var v in list) if (v == value) n++;
            return n;
        }

        // ---------------- 1. 单层 ----------------

        [Fact]
        public void Single_level_returns_all_children()
        {
            var order = ProcessTree.GetDescendantsInKillOrder(1, Map(2, 1, 3, 1));

            Assert.Equal(2, order.Count);
            Assert.Contains(2, order);
            Assert.Contains(3, order);
            // 同层没有先后关系，所以这里刻意不断言顺序
            Assert.DoesNotContain(1, order);
        }

        [Fact]
        public void Unrelated_processes_are_not_included()
        {
            // 40/41 是另一棵树，根 1 只该拿到 2、3
            var order = ProcessTree.GetDescendantsInKillOrder(1, Map(2, 1, 3, 1, 40, 39, 41, 40));

            Assert.Equal(2, order.Count);
            Assert.DoesNotContain(40, order);
            Assert.DoesNotContain(41, order);
        }

        // ---------------- 2 & 3. 三层链 + 完整顺序 ----------------

        [Fact]
        public void Deepest_child_comes_first_in_a_three_level_chain()
        {
            var map = Map(2, 1, 3, 2, 4, 3);

            var order = ProcessTree.GetDescendantsInKillOrder(1, map);

            Assert.Equal(3, order.Count);
            Assert.Equal(4, order[0]);          // 最深的孙进程排最前
            Assert.Equal(2, order[order.Count - 1]);
            AssertAllBefore(order, 4, 3);
            AssertAllBefore(order, 3, 2);
        }

        [Fact]
        public void Full_kill_order_puts_descendants_first_and_root_last()
        {
            var map = Map(2, 1, 3, 2, 4, 3);

            var order = ProcessTree.GetFullKillOrder(1, map);

            Assert.Equal(4, order.Count);
            Assert.Equal(1, order[order.Count - 1]);
            AssertAllBefore(order, 4, 3);
            AssertAllBefore(order, 3, 2);
            AssertAllBefore(order, 2, 1);
        }

        [Fact]
        public void Full_kill_order_of_a_leaf_is_the_leaf_itself()
        {
            var order = ProcessTree.GetFullKillOrder(7, Map(2, 1, 3, 1));

            Assert.Single(order);
            Assert.Equal(7, order[0]);
        }

        [Fact]
        public void Width_and_depth_combined_keep_children_before_parents()
        {
            // 1 -> {2, 3}，2 -> {4, 5}，5 -> 6
            var map = Map(2, 1, 3, 1, 4, 2, 5, 2, 6, 5);

            var order = ProcessTree.GetDescendantsInKillOrder(1, map);

            // 根 1 不在结果里，其余 5 个后代都要出现且只出现一次
            Assert.Equal(5, order.Count);
            foreach (var child in new[] { 2, 3, 4, 5, 6 })
            {
                Assert.Equal(1, CountOf(order, child));
            }
            AssertAllBefore(order, 4, 2);
            AssertAllBefore(order, 5, 2);
            AssertAllBefore(order, 6, 5);
            Assert.Equal(1, CountOf(ProcessTree.GetFullKillOrder(1, map), 1));
        }

        // ---------------- 4. 根没有后代 ----------------

        [Fact]
        public void Unknown_root_has_no_descendants()
        {
            Assert.Empty(ProcessTree.GetDescendantsInKillOrder(99, Map(2, 1, 3, 1)));
        }

        // ---------------- 5. 空映射 ----------------

        [Fact]
        public void Empty_parent_map_returns_no_descendants_without_throwing()
        {
            var empty = new Dictionary<int, int>();

            Assert.Empty(ProcessTree.GetDescendantsInKillOrder(1, empty));
        }

        [Fact]
        public void Null_parent_map_returns_no_descendants_without_throwing()
        {
            Assert.Empty(ProcessTree.GetDescendantsInKillOrder(1, null!));
        }

        [Fact]
        public void Empty_parent_map_still_kills_the_root_itself()
        {
            // GetFullKillOrder 的语义是"后代 + 根"，所以映射为空时结果就是 [根]。
            // 这是刻意的：WMI 查不到任何进程时，用户点"结束进程树"仍应能结束根进程。
            Assert.Equal(new[] { 1 }, ProcessTree.GetFullKillOrder(1, new Dictionary<int, int>()));
            Assert.Equal(new[] { 1 }, ProcessTree.GetFullKillOrder(1, null!));
        }

        [Fact]
        public void Non_positive_root_pid_is_never_killed()
        {
            var map = Map(2, 1, 3, 1);

            Assert.Empty(ProcessTree.GetDescendantsInKillOrder(0, map));
            Assert.Empty(ProcessTree.GetDescendantsInKillOrder(-1, map));
            // 根非法时不能把非法 PID 塞进结果
            Assert.Empty(ProcessTree.GetFullKillOrder(0, map));
            Assert.Empty(ProcessTree.GetFullKillOrder(-1, map));
        }

        // ---------------- 7. 环 ----------------

        [Fact]
        public void Two_node_cycle_terminates_and_excludes_the_root()
        {
            // 根 2 的父是 1、1 的父又是 2；PID 复用理论上能造出这种数据。
            // 期望：不死循环，根不出现，环里的另一个节点正好出现一次。
            var map = Map(2, 1, 1, 2);

            var order = ProcessTree.GetDescendantsInKillOrder(2, map);

            Assert.Single(order);
            Assert.DoesNotContain(2, order);
            Assert.Equal(1, order[0]);
        }

        [Fact]
        public void Full_kill_order_of_a_cycle_lists_each_pid_exactly_once()
        {
            var map = Map(2, 1, 1, 2);

            var order = ProcessTree.GetFullKillOrder(2, map);

            Assert.Equal(1, CountOf(order, 2)); // 根只出现一次（在最后）
            Assert.Equal(1, CountOf(order, 1));
            Assert.Equal(2, order[order.Count - 1]);
        }

        // ---------------- 8. 自环 ----------------

        [Fact]
        public void Self_parented_process_yields_no_descendants()
        {
            // 5 的父亲是 5 自己：不能把 5 当成自己的子进程，否则会无限展开
            var map = Map(5, 5);

            Assert.Empty(ProcessTree.GetDescendantsInKillOrder(5, map));
        }

        [Fact]
        public void Self_parented_process_is_still_killed_once()
        {
            var order = ProcessTree.GetFullKillOrder(5, Map(5, 5));

            Assert.Single(order);
            Assert.Equal(5, order[0]);
        }

        [Fact]
        public void Cycle_reachable_from_root_does_not_hang_or_duplicate()
        {
            // 根 1 -> 2 -> 3，而 3 的父又指回 1（PID 复用理论上能造出这种数据）。
            // 注意父映射是"pid → 单个父"，所以环只能写成 1 的父是 3，不能让 2 有两个父。
            // 期望：不死循环、不重复、根不出现在后代列表里。
            var map = Map(2, 1, 3, 2, 1, 3);

            var order = ProcessTree.GetDescendantsInKillOrder(1, map);

            Assert.Equal(2, order.Count);
            Assert.Equal(1, CountOf(order, 2));
            Assert.Equal(1, CountOf(order, 3));
            Assert.DoesNotContain(1, order);
            // 3 是 2 的子，必须排在 2 前面
            AssertAllBefore(order, 3, 2);
        }

        [Fact]
        public void Multiple_children_pointing_at_the_same_parent_are_not_deduplicated()
        {
            // 反向索引要容忍"一个父有多个子"以及同一子被写了两次的输入
            var map = Map(2, 1, 3, 1, 4, 1);

            var order = ProcessTree.GetDescendantsInKillOrder(1, map);

            Assert.Equal(3, order.Count);
        }

        // ---------------- 9. 深层链 ----------------

        [Fact]
        public void Very_deep_chain_does_not_overflow_the_stack()
        {
            // 实现是 BFS（迭代），200 层不该有任何栈风险；顺带验证链上的先后关系。
            const int depth = 200;
            var map = new Dictionary<int, int>();
            for (int pid = 2; pid <= depth + 1; pid++)
            {
                map[pid] = pid - 1;
            }

            var order = ProcessTree.GetDescendantsInKillOrder(1, map);

            Assert.Equal(depth, order.Count);
            Assert.Equal(depth + 1, order[0]);              // 最深的一层排最前
            Assert.Equal(2, order[order.Count - 1]);        // 紧挨着根的那一层排最后
            for (int i = 1; i < order.Count; i++)
            {
                Assert.Equal(order[i - 1] - 1, order[i]);   // 链上相邻节点必须是父子
            }
            // 根不在后代列表里
            Assert.Equal(-1, IndexOf(order, 1));

            var full = ProcessTree.GetFullKillOrder(1, map);
            Assert.Equal(depth + 1, full.Count);
            Assert.Equal(1, full[full.Count - 1]);
        }

        [Fact]
        public void Many_siblings_all_appear_exactly_once()
        {
            var map = new Dictionary<int, int>();
            for (int pid = 2; pid <= 301; pid++) map[pid] = 1;

            var order = ProcessTree.GetDescendantsInKillOrder(1, map);

            Assert.Equal(300, order.Count);
            Assert.DoesNotContain(1, order);
        }

        // ---------------- 10. 真实 WMI（断言刻意放宽） ----------------

        [Fact]
        public void Real_wmi_parent_map_is_not_empty()
        {
            var map = ProcessTree.GetParentMap();

            // 只断言"查得到东西"：WMI 被禁用/损坏的机器上这条会失败，但那属于环境问题。
            // 刻意不断言当前进程一定在 map 里，也不断言它的父 PID 是什么（PID 会被复用）。
            Assert.NotEmpty(map);

            foreach (var pair in map)
            {
                Assert.True(pair.Key > 0, "PID 必须为正数，实际 " + pair.Key);
            }

            var self = Process.GetCurrentProcess().Id;
            Assert.True(self > 0);
        }
    }
}
