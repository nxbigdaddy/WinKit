using System.Windows;
using System.Windows.Controls;
using WinKit.App.ViewModels;

namespace WinKit.App.Views.Pages
{
    /// <summary>
    /// 撤销中心页面。页面本身不做任何 IO：所有读取/撤销都在 ViewModel 里走线程池，
    /// 这里只负责接线与首次加载。
    /// </summary>
    public partial class JournalPage : UserControl
    {
        public JournalPage()
        {
            InitializeComponent();
            DataContext = new JournalViewModel();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 无定时器：只在页面显示时再刷新一次，保证看到的是最新日志。
            var vm = DataContext as JournalViewModel;
            if (vm == null) return;

            // 页面被缓存复用：顺带刷一次 AI 可用性小字（用户可能刚在设置里启用）
            vm.RefreshAiAvailability();

            if (vm.RefreshCommand.CanExecute(null)) vm.RefreshCommand.Execute(null);
        }
    }
}
