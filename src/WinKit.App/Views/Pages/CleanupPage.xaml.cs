using System;
using System.Windows;
using System.Windows.Controls;
using WinKit.App.ViewModels;
using WinKit.Core.Diagnostics;

namespace WinKit.App.Views.Pages
{
    /// <summary>
    /// 垃圾清理页。代码后置只做生命周期：装载时刷新隔离区/回收站信息，
    /// 卸载时取消仍在跑的后台任务。扫描、确认、执行全部在 CleanupViewModel 里，
    /// 保证危险操作的判断逻辑不依赖任何控件、可以单独审查。
    /// </summary>
    public partial class CleanupPage : UserControl
    {
        private readonly CleanupViewModel _viewModel;

        public CleanupPage()
        {
            InitializeComponent();

            // 本项目的约定：页面自己准备 ViewModel（主窗口不注入）
            _viewModel = new CleanupViewModel();
            DataContext = _viewModel;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _viewModel.Start();
            }
            catch (Exception ex)
            {
                Log.Error("垃圾清理页启动失败：" + ex.Message, ex);
            }
        }

        /// <summary>
        /// 页面被切走后 UserControl 仍然活着。扫描可能还在后台跑（尤其是 Temp 目录里几十万个文件时），
        /// 必须在这里取消，否则任务会继续往 UI 线程投进度，切页越多堆积越多。
        /// </summary>
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _viewModel.Stop();
            }
            catch (Exception ex)
            {
                Log.Warn("垃圾清理页停止后台任务时出错：" + ex.Message, ex);
            }
        }
    }
}
