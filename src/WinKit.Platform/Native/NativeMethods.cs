using System;
using System.Runtime.InteropServices;

namespace WinKit.Platform.Native
{
    /// <summary>
    /// 所有 P/Invoke 集中在这里。热路径（每秒采样）走这些原生调用，
    /// 而不是 WMI —— WMI 单次查询要 50~300ms，做 1 秒采样会把 CPU 吃掉。
    /// </summary>
    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileTime
        {
            public uint Low;
            public uint High;

            public ulong ToUInt64() => ((ulong)High << 32) | Low;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

        [StructLayout(LayoutKind.Sequential)]
        internal struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        /// <summary>Win10 1809 起该属性号为 20；更早的 1903 之前版本为 19。</summary>
        internal const int DwmwaUseImmersiveDarkMode = 20;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("ntdll.dll", PreserveSig = true)]
        internal static extern int NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll", PreserveSig = true)]
        internal static extern int NtResumeProcess(IntPtr processHandle);

        internal const uint WmSettingChange = 0x001A;
        internal static readonly IntPtr HwndBroadcast = new IntPtr(0xffff);

        /// <summary>修改环境变量后广播，让新启动的进程能拿到新值。</summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
            uint flags, uint timeout, out IntPtr result);

        internal const uint SmtoAbortIfHung = 0x0002;

        /// <summary>
        /// SHQUERYRBINFO：回收站的占用统计。字段名与原生结构一一对应
        /// （cbSize / i64Size / i64NumItems）。
        ///
        /// 注意 x64 下的对齐：cbSize 是 4 字节 DWORD，i64Size 需要 8 字节对齐，
        /// 因此中间有 4 字节填充，整个结构体是 24 字节。默认的 LayoutKind.Sequential
        /// 正好符合这个布局，所以不要给它加 Pack —— 加了反而会算错 cbSize。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct ShQueryRbInfo
        {
            public int CbSize;
            public long TotalSize;
            public long NumItems;
        }

        /// <summary>查询回收站占用。<paramref name="rootPath"/> 传 null 表示统计所有驱动器。返回 HRESULT。</summary>
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern int SHQueryRecycleBinW(string? rootPath, ref ShQueryRbInfo info);

        internal const uint SherbNoConfirmation = 0x00000001;
        internal const uint SherbNoProgressUi = 0x00000002;
        internal const uint SherbNoSound = 0x00000004;

        /// <summary>清空回收站。<paramref name="hwnd"/> 传 IntPtr.Zero、<paramref name="rootPath"/> 传 null 表示所有驱动器。</summary>
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? rootPath, uint flags);
    }
}
