using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinKit.App.ViewModels;
using WinKit.Core.Diagnostics;

namespace WinKit.App.Views.Pages
{
    /// <summary>
    /// 进程管理页。代码后置只做三件事：生命周期（Start/Stop）、把 DataGrid 的表头排序转给 ViewModel、
    /// 双击查看详情。采样、过滤、结束进程全部在 ViewModel 里，保证可测且不依赖控件。
    /// </summary>
    public partial class ProcessPage : UserControl
    {
        private ProcessViewModel? _viewModel;

        public ProcessPage()
        {
            InitializeComponent();

            _viewModel = new ProcessViewModel();
            DataContext = _viewModel;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _viewModel?.Start();
            }
            catch (Exception ex)
            {
                Log.Error("进程管理页启动失败：" + ex.Message, ex);
            }
        }

        /// <summary>
        /// 必须停掉自动刷新定时器。页面被切走后 UserControl 还活着，
        /// 不停就会继续每 2 秒后台采样一次并往 UI 线程投递，切页越多泄漏越多。
        /// </summary>
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _viewModel?.Stop();
            }
            catch (Exception ex)
            {
                Log.Warn("进程管理页停止刷新时出错：" + ex.Message, ex);
            }
        }

        /// <summary>
        /// 点表头排序：ViewModel 自己掌握排序状态（按 PID/CPU/内存等重排 _all 的副本），
        /// 所以这里把事件标记为已处理，不让 DataGrid 再走它默认的 ICollectionView 排序 ——
        /// 那会和每次刷新重建 Rows 的做法打架，表现为"点了排序没反应"。
        /// </summary>
        private void OnDataGridSorting(object sender, DataGridSortingEventArgs e)
        {
            if (e.Column == null) return;

            var member = e.Column.SortMemberPath;
            if (string.IsNullOrEmpty(member)) return;

            // 第一次点某列按升序，再次点同一列切换升/降序
            var direction = e.Column.SortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;

            e.Column.SortDirection = direction;
            e.Handled = true;

            _viewModel?.ApplySort(member, direction);
        }

        /// <summary>
        /// 双击行 = 弹出该进程的详情。做成显式弹窗而不是"双击复制"，是为了不做用户看不见的副作用：
        /// 双击滚动条、列分隔线、表头时都会被 MouseDoubleClick 捕获，只有确实点在 DataGridRow 上才响应。
        /// </summary>
        private void OnDataGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!(e.OriginalSource is DependencyObject source)) return;
            if (FindAncestor<DataGridRow>(source) == null) return;

            var row = _viewModel == null ? null : _viewModel.SelectedRow;
            if (row == null) return;

            var text = "进程名称：" + row.DisplayName + "\r\n"
                + "可执行文件：" + row.Name + "\r\n"
                + "进程 PID：" + row.Pid + "\r\n"
                + "文件路径：" + row.Path + "\r\n"
                + "启动时间：" + row.StartTimeText + "\r\n"
                + "CPU 占用：" + row.CpuText + "\r\n"
                + "内存占用：" + row.MemoryText + "\r\n"
                + "线程数：" + row.Threads + "\r\n"
                + "句柄数：" + row.Handles
                + (row.AccessDenied ? "\r\n\r\n注意：该进程信息受权限限制，部分字段不可读。" : string.Empty);

            MessageBox.Show(text, "进程详情", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
        {
            while (node != null)
            {
                if (node is T match) return match;

                // 文本单元格里可能点到 Run/ContentElement，这类对象不在 Visual 树上，
                // VisualTreeHelper.GetParent 会直接抛异常，所以这里必须先判类型。
                if (!(node is System.Windows.Media.Visual) && !(node is System.Windows.Media.Media3D.Visual3D)) return null;
                node = System.Windows.Media.VisualTreeHelper.GetParent(node);
            }
            return null;
        }
    }
}
