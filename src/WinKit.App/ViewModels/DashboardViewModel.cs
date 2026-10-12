using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WinKit.App.Mvvm;
using WinKit.App.Services;
using WinKit.Core.Diagnostics;
using WinKit.Core.Text;
using WinKit.Platform.Metrics;
using WinKit.Platform.Wmi;

namespace WinKit.App.ViewModels
{
    /// <summary>
    /// 磁盘表格的一行。DiskVolume 属于 WinKit.Platform，本页不能改它，
    /// 所以在 ViewModel 里包一层，把 ulong 容量预先格式化成界面直接可用的字符串。
    /// </summary>
    public sealed class DiskRow
    {
        public DiskRow(DiskVolume volume)
        {
            Name = volume.Name ?? string.Empty;
            Label = volume.Label ?? string.Empty;
            FileSystem = volume.FileSystem ?? string.Empty;
            Kind = volume.Kind ?? string.Empty;
            TotalBytes = volume.TotalBytes;
            FreeBytes = volume.FreeBytes;
            UsedPercent = volume.UsedPercent;

            var display = (volume.Display ?? string.Empty).Trim();
            Display = display.Length > 0 ? display : (Name.Length > 0 ? Name : "—");
        }

        private DiskRow()
        {
            Name = string.Empty;
            Label = string.Empty;
            Kind = string.Empty;
            Display = "—";
            FileSystem = "—";
        }

        /// <summary>卷未被读取出来时的占位行，避免界面出现空绑定。</summary>
        public static DiskRow Empty { get; } = new DiskRow();

        public string Name { get; }
        public string Label { get; }
        public string FileSystem { get; }
        public string Kind { get; }
        public ulong TotalBytes { get; }
        public ulong FreeBytes { get; }

        /// <summary>例如 "C:\ (系统)"；无卷标时就是盘符。</summary>
        public string Display { get; }

        public string TotalText => ByteSize.Format((long)TotalBytes, 1);

        public string FreeText => ByteSize.Format((long)FreeBytes, 1);

        public double UsedPercent { get; }

        public string UsedPercentText =>
            UsedPercent.ToString("0.0", CultureInfo.InvariantCulture) + " %";
    }

    /// <summary>
    /// 总览页 ViewModel。
    ///
    /// 线程模型：热路径采样（CPU / 内存 / 网络）是微秒级的，直接在 UI 线程的 DispatcherTimer
    /// 里跑；冷路径（WMI 硬件信息、磁盘枚举）必须走线程池，绝不能在 UI 线程调用。
    /// </summary>
    public sealed class DashboardViewModel : ObservableObject
    {
        private const int DiskRefreshSeconds = 60;

        private readonly CpuSampler _cpu = new CpuSampler();
        private readonly NetworkSampler _network = new NetworkSampler();
        private readonly DispatcherTimer? _timer;
        private readonly DispatcherTimer? _diskTimer;

        private bool _running;
        private bool _firstTick = true;
        private bool _disposed;

        private double _cpuPercent;
        private string _cpuText = "—";
        private string _memoryText = "—";
        private string _memoryDetailText = "—";
        private double _memoryPercent;
        private string _networkText = "↓ —   ↑ —";
        private double _networkDown;
        private double _networkUp;
        private double _primaryDiskUsedPercent;
        private string _primaryDiskName = "—";
        private string _primaryDiskFreeText = "—";
        private string _primaryDiskDetailText = "—";
        private string _lastUpdateText = "尚未采样";
        private string _sampleIntervalText = "1.0 秒";
        private bool _isLoadingInfo;

        private string _diskIoText = "—";
        private string _diskIoDetailText = "—";
        private bool _diskIoAvailable = true;
        private bool _diskIoTickSkipped;

        private SystemInfo? _info;
        private string _osText = "—";
        private string _cpuInfoText = "—";
        private string _memoryInfoText = "—";
        private string _boardText = "—";
        private string _gpuText = "—";
        private string _biosText = "—";
        private string _machineText = "—";
        private string _memoryModulesText = "—";

