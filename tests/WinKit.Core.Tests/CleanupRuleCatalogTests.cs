using System;
using System.Collections.Generic;
using WinKit.Core.Cleanup;
using WinKit.Core.Journal;
using Xunit;

namespace WinKit.Core.Tests
{
    /// <summary>
    /// 内置清理规则库的不变式测试。
    ///
    /// 规则库是产品对用户的承诺：每条规则都必须说清楚"删的是什么、有什么影响"，
    /// 危险的规则必须明确标注权限与危险等级。这些承诺一旦被后续改动破坏
    /// （例如新加一条描述为空的规则、或者随手给受保护目录开绿灯），
    /// 用户就会在不知情的前提下丢数据，所以必须由测试守住。
    /// </summary>
    public class CleanupRuleCatalogTests
    {
        private static IReadOnlyList<CleanupRule> Rules()
        {
            var rules = CleanupRuleCatalog.CreateDefault();
            Assert.NotNull(rules);
            return rules;
        }

        /// <summary>拼接用于失败提示的定位串，出错时能直接看出是哪条规则。</summary>
        private static string Tag(CleanupRule rule)
        {
            return "规则「" + rule.Id + " / " + rule.Name + "」";
        }

        private static List<string> PrefetchTargets()
        {
            var list = new List<string>();
            foreach (var rule in Rules())
            {
                foreach (var target in rule.Targets)
                {
                    if (target.IndexOf("prefetch", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        list.Add(rule.Id + " → " + target);
                    }
                }
            }
            return list;
        }

        [Fact]
        public void 默认规则库非空()
        {
            Assert.NotEmpty(Rules());
        }

        [Fact]
        public void 所有规则Id唯一且非空()
        {
            var seen = new List<string>();

            foreach (var rule in Rules())
            {
                Assert.False(string.IsNullOrWhiteSpace(rule.Id), "存在 Id 为空的规则");
                Assert.True(rule.Id.IndexOf(' ') < 0, Tag(rule) + " 的 Id 含空格");
                Assert.False(seen.Contains(rule.Id), "存在重复的规则 Id：" + rule.Id);
                seen.Add(rule.Id);
            }
        }

        [Fact]
        public void 所有规则名称描述与影响都非空()
        {
            // 这是"用户有权知道自己在删什么"的硬要求：描述为空 = 没解释
            foreach (var rule in Rules())
            {
                Assert.False(string.IsNullOrWhiteSpace(rule.Name), Tag(rule) + " 的名称为空");
                Assert.False(string.IsNullOrWhiteSpace(rule.Description), Tag(rule) + " 的描述为空");
                Assert.False(string.IsNullOrWhiteSpace(rule.Impact), Tag(rule) + " 的影响说明为空");
            }
        }

        [Fact]
        public void 所有规则都有非空的目标路径()
        {
            foreach (var rule in Rules())
            {
                Assert.NotNull(rule.Targets);
                Assert.NotEmpty(rule.Targets);

                foreach (var target in rule.Targets)
                {
                    Assert.False(string.IsNullOrWhiteSpace(target), Tag(rule) + " 含空的目标路径");
                }
            }
        }

        [Fact]
        public void 放行受保护目录的规则必须同时需要管理员且为高危险等级()
        {
            // 防止随手给 Windows / Program Files 之类的目录开绿灯
            foreach (var rule in Rules())
            {
                if (!rule.AllowProtected) continue;

                Assert.True(rule.RequiresAdmin,
                    Tag(rule) + " 放行了受保护目录，却不是 RequiresAdmin");

                var level = rule.DangerLevel;
                var acceptable = string.Equals(level, DangerLevel.HardToReverse, StringComparison.Ordinal)
                    || string.Equals(level, DangerLevel.HighRisk, StringComparison.Ordinal);

                Assert.True(acceptable,
                    Tag(rule) + " 放行了受保护目录，危险等级却是 " + level + "（应为 L2/L3）");
            }
        }

        [Fact]
        public void 需要管理员的规则默认必须是关闭的()
        {
            // 否则标准权限用户一进来就看到一堆扫不出东西的规则
            foreach (var rule in Rules())
            {
                if (!rule.RequiresAdmin) continue;

                Assert.False(rule.DefaultEnabled,
                    Tag(rule) + " 需要管理员权限却是默认启用");
            }
        }

        [Fact]
        public void 所有规则的最小年龄不为负()
        {
            foreach (var rule in Rules())
            {
                Assert.True(rule.MinimumAge >= TimeSpan.Zero,
                    Tag(rule) + " 的 MinimumAge 为负：" + rule.MinimumAge);
            }
        }

        [Fact]
        public void 默认启用集合非空且都不需要管理员()
        {
            var enabled = new List<CleanupRule>();
            foreach (var rule in Rules())
            {
                if (rule.DefaultEnabled) enabled.Add(rule);
            }

            Assert.NotEmpty(enabled);

            foreach (var rule in enabled)
            {
                Assert.False(rule.RequiresAdmin,
                    Tag(rule) + " 是默认启用规则，却需要管理员权限");
            }
        }

        [Fact]
        public void FindById_能取到内置规则_找不到时返回null()
        {
            var rule = CleanupRuleCatalog.FindById("temp.user");

            Assert.NotNull(rule);
            Assert.Equal("temp.user", rule!.Id);
            Assert.False(string.IsNullOrWhiteSpace(rule.Name));

            Assert.Null(CleanupRuleCatalog.FindById("不存在的 id"));
            Assert.Null(CleanupRuleCatalog.FindById(null));
        }

        [Fact]
        public void 规则库不包含Prefetch相关规则()
        {
            // 刻意排除的反效果项：清了 Prefetch 会让下次启动变慢。
            // FindById 与 Target 两条路径都查一遍。
            Assert.Null(CleanupRuleCatalog.FindById("prefetch"));
            Assert.Null(CleanupRuleCatalog.FindById("prefetch.system"));

            var offenders = PrefetchTargets();
            Assert.True(offenders.Count == 0,
                "规则库不应包含指向 Prefetch 的规则，实际有：" + string.Join("；", offenders));
        }
    }
}
