using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WinKit.App.ViewModels;
using WinKit.Core.Text;

namespace WinKit.App.Views.Pages
{
    /// <summary>
    /// 总览页。只负责把 ViewModel 抛出的采样值喂给折线图（CPU / 内存 / 网络 / 磁盘读 / 磁盘写），
    /// 以及管理采样定时器的生命周期 —— 切页时必须 Stop，否则定时器会一直跑。
    /// </summary>
    public partial class DashboardPage : UserControl
    {
        private DashboardViewModel? _viewModel;

        public DashboardPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 与其他功能页保持一致：页面自己准备 ViewModel（主窗口只负责把页面放进导航宿主）
            var model = DataContext as DashboardViewModel;
            if (model == null)
            {
                model = new DashboardViewModel();
                DataContext = model;
            }

            if (ReferenceEquals(model, _viewModel)) return;

            Detach();
            _viewModel = model;
            _viewModel.MetricsUpdated += OnMetricsUpdated;
            _viewModel.DiskIoUpdated += OnDiskIoUpdated;
            _viewModel.Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Detach();
        }

        private void Detach()
        {
            if (_viewModel == null) return;
            _viewModel.MetricsUpdated -= OnMetricsUpdated;
            _viewModel.DiskIoUpdated -= OnDiskIoUpdated;
            // 页面离开可视树就停表：定时器不停会一直占 CPU，也会拖住整个 Shell
            _viewModel.Stop();
            _viewModel = null;
        }

        private void OnMetricsUpdated(double cpu, double memoryPercent, double netDown, double netUp)
        {
            CpuChart.Push(cpu);
            CpuChart.ValueText = cpu.ToString("0.0", CultureInfo.InvariantCulture) + " %";

            MemoryChart.Push(memoryPercent);
            MemoryChart.ValueText = memoryPercent.ToString("0.0", CultureInfo.InvariantCulture) + " %";

            NetworkChart.Push(netDown);
            NetworkChart.ValueText = "↓ " + ByteSize.FormatRate(netDown) + "  ↑ " + ByteSize.FormatRate(netUp);
        }

        /// <summary>
        /// 磁盘读 / 写各推一张图。磁盘吞吐不可用时 ViewModel 不发这个事件，
        /// 所以这两张图绝不会被 0 值填成一条假的"没有活动"平线。
        /// </summary>
        private void OnDiskIoUpdated(double readBytesPerSecond, double writeBytesPerSecond)
        {
            if (!DiskIoAvailableForCharts())
            {
                // 运行期从可用降级：清掉残影，并让数值文本不要停留在最后一帧
                DiskReadChart.Clear();
                DiskWriteChart.Clear();
                DiskReadChart.ValueText = string.Empty;
                DiskWriteChart.ValueText = string.Empty;
                return;
            }

            DiskReadChart.Push(readBytesPerSecond);
            DiskReadChart.ValueText = "读 " + ByteSize.FormatRate(readBytesPerSecond);

            DiskWriteChart.Push(writeBytesPerSecond);
            DiskWriteChart.ValueText = "写 " + ByteSize.FormatRate(writeBytesPerSecond);
        }

        private bool DiskIoAvailableForCharts()
        {
            return _viewModel == null || _viewModel.DiskIoAvailable;
        }
    }
}
