using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using WinKit.App.Services;
using WinKit.App.ViewModels;
using WinKit.Core.Diagnostics;

namespace WinKit.App.Views
{
    /// <summary>
    /// 主窗口只保留纯粹的"视图"职责：XAML 装不下的那点鼠标/键盘行为。
    /// 窗口尺寸的持久化统一由 App.OnExit 负责（那里能同时拿到配置与窗口），这里不重复做。
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            PreviewKeyDown += OnPreviewKeyDown;
            DataContextChanged += OnDataContextChanged;
        }

        /// <summary>句柄就绪后立刻把标题栏刷成与主题一致（深色主题下白标题栏很刺眼）。</summary>
        private void OnSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                ThemeManager.Current.ApplyTitleBarToOpenWindows();
            }
            catch (Exception ex)
            {
                Log.Warn("应用标题栏主题失败：" + ex.Message);
            }
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is not MainViewModel viewModel) return;

            // 配置里记的是"上次最大化"，WindowState 不能用单纯的布尔绑定表达，这里补一刀
            if (viewModel.WindowMaximized) WindowState = WindowState.Maximized;

            // PageHost 的 DataContext 被显式清空（防止页面继承 MainViewModel），
            // 所以它的 Content 必须用显式 Source 绑定到外壳 VM，不能靠继承或 FindAncestor。
            PageHost.SetBinding(ContentControl.ContentProperty, new Binding("CurrentView") { Source = viewModel });
        }

        /// <summary>Ctrl+K 聚焦搜索框。</summary>
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.K) return;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;

            FocusSearchBox();
            e.Handled = true;
        }

        /// <summary>供快捷键与将来的命令面板调用。</summary>
        public void FocusSearchBox()
        {
            if (FindName("SearchBox") is TextBox box)
            {
                box.Focus();
                box.SelectAll();
            }
        }
    }
}
