using System;
using WinKit.Platform.Metrics;
using Xunit;

namespace WinKit.Platform.Tests
{
    /// <summary>
    /// DiskIoSampler 走 Windows 性能计数器 PhysicalDisk(_Total)。
    /// 这组用例只验证**契约**：不抛异常、速率非负且不是 NaN/Infinity、可用性与原因字段一致。
    /// 刻意不断言速率 &gt; 0（空闲磁盘就是 0），也不断言性能计数器在本机一定可用。
    /// </summary>
    public class DiskIoSamplerTests
    {
        private static void AssertHealthySample(DiskIoSample sample)
        {
            Assert.False(double.IsNaN(sample.ReadBytesPerSecond), "读速率是 NaN");
            Assert.False(double.IsInfinity(sample.ReadBytesPerSecond), "读速率是 Infinity");
            Assert.False(double.IsNaN(sample.WriteBytesPerSecond), "写速率是 NaN");
            Assert.False(double.IsInfinity(sample.WriteBytesPerSecond), "写速率是 Infinity");
            Assert.True(sample.ReadBytesPerSecond >= 0d, "读速率不该为负，实际 " + sample.ReadBytesPerSecond);
            Assert.True(sample.WriteBytesPerSecond >= 0d, "写速率不该为负，实际 " + sample.WriteBytesPerSecond);
        }

        // ---------------- 1. 连续采样 ----------------

        [Fact]
        public void First_sample_is_a_baseline_and_never_throws()
        {
            using (var sampler = new DiskIoSampler())
            {
                var first = sampler.Sample();

                AssertHealthySample(first);
                // 首次只建立基准，速率按设计返回 0
                Assert.Equal(0d, first.ReadBytesPerSecond);
                Assert.Equal(0d, first.WriteBytesPerSecond);
            }
        }

        [Fact]
        public void Two_consecutive_samples_are_non_negative_and_finite()
        {
            using (var sampler = new DiskIoSampler())
            {
                var first = sampler.Sample();
                var second = sampler.Sample();

                AssertHealthySample(first);
                AssertHealthySample(second);
                Assert.Equal(sampler.IsAvailable, second.Available);
            }
        }

        [Fact]
        public void Many_consecutive_samples_never_throw()
        {
            using (var sampler = new DiskIoSampler())
            {
                for (int i = 0; i < 100; i++)
                {
                    AssertHealthySample(sampler.Sample());
                }
            }
        }

        // ---------------- 2. 可用性与原因必须一致 ----------------

        [Fact]
        public void Availability_after_first_sample_agrees_with_the_reason_field()
        {
            using (var sampler = new DiskIoSampler())
            {
                var sample = sampler.Sample();

                Assert.Equal(sampler.IsAvailable, sample.Available);

                if (sampler.IsAvailable)
                {
                    Assert.True(sampler.UnavailableReason == null,
                        "可用时不该同时给出不可用原因，实际：" + sampler.UnavailableReason);
                }
                else
                {
                    // 界面上「不可用但没有原因」是没法向用户解释的状态，必须禁止
                    Assert.False(string.IsNullOrWhiteSpace(sampler.UnavailableReason),
                        "不可用时必须给出原因");
                }
            }
        }

        [Fact]
        public void Two_instances_do_not_share_state()
        {
            using (var a = new DiskIoSampler())
            using (var b = new DiskIoSampler())
            {
                a.Sample();
                var sample = b.Sample();

                Assert.Equal(0d, sample.ReadBytesPerSecond);
                Assert.Equal(0d, sample.WriteBytesPerSecond);
                Assert.Equal(b.IsAvailable, sample.Available);
            }
        }

        // ---------------- 3 & 4. Dispose 语义 ----------------

        [Fact]
        public void Sample_after_dispose_returns_unavailable_and_flags_agree()
        {
            var sampler = new DiskIoSampler();
            sampler.Sample();
            sampler.Dispose();

            var sample = sampler.Sample();

            Assert.False(sample.Available);
            Assert.Equal(0d, sample.ReadBytesPerSecond);
            Assert.Equal(0d, sample.WriteBytesPerSecond);

            // Dispose 之后"属性说可用、方法说不可用"是自相矛盾的状态，必须两边一致：
            // 这条曾经抓到过实现缺陷（Dispose 没有把 IsAvailable 置回 false）。
            Assert.False(sampler.IsAvailable, "Dispose 之后 IsAvailable 必须与 Sample().Available 一致");
        }

        [Fact]
        public void Status_after_dispose_is_self_consistent_and_readable()
        {
            var sampler = new DiskIoSampler();
            sampler.Sample();
            sampler.Dispose();

            // 属性与方法必须给出同一个结论，否则界面会同时看到"可用"和"不可用"
            Assert.False(sampler.IsAvailable);
            Assert.False(sampler.Sample().Available);

            // 这两个属性在 Dispose 之后读取不能抛异常
            _ = sampler.IsAvailable;
            _ = sampler.UnavailableReason;
        }

        [Fact]
        public void Repeated_dispose_and_sample_keep_reporting_unavailable()
        {
            var sampler = new DiskIoSampler();
            sampler.Sample();
            sampler.Dispose();
            sampler.Sample();
            sampler.Dispose();

            Assert.False(sampler.IsAvailable);
            Assert.False(sampler.Sample().Available);
        }

        [Fact]
        public void Dispose_before_any_sample_then_sample_is_unavailable()
        {
            var sampler = new DiskIoSampler();
            sampler.Dispose();

            var sample = sampler.Sample();

            Assert.False(sample.Available);
            AssertHealthySample(sample);
        }

        [Fact]
        public void Dispose_is_idempotent()
        {
            var sampler = new DiskIoSampler();
            sampler.Sample();

            sampler.Dispose();
            sampler.Dispose();
            sampler.Dispose();
        }

        [Fact]
        public void Dispose_without_sampling_does_not_throw()
        {
            var sampler = new DiskIoSampler();
            sampler.Dispose();
        }
    }
}
