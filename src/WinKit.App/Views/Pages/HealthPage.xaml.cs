using System;
using System.Windows;
using System.Windows.Controls;
using WinKit.App.ViewModels;
using WinKit.Core.Diagnostics;

namespace WinKit.App.Views.Pages
{
    /// <summary>
    /// 一键体检页。代码后置只做生命周期。
    ///
    /// 两个刻意的决定：
    /// 1) **Loaded 里不自动跑体检**。一次体检要枚举磁盘、采样进程、扫描临时目录，
    ///    进页面就自动扫盘会让"打开看看"变成一次几百毫秒到几秒的磁盘负载。
    ///    体检是用户显式点的动作，所以这里什么都不做。
    /// 2) **没有定时器**。体检是一次性快照，不需要轮询；Unloaded 时只需要放掉
    ///    可能还在跑的后台任务，否则用户切页后任务还在扫盘并往 UI 线程投进度。
    /// </summary>
    public partial class HealthPage : UserControl
    {
        private readonly HealthViewModel _viewModel;

        public HealthPage()
        {
            InitializeComponent();

            // 本项目的约定：页面自己准备 ViewModel（主窗口不注入）
            _viewModel = new HealthViewModel();
            DataContext = _viewModel;

            // 页面被缓存复用：每次重新显示都刷一次 AI 可用性小字（用户可能刚在设置里启用）。
            // 这里**刻意不自动跑体检** —— 见类注释。
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _viewModel.RefreshAiAvailability();
            }
            catch (Exception ex)
            {
                Log.Warn("一键体检页刷新 AI 可用性时出错：" + ex.Message, ex);
            }
        }

        /// <summary>页面被切走后 UserControl 仍然活着，后台体检必须在这里取消。</summary>
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _viewModel.Stop();
            }
            catch (Exception ex)
            {
                Log.Warn("一键体检页停止后台任务时出错：" + ex.Message, ex);
            }
        }
    }
}
