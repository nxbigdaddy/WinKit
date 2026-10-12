using System;
using Microsoft.Win32;
using WinKit.Platform.Native;

namespace WinKit.Platform.Native
{
    /// <summary>
    /// 窗口外观与系统主题探测。放在 Platform 层是因为需要 P/Invoke，
    /// App 层（WPF）只负责把窗口句柄传进来。
    /// </summary>
    public static class WindowAppearance
    {
        /// <summary>
        /// 尝试给窗口标题栏套用深色。成功返回 true；系统不支持时返回 false，
        /// 调用方无需处理 —— 退回系统默认标题栏即可，内容区配色不受影响。
        /// </summary>
        public static bool TryApplyDarkTitleBar(IntPtr hwnd, bool dark)
        {
            if (hwnd == IntPtr.Zero) return false;

            try
            {
                var value = dark ? 1 : 0;
                var hr = NativeMethods.DwmSetWindowAttribute(
                    hwnd, NativeMethods.DwmwaUseImmersiveDarkMode, ref value, sizeof(int));

                if (hr != 0)
                {
                    // Win10 1809~1903 用的属性号是 19
                    hr = NativeMethods.DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
                }
                return hr == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>当前用户是否使用浅色系统主题（读 HKCU\...\Themes\Personalize）。</summary>
        public static bool IsSystemLightTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false))
                {
                    if (key == null) return false;
                    var value = key.GetValue("AppsUseLightTheme");
                    if (value is int i) return i != 0;
                    int parsed;
                    if (value != null && int.TryParse(value.ToString(), out parsed)) return parsed != 0;
                }
            }
            catch (Exception)
            {
                // 读不到就按深色处理
            }
            return false;
        }

        /// <summary>
        /// 修改环境变量（PATH 等）后广播 WM_SETTINGCHANGE，
        /// 让之后启动的新进程能读到新值。超时用 AbortIfHung，避免有窗口卡死时把我们也拖住。
        /// </summary>
        public static bool BroadcastEnvironmentChange()
        {
            try
            {
                IntPtr result;
                NativeMethods.SendMessageTimeout(
                    NativeMethods.HwndBroadcast,
                    NativeMethods.WmSettingChange,
                    IntPtr.Zero,
                    "Environment",
                    NativeMethods.SmtoAbortIfHung,
                    3000,
                    out result);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
