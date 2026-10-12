using System;
using System.Linq;
using System.Windows;
using WinKit.Core.Configuration;
using WinKit.Core.Diagnostics;
using WinKit.Platform.Native;

namespace WinKit.App.Services
{
    /// <summary>
    /// 主题管理。只替换 MergedDictionaries[0]（Dark.xaml / Light.xaml），
    /// Controls.xaml 里全部用 DynamicResource 引用，因此换主题时界面实时生效。
    /// </summary>
    public sealed class ThemeManager
    {
        private const string DarkSource = "Themes/Dark.xaml";
        private const string LightSource = "Themes/Light.xaml";

        private static readonly ThemeManager Instance = new ThemeManager();

        private ThemeManager()
        {
        }

        public static ThemeManager Current => Instance;

        /// <summary>实际生效的主题（Dark / Light），已把 System 解析成具体值。</summary>
        public string AppliedTheme { get; private set; } = ThemeMode.Dark;

        public event Action<string>? ThemeChanged;

        /// <summary>把配置里的主题（可能是 System）解析成实际主题。</summary>
        public static string ResolveEffectiveTheme(string configured)
        {
            if (string.Equals(configured, ThemeMode.Light, StringComparison.OrdinalIgnoreCase))
            {
                return ThemeMode.Light;
            }
            if (string.Equals(configured, ThemeMode.System, StringComparison.OrdinalIgnoreCase))
            {
                return WindowAppearance.IsSystemLightTheme() ? ThemeMode.Light : ThemeMode.Dark;
            }
            return ThemeMode.Dark;
        }

        public void Apply(string configuredTheme)
        {
            var effective = ResolveEffectiveTheme(configuredTheme);
            var application = Application.Current;
            if (application == null) return;

            try
            {
                var source = effective == ThemeMode.Light ? LightSource : DarkSource;
                var dictionary = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };

                var merged = application.Resources.MergedDictionaries;
                if (merged.Count == 0) merged.Add(dictionary);
                else merged[0] = dictionary;

                AppliedTheme = effective;
                Log.Info("已应用主题：" + effective);
            }
            catch (Exception ex)
            {
                Log.Error("应用主题失败：" + ex.Message, ex);
                return;
            }

            ApplyTitleBarToOpenWindows();

            var handler = ThemeChanged;
            if (handler != null)
            {
                try { handler(effective); } catch (Exception) { }
            }
        }

        public void Toggle()
        {
            var next = AppliedTheme == ThemeMode.Light ? ThemeMode.Dark : ThemeMode.Light;
            Apply(next);

            var config = AppServices.Config;
            if (config != null)
            {
                config.Current.Theme = next;
                config.Save();
            }
        }

        /// <summary>标题栏深色。Win10 1809+ 支持；不支持时静默退回系统标题栏。</summary>
        public void ApplyTitleBarToOpenWindows()
        {
            var application = Application.Current;
            if (application == null) return;

            var dark = AppliedTheme != ThemeMode.Light;
            foreach (Window window in application.Windows.OfType<Window>().ToList())
            {
                try
                {
                    var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    if (handle != IntPtr.Zero) WindowAppearance.TryApplyDarkTitleBar(handle, dark);
                }
                catch (Exception)
                {
                    // 单个窗口失败不影响其他窗口
                }
            }
        }
    }
}
