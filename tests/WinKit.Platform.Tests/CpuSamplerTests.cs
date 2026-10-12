using System;
using System.Threading;
using WinKit.Platform.Metrics;
using Xunit;

namespace WinKit.Platform.Tests
{
    /// <summary>
    /// CpuSampler 基于 GetSystemTimes 的 P/Invoke 差值，这组用例真正验证原生调用可用。
    /// </summary>
    public class CpuSamplerTests
    {
        [Fact]
        public void First_sample_establishes_baseline_and_returns_zero()
        {
            var sampler = new CpuSampler();

            // 设计行为：没有上一次基准就没有差值可言，必须返回 0 而不是随便给个数
            Assert.Equal(0d, sampler.Sample());
        }

        [Fact]
        public void Second_sample_is_within_zero_to_hundred()
        {
            var sampler = new CpuSampler();
            sampler.Sample();
            Thread.Sleep(120);

            var value = sampler.Sample();

            Assert.InRange(value, 0d, 100d);
        }

        [Fact]
        public void Reset_makes_the_next_sample_a_baseline_again()
        {
            var sampler = new CpuSampler();
            sampler.Sample();
            Thread.Sleep(60);
            Assert.InRange(sampler.Sample(), 0d, 100d);

            sampler.Reset();

            Assert.Equal(0d, sampler.Sample());
        }

        [Fact]
        public void Many_consecutive_samples_never_throw_and_stay_in_range()
        {
            var sampler = new CpuSampler();

            for (int i = 0; i < 200; i++)
            {
                var value = sampler.Sample();
                Assert.InRange(value, 0d, 100d);
                if (i % 20 == 0) Thread.Sleep(5);
            }
        }

        [Fact]
        public void Two_independent_samplers_do_not_share_state()
        {
            var a = new CpuSampler();
            var b = new CpuSampler();

            a.Sample();
            Thread.Sleep(60);

            // b 是全新的实例，第一次调用必须仍然是基准
            Assert.Equal(0d, b.Sample());
            Assert.InRange(a.Sample(), 0d, 100d);
        }
    }
}
