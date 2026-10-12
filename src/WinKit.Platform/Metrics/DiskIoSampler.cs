using System;
using System.Diagnostics;
using WinKit.Core.Diagnostics;

namespace WinKit.Platform.Metrics
{
    public readonly struct DiskIoSample
    {
        public DiskIoSample(double readBytesPerSecond, double writeBytesPerSecond, bool available)
        {
            ReadBytesPerSecond = readBytesPerSecond;
            WriteBytesPerSecond = writeBytesPerSecond;
            Available = available;
        }

        public double ReadBytesPerSecond { get; }
        public double WriteBytesPerSecond { get; }
        public bool Available { get; }
    }

    /// <summary>
    /// 磁盘吞吐采样（读/写 字节每秒），走 Windows 性能计数器 PhysicalDisk(_Total)。
    ///
    /// 为什么用性能计数器而不是 WMI：WMI 的 Win32_PerfFormattedData 单次查询 50~300ms，
    /// 做 1 秒采样会把 CPU 吃掉；性能计数器读的是共享内存，微秒级。
    ///
    /// 为什么不做成 static：性能计数器的创建（首次约 10~50ms）只该付一次，
    /// 而且它持有系统句柄需要释放。
    ///
    /// 可用性：某些机器上性能计数器库损坏或被策略禁用，此时 <see cref="IsAvailable"/> 为 false、
    /// 界面应显示"不可用"而不是把 0 当成"磁盘没活动"。
    /// </summary>
    public sealed class DiskIoSampler : IDisposable
    {
        private const string CategoryName = "PhysicalDisk";
        private const string TotalInstance = "_Total";

        private PerformanceCounter? _read;
        private PerformanceCounter? _write;
        private bool _primed;
        private bool _initialized;
        private bool _disposed;

        /// <summary>性能计数器是否可用。首次调用 <see cref="Sample"/> 后才确定。</summary>
        public bool IsAvailable { get; private set; }

        /// <summary>不可用时的原因（面向界面展示）。</summary>
        public string? UnavailableReason { get; private set; }

        public DiskIoSample Sample()
        {
            if (_disposed) return new DiskIoSample(0, 0, false);

            if (!_initialized)
            {
                _initialized = true;
                try
                {
                    _read = new PerformanceCounter(CategoryName, "Disk Read Bytes/sec", TotalInstance, readOnly: true);
                    _write = new PerformanceCounter(CategoryName, "Disk Write Bytes/sec", TotalInstance, readOnly: true);

                    // 第一次 NextValue 只是建立基准，返回 0
                    _read.NextValue();
                    _write.NextValue();
                    _primed = true;
                    IsAvailable = true;
                }
                catch (Exception ex)
                {
                    IsAvailable = false;
                    UnavailableReason = "磁盘吞吐不可用：" + ex.Message;
                    Log.Warn(UnavailableReason);
                    DisposeCounters();
                    return new DiskIoSample(0, 0, false);
                }
            }

            if (!IsAvailable || _read == null || _write == null)
            {
                return new DiskIoSample(0, 0, false);
            }

            try
            {
                var read = _read.NextValue();
                var write = _write.NextValue();

                if (!_primed)
                {
                    _primed = true;
                    return new DiskIoSample(0, 0, true);
                }

                return new DiskIoSample(
                    Math.Max(0d, read),
                    Math.Max(0d, write),
                    true);
            }
            catch (Exception ex)
            {
                // 计数器在运行期失效（例如被禁用）时降级，而不是抛给界面
                IsAvailable = false;
                UnavailableReason = "磁盘吞吐读取失败：" + ex.Message;
                Log.Warn(UnavailableReason);
                DisposeCounters();
                return new DiskIoSample(0, 0, false);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 两个字段必须一起翻，否则外部会拿到自相矛盾的状态：
            // 只看 IsAvailable 说"不可用"，去看 UnavailableReason 却是空的 ——
            // 界面就只能显示"磁盘吞吐不可用"而给不出任何理由。
            // 测试里对这条不变式有明确断言（可用时原因必须为 null，不可用时原因必须非空）。
            IsAvailable = false;
            if (string.IsNullOrWhiteSpace(UnavailableReason))
            {
                UnavailableReason = "磁盘吞吐采样器已释放";
            }

            DisposeCounters();
        }

        private void DisposeCounters()
        {
            try { _read?.Dispose(); } catch (Exception) { }
            try { _write?.Dispose(); } catch (Exception) { }
            _read = null;
            _write = null;
        }
    }
}
