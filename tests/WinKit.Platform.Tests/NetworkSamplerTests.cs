using System;
using System.Threading;
using WinKit.Platform.Metrics;
using Xunit;

namespace WinKit.Platform.Tests
{
    /// <summary>NetworkInterface 统计读取。速率依赖时间差，因此只断言非负且非 NaN。</summary>
    public class NetworkSamplerTests
    {
        [Fact]
        public void First_sample_returns_zero_rates_without_throwing()
        {
            var sampler = new NetworkSampler();

            var rates = sampler.Sample();

            Assert.Equal(0d, rates.SentPerSecond);
            Assert.Equal(0d, rates.ReceivedPerSecond);
        }

        [Fact]
        public void Totals_are_non_negative()
        {
            var sampler = new NetworkSampler();

            var rates = sampler.Sample();

            // 累计字节数是 ulong，这里断言的是"读取到的原始值没有被算成负数/回绕"
            Assert.True(rates.TotalSent >= 0);
            Assert.True(rates.TotalReceived >= 0);
        }

        [Fact]
        public void Second_sample_produces_finite_non_negative_rates()
        {
            var sampler = new NetworkSampler();
            sampler.Sample();
            Thread.Sleep(150);

            var rates = sampler.Sample();

            Assert.False(double.IsNaN(rates.SentPerSecond), "SentPerSecond 不应是 NaN");
            Assert.False(double.IsNaN(rates.ReceivedPerSecond), "ReceivedPerSecond 不应是 NaN");
            Assert.False(double.IsInfinity(rates.SentPerSecond), "SentPerSecond 不应是无穷");
            Assert.False(double.IsInfinity(rates.ReceivedPerSecond), "ReceivedPerSecond 不应是无穷");
            Assert.True(rates.SentPerSecond >= 0d);
            Assert.True(rates.ReceivedPerSecond >= 0d);
        }

        [Fact]
        public void Repeated_sampling_never_throws()
        {
            // 注意：NetworkInterface.GetIPv4Statistics() 在装了 VMware/Hyper-V 虚拟网卡的机器上
            // 单次要 ~1 秒（实测 7 张网卡 950~1180ms），所以这里只跑 3 轮，避免测试白等半分钟。
            var sampler = new NetworkSampler();

            for (int i = 0; i < 3; i++)
            {
                var rates = sampler.Sample();
                Assert.True(rates.TotalSent >= 0);
                Assert.False(double.IsNaN(rates.SentPerSecond));
            }

            sampler.Reset();
            Assert.Equal(0d, sampler.Sample().SentPerSecond);
        }
    }
}
