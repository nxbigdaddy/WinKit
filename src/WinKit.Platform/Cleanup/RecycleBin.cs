using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using WinKit.Core.Diagnostics;
using WinKit.Platform.Native;

namespace WinKit.Platform.Cleanup
{
    /// <summary>
    /// Windows 回收站。只做两件事：查当前占用、清空。
    ///
    /// 为什么走 shell32 而不是自己遍历各卷的 $Recycle.Bin：回收站的内部布局
    /// （每卷每个 SID 一个目录、$I 元数据与 $R 数据成对出现）是未公开实现，
    /// 自己删会留下孤立元数据，甚至把别的用户的回收站算错。
    /// </summary>
    public static class RecycleBin
    {
        private const int S_OK = 0;

        /// <summary>
        /// 回收站占用的总字节数（所有驱动器）。查询失败返回 -1 ——
        /// 用 -1 而不是 0，是为了让界面能区分"回收站是空的"和"查不到"。
        /// </summary>
        public static long GetSizeBytes()
        {
            try
            {
                var info = new NativeMethods.ShQueryRbInfo();
                // cbSize 必须等于结构体自身的字节数（x64 下是 24，含 4 字节填充），
                // 填错 shell32 直接返回 E_INVALIDARG，所以用 Marshal.SizeOf 而不是硬编码常量。
                info.CbSize = Marshal.SizeOf(typeof(NativeMethods.ShQueryRbInfo));

                var hr = NativeMethods.SHQueryRecycleBinW(null, ref info);
                if (hr != S_OK)
                {
                    Log.Debug("查询回收站大小失败，HRESULT=0x" + hr.ToString("X8"));
                    return -1;
                }

                return info.TotalSize;
            }
            catch (Exception ex)
            {
                Log.Warn("查询回收站大小异常：" + ex.Message);
                return -1;
            }
        }

        /// <summary>
        /// 清空所有驱动器的回收站。失败不抛异常，把中文原因写进 <paramref name="error"/>。
        /// 这是不可撤销的 L3 操作，调用方必须先经过危险分级确认。
        /// </summary>
        public static bool Empty(out string? error)
        {
            error = null;

            try
            {
                // 三个标志都打开：不弹确认框、不显示进度条、不响声音 ——
                // 确认流程由 WinKit 自己的对话框负责，这里再弹一次会让用户看到两层确认。
                var flags = NativeMethods.SherbNoConfirmation
                    | NativeMethods.SherbNoProgressUi
                    | NativeMethods.SherbNoSound;

                var hr = NativeMethods.SHEmptyRecycleBinW(IntPtr.Zero, null, flags);
                if (hr == S_OK)
                {
                    Log.Info("已清空回收站");
                    return true;
                }

                error = "清空回收站失败（错误码 0x" + hr.ToString("X8") + "）"
                    + Describe(hr);
                Log.Warn(error);
                return false;
            }
            catch (Exception ex)
            {
                error = "清空回收站失败：" + ex.Message;
                Log.Error(error, ex);
                return false;
            }
        }

        /// <summary>把 HRESULT 翻成系统自带的中文说明；翻不出来就只保留错误码。</summary>
        private static string Describe(int hr)
        {
            try
            {
                var message = new Win32Exception(hr).Message;
                return string.IsNullOrWhiteSpace(message) ? string.Empty : "：" + message;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