        public DashboardViewModel()
        {
            RefreshInfoCommand = new RelayCommand(() => _ = LoadInfoAsync(true), () => !IsLoadingInfo);

            CopyInfoCommand = new RelayCommand(() =>
            {
                var text = BuildSystemInfoText();
                if (text.Length == 0) return;
                try
                {
                    Clipboard.SetText(text);
                    LastUpdateText = "系统信息已复制到剪贴板";
                }
                catch (Exception ex)
                {
                    // 剪贴板被其他进程占用是常态，不能让它把界面搞崩
                    LastUpdateText = "复制失败：剪贴板被占用";
                    Log.Warn("复制系统信息失败：" + ex.Message, ex);
                }
            });

            PrimaryDisk = DiskRow.Empty;
            Disks = new ObservableCollection<DiskRow>();

            var intervalMs = ResolveSampleIntervalMs();
            SampleIntervalText = FormatInterval(intervalMs);

            // 首次 Sample 只建立基准并返回 0，先空跑一次，避免头一秒显示 0 造成误判
            _cpu.Sample();
            _network.Sample();

            // 定时器必须在 UI 线程构造；设计器/无 Dispatcher 环境下退化为无定时器（只加载一次静态信息）
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Render, dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(intervalMs),
                };
                _timer.Tick += OnTick;

                _diskTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
                {
                    Interval = TimeSpan.FromSeconds(DiskRefreshSeconds),
                };
                _diskTimer.Tick += OnDiskTick;
            }

