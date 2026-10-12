using System.Windows;
using System.Windows.Controls;
using WinKit.App.ViewModels;
using WinKit.Core.Configuration;

namespace WinKit.App.Views.Pages
{
    /// <summary>
    /// 设置页。改动即时保存（VM 负责），这里只处理"主题三选一"的点击与首次加载刷新。
    /// </summary>
    public partial class SettingsPage : UserControl
    {
        public SettingsPage()
        {
            InitializeComponent();
            DataContext = new SettingsViewModel();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as SettingsViewModel;
            if (vm != null) vm.RefreshReadonlyInfo();
        }

        private void OnThemeDarkClick(object sender, RoutedEventArgs e) => ApplyTheme(ThemeMode.Dark);

        private void OnThemeLightClick(object sender, RoutedEventArgs e) => ApplyTheme(ThemeMode.Light);

        private void OnThemeSystemClick(object sender, RoutedEventArgs e) => ApplyTheme(ThemeMode.System);

        private void ApplyTheme(string theme)
        {
            var vm = DataContext as SettingsViewModel;
            if (vm != null) vm.SelectedTheme = theme;
        }
    }
}
