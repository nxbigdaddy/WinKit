using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using WinKit.Platform.Native;

namespace WinKit.Platform.Metrics
{
    /// <summary>
    /// CPU 总占用率采样器。基于 GetSystemTimes 的时间差，单次调用是微秒级，
    /// 可以放心每秒采样。注意 kernel 时间里已经包含了 idle 时间。
    /// </summary>
    public sealed class CpuSampler
    {
        private ulong _idle;
        private ulong _kernel;
        private ulong _user;
        private bool _primed;

        /// <summary>返回 0~100 的整机 CPU 占用率；首次调用只建立基准，返回 0。</summary>
        public double Sample()
        {
            NativeMethods.FileTime idle, kernel, user;
            if (!NativeMethods.GetSystemTimes(out idle, out kernel, out user)) return 0d;

            var i = idle.ToUInt64();
            var k = kernel.ToUInt64();
            var u = user.ToUInt64();

            if (!_primed)
            {
                _primed = true;
                _idle = i;
                _kernel = k;
                _user = u;
                return 0d;
            }

            var idleDelta = i - _idle;
            var kernelDelta = k - _kernel;
            var userDelta = u - _user;
            _idle = i;
            _kernel = k;
            _user = u;

            var total = kernelDelta + userDelta;
            if (total == 0) return 0d;

            var busy = total > idleDelta ? total - idleDelta : 0UL;
            var percent = busy * 100d / total;
            return Math.Round(Math.Min(100d, Math.Max(0d, percent)), 1);
        }

        public void Reset() => _primed = false;
    }

    public readonly struct MemoryStatus
    {
        public MemoryStatus(ulong totalBytes, ulong availableBytes, uint loadPercent)
        {
            TotalBytes = totalBytes;
            AvailableBytes = availableBytes;
            LoadPercent = loadPercent;
        }

        public ulong TotalBytes { get; }
        public ulong AvailableBytes { get; }
        public uint LoadPercent { get; }
        public ulong UsedBytes => TotalBytes > AvailableBytes ? TotalBytes - AvailableBytes : 0UL;
        public double UsedPercent => TotalBytes == 0 ? 0d : Math.Round(UsedBytes * 100d / TotalBytes, 1);
    }

    public static class MemoryInfo
    {
        public static MemoryStatus Query()
        {
            var buffer = new NativeMethods.MemoryStatusEx();
            buffer.Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.MemoryStatusEx));
            if (!NativeMethods.GlobalMemoryStatusEx(ref buffer))
            {
                return new MemoryStatus(0, 0, 0);
            }
            return new MemoryStatus(buffer.TotalPhys, buffer.AvailPhys, buffer.MemoryLoad);
        }
    }

    public sealed class DiskVolume
    {
        public string Name { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string FileSystem { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public ulong TotalBytes { get; set; }
        public ulong FreeBytes { get; set; }

        public ulong UsedBytes => TotalBytes > FreeBytes ? TotalBytes - FreeBytes : 0UL;

        public double UsedPercent => TotalBytes == 0 ? 0d : Math.Round(UsedBytes * 100d / TotalBytes, 1);

        public string Display => Label.Length > 0 ? Name + " (" + Label + ")" : Name;
    }

    public static class DiskInfo
    {
        /// <summary>只列固定磁盘与可移动磁盘，跳过光驱/网络盘（后者枚举可能长时间阻塞）。</summary>
        public static IReadOnlyList<DiskVolume> Query()
        {
            var list = new List<DiskVolume>();
            DriveInfo[] drives;
            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch (Exception)
            {
                return list;
            }

            foreach (var drive in drives)
            {
                try
                {
                    var type = drive.DriveType;
                    if (type != DriveType.Fixed && type != DriveType.Removable) continue;
                    if (!drive.IsReady) continue;

                    list.Add(new DiskVolume
                    {
                        Name = drive.Name,
                        Label = drive.VolumeLabel ?? string.Empty,
                        FileSystem = drive.DriveFormat ?? string.Empty,
                        Kind = type == DriveType.Removable ? "可移动" : "固定",
                        TotalBytes = (ulong)drive.TotalSize,
                        FreeBytes = (ulong)drive.TotalFreeSpace,
                    });
                }
                catch (Exception)
                {
                    // 单个卷读取失败（未插入介质、权限不足）跳过即可
                }
            }
            return list;
        }
    }

    public readonly struct NetworkRates
    {
        public NetworkRates(double sentPerSecond, double receivedPerSecond, ulong totalSent, ulong totalReceived)
        {
            SentPerSecond = sentPerSecond;
            ReceivedPerSecond = receivedPerSecond;
            TotalSent = totalSent;
            TotalReceived = totalReceived;
        }

        public double SentPerSecond { get; }
        public double ReceivedPerSecond { get; }
        public ulong TotalSent { get; }
        public ulong TotalReceived { get; }
    }

    /// <summary>所有处于 Up 状态的非回环网卡的流量合计。</summary>
    public sealed class NetworkSampler
    {
        private ulong _sent;
        private ulong _received;
        private long _timestamp;
        private bool _primed;

        public NetworkRates Sample()
        {
            ulong sent = 0, received = 0;
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;

                    var stats = nic.GetIPv4Statistics();
                    sent += (ulong)stats.BytesSent;
                    received += (ulong)stats.BytesReceived;
                }
            }
            catch (Exception)
            {
                // 网卡枚举失败时按 0 处理，不影响其他监控项
            }

            var now = Stopwatch.GetTimestamp();
            if (!_primed)
            {
                _primed = true;
                _sent = sent;
                _received = received;
                _timestamp = now;
                return new NetworkRates(0, 0, sent, received);
            }

            var elapsed = (now - _timestamp) / (double)Stopwatch.Frequency;
            var sentDelta = sent >= _sent ? sent - _sent : 0UL;
            var receivedDelta = received >= _received ? received - _received : 0UL;

            _sent = sent;
            _received = received;
            _timestamp = now;

            if (elapsed <= 0) return new NetworkRates(0, 0, sent, received);

            return new NetworkRates(sentDelta / elapsed, receivedDelta / elapsed, sent, received);
        }

        public void Reset() => _primed = false;
    }
}