            _ = LoadInfoAsync(false);
            _ = LoadDisksAsync();
        }

        #region 事件

        /// <summary>采样事件：cpu 百分比、内存使用率、下行 B/s、上行 B/s。由页面 code-behind 订阅后推入图表。</summary>
        public event Action<double, double, double, double>? MetricsUpdated;

        /// <summary>磁盘读写事件：读 B/s、写 B/s。不要塞进 MetricsUpdated —— 那个签名已被页面订阅。</summary>
        public event Action<double, double>? DiskIoUpdated;

        #endregion

        #region 命令

        public RelayCommand RefreshInfoCommand { get; }

        public RelayCommand CopyInfoCommand { get; }

        #endregion

        #region 热路径指标

        public double CpuPercent
        {
            get => _cpuPercent;
            private set => SetProperty(ref _cpuPercent, value);
        }

        public string CpuText
        {
            get => _cpuText;
            private set => SetProperty(ref _cpuText, value);
        }

        public string MemoryText
        {
            get => _memoryText;
            private set => SetProperty(ref _memoryText, value);
        }

        /// <summary>已用 / 总量。</summary>
        public string MemoryDetailText
        {
            get => _memoryDetailText;
            private set => SetProperty(ref _memoryDetailText, value);
        }

        public double MemoryPercent
        {
            get => _memoryPercent;
            private set => SetProperty(ref _memoryPercent, value);
        }

        public string NetworkText
        {
            get => _networkText;
            private set => SetProperty(ref _networkText, value);
        }

        /// <summary>下行速率（B/s）。</summary>
        public double NetworkDownPerSecond
        {
            get => _networkDown;
            private set => SetProperty(ref _networkDown, value);
        }

        /// <summary>上行速率（B/s）。</summary>
        public double NetworkUpPerSecond
        {
            get => _networkUp;
            private set => SetProperty(ref _networkUp, value);
        }

        #endregion

        #region 磁盘

        public ObservableCollection<DiskRow> Disks { get; }

        /// <summary>系统盘（列表第一项），供"系统盘"卡片直接绑定，永不为 null。</summary>
        public DiskRow PrimaryDisk { get; private set; }

        public string PrimaryDiskName
        {
            get => _primaryDiskName;
            private set => SetProperty(ref _primaryDiskName, value);
        }

        public string PrimaryDiskFreeText
        {
            get => _primaryDiskFreeText;
            private set => SetProperty(ref _primaryDiskFreeText, value);
        }

        public string PrimaryDiskDetailText
        {
            get => _primaryDiskDetailText;
            private set => SetProperty(ref _primaryDiskDetailText, value);
        }

        /// <summary>系统盘已用百分比，直接绑 ProgressBar。</summary>
        public double PrimaryDiskUsedPercent
        {
            get => _primaryDiskUsedPercent;
            private set => SetProperty(ref _primaryDiskUsedPercent, value);
        }

        /// <summary>磁盘读写实时速率，例如 "读 1.3 KB/s　写 24.0 KB/s"；不可用时为 "不可用"。</summary>
        public string DiskIoText
        {
            get => _diskIoText;
            private set => SetProperty(ref _diskIoText, value);
        }

        /// <summary>读写合计或不可用原因（不可用时放 UnavailableReason，绝不显示成 0 B/s）。</summary>
        public string DiskIoDetailText
        {
            get => _diskIoDetailText;
            private set => SetProperty(ref _diskIoDetailText, value);
        }

        /// <summary>性能计数器是否可用。false 时页面隐藏两张曲线图，改显示降级说明。</summary>
        public bool DiskIoAvailable
        {
            get => _diskIoAvailable;
            private set => SetProperty(ref _diskIoAvailable, value);
        }

        #endregion

        #region 系统信息

        public SystemInfo? Info
        {
            get => _info;
            private set
            {
                if (SetProperty(ref _info, value)) UpdateInfoTexts();
            }
        }

        public bool IsLoadingInfo
        {
            get => _isLoadingInfo;
            private set
            {
                if (SetProperty(ref _isLoadingInfo, value)) RelayCommand.RaiseCanExecuteChanged();
            }
        }

        public string OsText
        {
            get => _osText;
            private set => SetProperty(ref _osText, value);
        }

        public string CpuInfoText
        {
            get => _cpuInfoText;
            private set => SetProperty(ref _cpuInfoText, value);
        }

        public string MemoryInfoText
        {
            get => _memoryInfoText;
            private set => SetProperty(ref _memoryInfoText, value);
        }

        public string BoardText
        {
            get => _boardText;
            private set => SetProperty(ref _boardText, value);
        }

        public string GpuText
        {
            get => _gpuText;
            private set => SetProperty(ref _gpuText, value);
        }

        public string BiosText
        {
            get => _biosText;
            private set => SetProperty(ref _biosText, value);
        }

        public string MachineText
        {
            get => _machineText;
            private set => SetProperty(ref _machineText, value);
        }

        /// <summary>内存条明细，一行一条。</summary>
        public string MemoryModulesText
        {
            get => _memoryModulesText;
            private set => SetProperty(ref _memoryModulesText, value);
        }

        #endregion

        #region 状态文本

        public string SampleIntervalText
        {
            get => _sampleIntervalText;
            private set => SetProperty(ref _sampleIntervalText, value);
        }

        public string LastUpdateText
        {
            get => _lastUpdateText;
            private set => SetProperty(ref _lastUpdateText, value);
        }

        #endregion

        #region 生命周期

        /// <summary>开始采样。页面 Loaded 时调用。</summary>
        public void Start()
        {
            if (_disposed) return;
            _running = true;
            _timer?.Start();
            _diskTimer?.Start();

            // 磁盘性能计数器的首次创建要 10~50ms 并持有系统句柄，绝不能放在首帧的 UI 线程上；
            // 这里后台预热一次（不 await：预热失败也没关系，OnTick 会走不可用降级分支）。
            _ = WarmUpDiskIoAsync();

            // 立即采一次，切页回来不用等一个间隔才有数据
            OnTick(this, EventArgs.Empty);
        }

        /// <summary>后台线程预热 <see cref="AppServices.DiskIo"/>，把首次建计数器的开销从首帧移走。</summary>
        private async Task WarmUpDiskIoAsync()
        {
            try
            {
                await TaskRunner.RunAsync(() =>
                {
                    // 首次 Sample 只建立基准并返回 0；这里只关心"计数器能不能建起来"
                    AppServices.DiskIo.Sample();
                    return true;
                }).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Warn("磁盘吞吐预热失败：" + ex.Message, ex);
            }
        }

        /// <summary>停止采样。页面 Unloaded 时必须调用，否则切页后定时器会一直占 CPU。</summary>
        public void Stop()
        {
            _running = false;
            _timer?.Stop();
            _diskTimer?.Stop();
        }

        /// <summary>卸载整个页面时释放采样器与定时器。</summary>
        public void Dispose()
        {
            Stop();
            _disposed = true;
            if (_timer != null) _timer.Tick -= OnTick;
            if (_diskTimer != null) _diskTimer.Tick -= OnDiskTick;
            // 只解绑自己的订阅者；AppServices.DiskIo 的释放由 AppServices 负责，这里不碰
            DiskIoUpdated = null;
            _cpu.Reset();
            _network.Reset();
        }

        #endregion

        #region 采样

        private void OnTick(object? sender, EventArgs e)
        {
            if (!_running || _disposed) return;

            try
            {
                var cpu = _cpu.Sample();
                var memory = MemoryInfo.Query();
                var net = _network.Sample();

                CpuPercent = cpu;
                CpuText = cpu.ToString("0.0", CultureInfo.InvariantCulture) + " %";
                MemoryPercent = memory.UsedPercent;
                MemoryText = memory.UsedPercent.ToString("0.0", CultureInfo.InvariantCulture) + " %";
                MemoryDetailText = ByteSize.Format((long)memory.UsedBytes, 1)
                    + " / " + ByteSize.Format((long)memory.TotalBytes, 1);

                NetworkDownPerSecond = net.ReceivedPerSecond;
                NetworkUpPerSecond = net.SentPerSecond;
                NetworkText = "↓ " + ByteSize.FormatRate(net.ReceivedPerSecond)
                    + "   ↑ " + ByteSize.FormatRate(net.SentPerSecond);

                // 图表开了 AutoScale，这里就不需要手工估算上限
                LastUpdateText = "最后更新 " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    + "　·　采样间隔 " + SampleIntervalText
                    + "　·　" + (AppServices.IsReadOnly ? "只读模式" : "正常模式");

                // 磁盘吞吐：读的是共享内存，微秒级，可以直接在 UI 线程采样
                var disk = AppServices.DiskIo.Sample();
                ApplyDiskIo(disk);

                // 第一拍只显示数值不画点，避免把"基准采样"的 0 变成一根假的谷底
                if (_firstTick)
                {
                    _firstTick = false;
                    return;
                }

                MetricsUpdated?.Invoke(cpu, memory.UsedPercent, net.ReceivedPerSecond, net.SentPerSecond);

                // 不可用时不发事件：页面没有数据可画，也不能把 0 当成"磁盘没活动"
                if (disk.Available)
                {
                    _diskIoTickSkipped = false;
                    DiskIoUpdated?.Invoke(disk.ReadBytesPerSecond, disk.WriteBytesPerSecond);
                }
                else if (!_diskIoTickSkipped)
                {
                    // 运行期从可用降级到不可用：补发一帧，好让页面隐藏曲线并清掉残影
                    _diskIoTickSkipped = true;
                    DiskIoUpdated?.Invoke(0d, 0d);
                }
            }
            catch (Exception ex)
            {
                Log.Error("总览采样失败：" + ex.Message, ex);
            }
        }

        /// <summary>把一次磁盘采样翻译成界面文本；不可用时绝不显示 0 B/s。</summary>
        private void ApplyDiskIo(DiskIoSample sample)
        {
            if (!sample.Available)
            {
                DiskIoAvailable = false;
                DiskIoText = "不可用";
                // UnavailableReason 可能为 null（例如已释放的采样器），这里必须有兜底文案
                var reason = (AppServices.DiskIo.UnavailableReason ?? string.Empty).Trim();
                DiskIoDetailText = reason.Length > 0 ? reason : "磁盘吞吐不可用";
                return;
            }

            DiskIoAvailable = true;
            DiskIoText = "读 " + ByteSize.FormatRate(sample.ReadBytesPerSecond)
                + "　写 " + ByteSize.FormatRate(sample.WriteBytesPerSecond);

            var total = sample.ReadBytesPerSecond + sample.WriteBytesPerSecond;
            DiskIoDetailText = "读写合计 " + ByteSize.FormatRate(total);
        }

        private void OnDiskTick(object? sender, EventArgs e)
        {
            if (!_running || _disposed) return;
            _ = LoadDisksAsync();
        }

        #endregion

        #region 后台加载

        private async Task LoadInfoAsync(bool forceRefresh)
        {
            if (_disposed || IsLoadingInfo) return;

            IsLoadingInfo = true;
            try
            {
                // WMI 是冷路径（50~300ms，多类查询更久），必须离开 UI 线程；provider 自带 10 分钟 TTL 缓存
                var info = await TaskRunner.RunAsync(() => AppServices.SystemInfo.Get(forceRefresh))
                    .ConfigureAwait(true);
                Info = info;
                if (forceRefresh) LastUpdateText = "硬件信息已刷新";
            }
            catch (Exception ex)
            {
                Log.Error("读取硬件信息失败：" + ex.Message, ex);
                LastUpdateText = "读取硬件信息失败：" + ex.Message;
            }
            finally
            {
                IsLoadingInfo = false;
            }
        }

        private async Task LoadDisksAsync()
        {
            if (_disposed) return;

            try
            {
                var volumes = await TaskRunner.RunAsync(() => DiskInfo.Query()).ConfigureAwait(true);
                if (_disposed) return;
                ApplyDisks(volumes);
            }
            catch (Exception ex)
            {
                Log.Warn("枚举磁盘失败：" + ex.Message, ex);
            }
        }

        private void ApplyDisks(IReadOnlyList<DiskVolume> volumes)
        {
            var rows = new List<DiskRow>(volumes.Count);
            foreach (var volume in volumes)
            {
                if (volume == null) continue;
                rows.Add(new DiskRow(volume));
            }

            // 磁盘只有个位数，整体重建比逐项 diff 更简单，也不会漏掉热插拔的卷
            Disks.Clear();
            foreach (var row in rows) Disks.Add(row);

            var primary = rows.Count > 0 ? rows[0] : DiskRow.Empty;
            PrimaryDisk = primary;
            OnPropertyChanged(nameof(PrimaryDisk));

            PrimaryDiskName = primary.Display;
            PrimaryDiskFreeText = "剩余 " + primary.FreeText;
            PrimaryDiskDetailText = "已用 " + primary.UsedPercentText + "　·　共 " + primary.TotalText;
            PrimaryDiskUsedPercent = primary.UsedPercent;
        }

        #endregion

        #region 文本拼装

        private void UpdateInfoTexts()
        {
            var info = _info;
            if (info == null) return;

            OsText = Value(info.OsDisplay);
            CpuInfoText = Value(info.CpuDisplay);
            MemoryInfoText = Value(info.MemoryDisplay);
            BoardText = Value(info.BaseBoardDisplay);
            GpuText = Value(info.GpuDisplay);
            BiosText = Value(info.BiosDisplay);

            var machine = (info.MachineName ?? string.Empty).Trim();
            var user = (info.UserName ?? string.Empty).Trim();
            MachineText = machine.Length == 0
                ? (user.Length == 0 ? "—" : user)
                : (user.Length == 0 ? machine : machine + "　·　" + user);

            var builder = new StringBuilder();
            for (var i = 0; i < info.MemoryModules.Count; i++)
            {
                var module = info.MemoryModules[i];
                if (module == null) continue;
                var display = (module.Display ?? string.Empty).Trim();
                if (display.Length == 0) continue;
                if (builder.Length > 0) builder.Append('\n');
                builder.Append(display);
            }
            MemoryModulesText = builder.Length == 0 ? "—" : builder.ToString();
        }

        private string BuildSystemInfoText()
        {
            var info = _info;
            if (info == null) return string.Empty;

            var builder = new StringBuilder();
            Append(builder, "操作系统", OsText);
            Append(builder, "处理器", CpuInfoText);
            Append(builder, "内存", MemoryInfoText);
            Append(builder, "主板", BoardText);
            Append(builder, "显卡", GpuText);
            Append(builder, "BIOS", BiosText);
            Append(builder, "计算机名", MachineText);
            Append(builder, "内存条", MemoryModulesText);
            Append(builder, "生成时间", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        private static void Append(StringBuilder builder, string label, string? value)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Length == 0 || text == "—") return;
            builder.Append(label).Append("：").Append(text).Append('\n');
        }

        private static string Value(string? text)
        {
            var trimmed = (text ?? string.Empty).Trim();
            return trimmed.Length == 0 ? "—" : trimmed;
        }

        #endregion

        #region 小工具

        private static int ResolveSampleIntervalMs()
        {
            try
            {
                var configured = AppServices.Config?.Current?.SampleIntervalMs ?? 1000;
                // 下限 200ms 与 ConfigStore 的钳制保持一致，避免配置被外部改坏后把 UI 线程拖死
                return configured < 200 ? 200 : configured;
            }
            catch (Exception)
            {
                return 1000;
            }
        }

        private static string FormatInterval(int milliseconds)
        {
            return (milliseconds / 1000d).ToString("0.0", CultureInfo.InvariantCulture) + " 秒";
        }

        #endregion
    }
}
