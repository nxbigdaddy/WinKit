using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WinKit.App.Ai;
using WinKit.App.Mvvm;
using WinKit.App.Services;
using WinKit.Core.Ai;
using WinKit.Core.Diagnostics;
using WinKit.Core.Journal;
using WinKit.Core.Text;
using WinKit.Platform.Diagnostics;
using WinKit.Platform.Metrics;
using WinKit.Platform.Wmi;

namespace WinKit.App.ViewModels
{
    /// <summary>
    /// "有文本"判定，供本页的命令行卡片使用（空字符串 → false）。
    /// 放在这个类库里而不是每个页面各写一个：转换器必须能在 XAML 里按类型找到，不依赖 App 级资源字典，
    /// 这样页面单独预览/单元测试时也能正常解析。
    /// 之所以自己实现而不用 Convert.ToString(value).Length：后者对 null 会返回空串，语义不够直白。
    /// </summary>
    public sealed class HasTextToBooleanConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var text = value as string;
            return !string.IsNullOrEmpty(text) && text!.Trim().Length > 0;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException("HasTextToBooleanConverter 只支持单向绑定。");
    }

    /// <summary>
    /// 进程管理页的 ViewModel。
    ///
    /// 三条硬约束都落在这里：
    /// 1) UI 线程绝不阻塞 —— 采样走 TaskRunner.RunAsync，命令行走 WMI 冷路径后台线程，并且两条路径都有重入/竞态保护；
    /// 2) 危险操作先预览再确认 —— 结束进程前必须弹确认框，系统核心进程直接拒绝执行；
    /// 3) 只读审计模式下所有写操作直接返回，按钮同时禁用。
    /// </summary>
    public sealed class ProcessViewModel : ObservableObject
    {
        /// <summary>命中即拒绝执行：结束这些进程会蓝屏/立即重启/丢失当前登录会话。</summary>
        private static readonly string[] DangerousProcessNames =
        {
            "csrss", "wininit", "winlogon", "services", "lsass",
            "smss", "System", "Idle", "Registry", "MemCompression"
        };

        private const string KillTaskId = "process.kill";
        private const string KillTreeTaskId = "process.kill-tree";

        /// <summary>确认框里最多列出多少个后代进程。一个进程树可能有几百个后代，全列出来会撑爆对话框，
        /// 而"看得到前几个是谁"已经足够让用户判断自己是不是点错了。</summary>
        private const int MaxTreePreviewItems = 10;

        /// <summary>失败明细里最多写出多少个进程，弹窗和日志都不该被刷屏。</summary>
        private const int MaxTreeFailureNames = 5;

        private const int MinRefreshIntervalMs = 500;
        private const int MaxRefreshIntervalMs = 60000;

        // ---- AI 助手（可选联网）----

        private const string AiTitle = "用 AI 解释";

        /// <summary>可用时按钮旁边那行小字：必须如实说明"会联网 + 发送前先给你看"。</summary>
        private const string AiAvailableHint = "会联网：将先显示要发送的内容，确认后才联网。";

        private const string AiEnableHint = "可在「设置 → AI 助手」启用并填写 API Key。";

        /// <summary>结果卡片的默认标题。</summary>
        private const string AiReplyHeader = "AI 解释（来自 DeepSeek，仅供参考）";

        private readonly ProcessSampler _sampler = new ProcessSampler();
        private readonly DispatcherTimer _timer;
        private readonly List<ProcessRow> _all = new List<ProcessRow>();

        private string _aiReplyText = string.Empty;
        private string _aiReplySubject = AiReplyHeader;
        private bool _hasAiReply;

        private DispatcherTimer? _warmupTimer;
        private CancellationTokenSource? _commandLineCts;
        private ProcessRow? _selectedRow;
        private int _selectedPid = -1;
        private bool _selectionEstablished;
        private bool _sampling;
        private bool _killingTree;
        private bool _loaded;
        private bool _suppressFilter;
        private int _commandLineRequestId;
        private string _searchText = string.Empty;
        private bool _autoRefresh = true;
        private int _refreshIntervalMs = 2000;
        private string _statusText = "正在读取进程列表…";
        private string _lastRefreshText = "—";
        private string? _selectedCommandLine;
        private bool _isLoadingCommandLine;

        public ProcessViewModel()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(_refreshIntervalMs) };
            _timer.Tick += OnTimerTick;

            RefreshCommand = new RelayCommand(() => { _ = RefreshAsync(); });
            KillProcessCommand = new RelayCommand(KillProcess, CanKillProcess);
            KillTreeCommand = new RelayCommand(KillTree, CanKillTree);
            CopyPathCommand = new RelayCommand(CopyPath, CanCopyPath);

            // AI 入口：CanExecute 里**故意不判断 AiLauncher.IsAvailable** ——
            // 不可用时按钮保持可见、可点，点下去只弹"为什么不能用"。
            ExplainWithAiCommand = new RelayCommand(ExplainWithAi);
            CopyAiReplyCommand = new RelayCommand(CopyAiReply);
            ClearAiReplyCommand = new RelayCommand(ClearAiReply);
        }

        /// <summary>界面上的进程行。包装 ProcessSample 是为了在这里一次性把显示文本格式化好，避免上千行绑定转换器。</summary>
        public sealed class ProcessRow
        {
            public ProcessRow(ProcessSample source)
            {
                Pid = source.Pid;
                Name = source.Name;
                DisplayName = source.DisplayName;
                CpuPercent = source.CpuPercent;
                CpuText = source.CpuPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%";
                MemoryBytes = source.WorkingSet;
                MemoryText = ByteSize.Format(source.WorkingSet);
                Threads = source.Threads;
                Handles = source.Handles;
                StartTimeText = source.StartTimeText;
                Path = source.PathText;
                Description = source.Description;
                AccessDenied = source.AccessDenied;
            }

            public int Pid { get; }
            public string Name { get; }
            public string DisplayName { get; }
            public double CpuPercent { get; }
            public string CpuText { get; }
            public long MemoryBytes { get; }
            public string MemoryText { get; }
            public int Threads { get; }
            public int Handles { get; }
            public string StartTimeText { get; }
            public string Path { get; }
            public string? Description { get; }
            public bool AccessDenied { get; }
            public bool IsSelected { get; set; }
        }

        public ObservableCollection<ProcessRow> Rows { get; } = new ObservableCollection<ProcessRow>();

        public RelayCommand RefreshCommand { get; }
        public RelayCommand KillProcessCommand { get; }
        public RelayCommand KillTreeCommand { get; }
        public RelayCommand CopyPathCommand { get; }

        public RelayCommand ExplainWithAiCommand { get; }
        public RelayCommand CopyAiReplyCommand { get; }
        public RelayCommand ClearAiReplyCommand { get; }

        public ProcessRow? SelectedRow
        {
            get => _selectedRow;
            set
            {
                if (ReferenceEquals(_selectedRow, value)) return;
                if (!SetProperty(ref _selectedRow, value)) return;
                if (value != null)
                {
                    _selectedPid = value.Pid;
                    _selectionEstablished = true;
                }
                OnPropertyChanged(nameof(HasSelection));
                RelayCommand.RaiseCanExecuteChanged();
                RequestCommandLine();
            }
        }

        public bool HasSelection => _selectedRow != null;

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (!SetProperty(ref _searchText, value ?? string.Empty)) return;
                ApplyFilter();
            }
        }

        public bool AutoRefresh
        {
            get => _autoRefresh;
            set
            {
                if (!SetProperty(ref _autoRefresh, value)) return;
                UpdateTimerState();
            }
        }

        /// <summary>自动刷新间隔。赋值时自动夹到 500~60000ms，避免误填 0 或负值把 CPU 打满。</summary>
        public int RefreshIntervalMs
        {
            get => _refreshIntervalMs;
            set
            {
                var clamped = value < MinRefreshIntervalMs ? MinRefreshIntervalMs
                    : value > MaxRefreshIntervalMs ? MaxRefreshIntervalMs
                    : value;
                if (!SetProperty(ref _refreshIntervalMs, clamped)) return;
                _timer.Interval = TimeSpan.FromMilliseconds(clamped);
            }
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        public string LastRefreshText
        {
            get => _lastRefreshText;
            private set => SetProperty(ref _lastRefreshText, value);
        }

        public string? SelectedCommandLine
        {
            get => _selectedCommandLine;
            private set => SetProperty(ref _selectedCommandLine, value);
        }

        public bool IsLoadingCommandLine
        {
            get => _isLoadingCommandLine;
            private set => SetProperty(ref _isLoadingCommandLine, value);
        }

        /// <summary>只读审计模式。界面用它显示警告条并禁用写操作按钮。</summary>
        public bool IsReadOnly => AppServices.IsReadOnly;

        public string SortMember { get; private set; } = string.Empty;

        public ListSortDirection SortDirection { get; private set; } = ListSortDirection.Ascending;

        /// <summary>页面装载时调用。首次连续采两次样，第二次才有有意义的 CPU 数值。</summary>
        public void Start()
        {
            // 页面被缓存复用：每次重新显示都刷一次 AI 可用性小字（用户可能刚在设置里启用）
            RefreshAiAvailability();

            if (_loaded) return;
            _loaded = true;

            // 立刻采一次：把 CPU 基线（TotalProcessorTime）建立起来，用户不用干等 500ms 才看到列表
            _ = RefreshAsync();

            // 500ms 后再采一次，这一次的 CPU 差值才有意义；故意不设 2s，早一点让 CPU 列"活"起来
            _warmupTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            _warmupTimer.Tick += OnWarmupTick;
            _warmupTimer.Start();
        }

        /// <summary>页面卸载必须调用，否则切页后定时器会一直往 UI 线程投递采样。</summary>
        public void Stop()
        {
            _loaded = false;
            _timer.Stop();
            DisposeWarmupTimer();
            CancelCommandLineRequest();
            SelectedCommandLine = null;
            IsLoadingCommandLine = false;
            StatusText = "已暂停（离开页面）";
        }

        /// <summary>表头点击排序。member 为 XAML 里的 SortMemberPath。</summary>
        public void ApplySort(string member, ListSortDirection direction)
        {
            if (string.IsNullOrWhiteSpace(member)) return;
            SortMember = member;
            SortDirection = direction;
            ApplyFilter();
        }

        private void OnWarmupTick(object? sender, EventArgs e)
        {
            DisposeWarmupTimer();
            if (!_loaded) return;
            _ = RefreshAsync();
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            // 自动刷新被关掉时（或页面已卸载）直接跳过这一拍，不再发起采样
            if (!_loaded || !AutoRefresh) return;
            _ = RefreshAsync();
        }

        private void UpdateTimerState()
        {
            if (_loaded && AutoRefresh) _timer.Start();
            else _timer.Stop();
        }

        private void DisposeWarmupTimer()
        {
            var timer = _warmupTimer;
            if (timer == null) return;
            _warmupTimer = null;
            timer.Stop();
            timer.Tick -= OnWarmupTick;
        }

        private async Task RefreshAsync()
        {
            // 重入保护：上一轮还没回来就跳过这一拍。采样本身要遍历上百个进程，比刷新间隔慢时会一直堆积。
            if (_sampling || !_loaded) return;
            _sampling = true;
            try
            {
                var samples = await TaskRunner.RunAsync(() => _sampler.Sample()).ConfigureAwait(true);

                // 采样期间用户可能已经切走页面
                if (!_loaded) return;

                _all.Clear();
                foreach (var sample in samples) _all.Add(new ProcessRow(sample));
                ApplyFilter();
                LastRefreshText = "最后刷新：" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                Log.Warn("进程采样失败：" + ex.Message, ex);
                StatusText = "采样失败：" + ex.Message;
            }
            finally
            {
                _sampling = false;
            }
        }

        /// <summary>
        /// 用采样结果重建 Rows。之所以整体替换而不是逐条增量更新：
        /// 增量更新要做逐行 diff，代码复杂度高，而 300 行规模下整体重建的代价可以忽略。
        /// 排序在副本上做，_all 保持采样顺序，清空搜索词后能回到"任务管理器"式的原始顺序。
        /// </summary>
        private void ApplyFilter()
        {
            if (_suppressFilter) return;

            _suppressFilter = true;
            try
            {
                // 关键：Rows.Clear() 会让 DataGrid 把 SelectedItem 置空，_selectedRow 随之变成 null，
                // 所以选中项的 PID 必须单独记一份，不能依赖 _selectedRow。
                var keyword = _searchText == null ? string.Empty : _searchText.Trim();

                var filtered = new List<ProcessRow>(_all.Count);
                foreach (var row in SortedSnapshot())
                {
                    if (Matches(row, keyword)) filtered.Add(row);
                }

                Rows.Clear();
                foreach (var row in filtered) Rows.Add(row);

                RestoreSelection();

                StatusText = string.Equals(keyword, string.Empty, StringComparison.Ordinal)
                    ? "共 " + _all.Count + " 个进程，显示 " + Rows.Count + " 个"
                    : "共 " + _all.Count + " 个进程，匹配 \"" + keyword + "\" 的 " + Rows.Count + " 个";
            }
            finally
            {
                _suppressFilter = false;
            }
        }

        /// <summary>排序在副本上做，_all 保持采样顺序，清空搜索词后能回到"任务管理器"式的原始顺序。</summary>
        private IEnumerable<ProcessRow> SortedSnapshot()
        {
            var list = new List<ProcessRow>(_all);
            if (string.IsNullOrEmpty(SortMember)) return list;

            Comparison<ProcessRow> comparison;
            switch (SortMember)
            {
                case "Pid":
                    comparison = (a, b) => a.Pid.CompareTo(b.Pid);
                    break;
                case "DisplayName":
                    comparison = (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
                    break;
                case "CpuText":
                    comparison = (a, b) => a.CpuPercent.CompareTo(b.CpuPercent);
                    break;
                case "MemoryText":
                    comparison = (a, b) => a.MemoryBytes.CompareTo(b.MemoryBytes);
                    break;
                case "Threads":
                    comparison = (a, b) => a.Threads.CompareTo(b.Threads);
                    break;
                case "Handles":
                    comparison = (a, b) => a.Handles.CompareTo(b.Handles);
                    break;
                case "StartTimeText":
                    // 拿不到启动时间（系统进程）的排在最后，而不是当成 DateTime.MinValue 排到最前
                    comparison = (a, b) => string.Compare(a.StartTimeText, b.StartTimeText, StringComparison.Ordinal);
                    break;
                case "Path":
                    comparison = (a, b) => string.Compare(a.Path, b.Path, StringComparison.CurrentCultureIgnoreCase);
                    break;
                default:
                    // 未知列名不排序，保持采样返回的顺序
                    return list;
            }

            list.Sort(comparison);
            if (SortDirection == ListSortDirection.Descending) list.Reverse();
            return list;
        }

        private static bool Matches(ProcessRow row, string keyword)
        {
            if (string.Equals(keyword, string.Empty, StringComparison.Ordinal)) return true;

            // 不用 string.Contains(char)：net48 上没有那个重载，这里统一走 IndexOf + OrdinalIgnoreCase
            if (!string.IsNullOrEmpty(row.Name)
                && row.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (!string.IsNullOrEmpty(row.DisplayName)
                && row.DisplayName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return row.Pid.ToString(CultureInfo.InvariantCulture)
                .IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 重建 Rows 后重新挑选中项：优先按 PID 还原，PID 消失则按"进程名 + 路径"退化匹配，
        /// 再不行就回到第一行。首次装载时自动选中第一行，让右侧详情卡片一开始就有内容。
        /// </summary>
        private void RestoreSelection()
        {
            var previousName = _selectedRow == null ? null : _selectedRow.Name;
            var previousPath = _selectedRow == null ? null : _selectedRow.Path;
            var previousPid = _selectedPid >= 0 ? _selectedPid : (_selectedRow == null ? -1 : _selectedRow.Pid);

            ProcessRow? match = null;

            if (previousPid >= 0)
            {
                foreach (var row in Rows)
                {
                    if (row.Pid == previousPid)
                    {
                        match = row;
                        break;
                    }
                }
            }

            // 进程重启后 PID 会变（或 PID 被复用），退化到"同名同路径"匹配
            if (match == null && !string.IsNullOrEmpty(previousName))
            {
                foreach (var row in Rows)
                {
                    if (string.Equals(row.Name, previousName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(row.Path, previousPath, StringComparison.Ordinal))
                    {
                        match = row;
                        break;
                    }
                }
            }

            if (match == null && !_selectionEstablished && Rows.Count > 0) match = Rows[0];

            SelectedRow = match;
        }

        private void RequestCommandLine()
        {
            CancelCommandLineRequest();

            var row = _selectedRow;
            if (!_loaded || row == null)
            {
                SelectedCommandLine = null;
                IsLoadingCommandLine = false;
                return;
            }

            var pid = row.Pid;
            var requestId = ++_commandLineRequestId;
            var cts = new CancellationTokenSource();
            _commandLineCts = cts;
            IsLoadingCommandLine = true;
            SelectedCommandLine = null;

            var token = cts.Token;
            Task<string?> task;
            try
            {
                // WMI 是冷路径（单次 50~300ms），必须在后台线程，绝不能放在选中行变更的 UI 线程里
                task = TaskRunner.RunAsync(() => QueryCommandLine(pid, token));
            }
            catch (Exception ex)
            {
                Log.Warn("启动命令行查询任务失败：" + ex.Message, ex);
                IsLoadingCommandLine = false;
                return;
            }

            task.ContinueWith(
                t =>
                {
                    string? text;
                    if (t.IsCanceled)
                    {
                        text = null;
                    }
                    else if (t.IsFaulted)
                    {
                        var reason = t.Exception == null ? "未知错误" : t.Exception.GetBaseException().Message;
                        Log.Warn("读取命令行失败（PID " + pid + "）：" + reason);
                        text = "读取失败：" + reason;
                    }
                    else
                    {
                        text = t.Result;
                    }

                    // 回到 UI 线程再改属性；用户可能已经换了选中行，过期结果直接丢弃
                    var dispatcher = Application.Current == null ? null : Application.Current.Dispatcher;
                    if (dispatcher == null) return;
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (requestId != _commandLineRequestId) return; // 过期结果
                        IsLoadingCommandLine = false;
                        SelectedCommandLine = text;
                    }));
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }

        private static string? QueryCommandLine(int pid, CancellationToken token)
        {
            if (token.IsCancellationRequested) return null;
            try
            {
                var commandLine = WmiQuery.GetProcessCommandLine(pid);
                return string.IsNullOrWhiteSpace(commandLine) ? null : commandLine;
            }
            catch (Exception ex)
            {
                Log.Warn("WMI 读取命令行失败（PID " + pid + "）：" + ex.Message);
                return null;
            }
        }

        private void CancelCommandLineRequest()
        {
            _commandLineRequestId++;
            var cts = _commandLineCts;
            if (cts == null) return;
            _commandLineCts = null;
            try { cts.Cancel(); }
            catch (Exception) { }
            try { cts.Dispose(); }
            catch (Exception) { }
        }

        private bool CanKillProcess() => !IsReadOnly && _selectedRow != null;

        /// <summary>
        /// 与 KillProcessCommand 同样的两个前提（有选中行、非只读审计模式），再额外要求"非执行中"：
        /// 取进程树要查一次 WMI（100~300ms），这段空档里连点两下会并发走两遍确认与执行，是真实的重复执行风险，
        /// 所以这里必须把它挡在 CanExecute 上，而不是只在方法体里 early return。
        /// </summary>
        private bool CanKillTree() => !IsReadOnly && _selectedRow != null && !_killingTree;

        /// <summary>
        /// 结束进程。分级确认 → 黑名单拦截 → 执行 → 记操作日志，任何一条路径都不允许静默跳过。
        /// </summary>
        private void KillProcess()
        {
            if (IsReadOnly) return; // 只读审计模式：界面按钮已禁用，这里再挡一道

            var row = _selectedRow;
            if (row == null) return;

            var processName = string.IsNullOrEmpty(row.Name) ? row.DisplayName : row.Name;
            var target = string.IsNullOrEmpty(row.Path) || row.Path == "—" ? processName : row.Path;

            if (IsDangerousName(processName))
            {
                WriteJournal(KillTaskId, DangerLevel.HighRisk, "kill", target, JournalResult.Skipped,
                    "命中内置危险进程黑名单（" + processName + "）", row, null);

                Log.Warn("已拒绝结束系统核心进程：" + processName + " (PID " + row.Pid + ")");
                Warn("这是系统核心进程，结束它会导致系统蓝屏或立即重启，WinKit 已拒绝执行。\r\n\r\n"
                    + "进程：" + processName + "（PID " + row.Pid + "）");
                StatusText = "已拒绝结束系统核心进程：" + processName;
                return;
            }

            var message =
                "即将强制结束以下进程：\r\n\r\n"
                + "进程名称：" + processName + "\r\n"
                + "进程 PID：" + row.Pid + "\r\n"
                + "可执行文件：" + target + "\r\n"
                + "内存占用：" + row.MemoryText + "    线程：" + row.Threads + "    句柄：" + row.Handles + "\r\n\r\n"
                + "警告：\r\n"
                + "1. 强制结束该进程可能导致未保存的数据丢失。\r\n"
                + "2. 结束系统关键进程可能导致系统不稳定、蓝屏或立即重启。\r\n"
                + "3. 该操作不可撤销，也不会进入撤销中心。\r\n\r\n"
                + "确定要继续吗？";

            var choice = MessageBox.Show(message, "结束进程（不可撤销）",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (choice != MessageBoxResult.OK) return;

            var error = TryKill(row.Pid);
            if (error == null)
            {
                WriteJournal(KillTaskId, DangerLevel.HighRisk, "kill", target, JournalResult.Ok, null, row, null);
                Log.Info("已结束进程：" + processName + " (PID " + row.Pid + ")  " + target);
                StatusText = "已结束进程：" + processName + "（PID " + row.Pid + "）";
            }
            else
            {
                WriteJournal(KillTaskId, DangerLevel.HighRisk, "kill", target, JournalResult.Failed, error, row, null);
                Log.Warn("结束进程失败：" + processName + " (PID " + row.Pid + ") → " + error);
                Warn("结束进程失败。\r\n\r\n进程：" + processName + "（PID " + row.Pid + "）\r\n原因：" + error);
                StatusText = "结束进程失败：" + error;
            }

            _ = RefreshAsync(); // 成功失败都刷新一次，让列表跟上实际状态
        }

        /// <summary>执行 Kill，把异常翻译成中文原因。返回 null 表示成功。</summary>
        private static string? TryKill(int pid)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    process.Kill();
                    return null;
                }
            }
            catch (ArgumentException)
            {
                return "进程不存在或已经退出（PID " + pid + "）。";
            }
            catch (InvalidOperationException)
            {
                return "进程已经退出，无法结束。";
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                switch (ex.NativeErrorCode)
                {
                    case 5:
                        return "权限不足，该进程需要管理员权限（请以管理员身份重新运行 WinKit）。";
                    case 87:
                        return "该进程受到系统保护，Windows 不允许结束它。";
                    case 0:
                        return "操作系统拒绝了结束请求，该进程可能已经受到保护或正在退出。";
                    default:
                        return "系统拒绝结束该进程（错误码 " + ex.NativeErrorCode + "：" + ex.Message + "）。";
                }
            }
            catch (Exception ex)
            {
                return "发生未预期的错误：" + ex.Message;
            }
        }

        // ───────────────────────── 结束进程树 ─────────────────────────
        //
        // 与"结束进程"的关键差别：一次操作可能干掉几百个进程，所以
        //   a) 确认框必须讲清楚"将结束多少个、它们是谁"，否则用户根本不知道自己在同意什么；
        //   b) 黑名单必须覆盖**整棵树**，只查根进程会放过核心子进程；
        //   c) 审计只写一条 —— 逐进程写日志会把 journal.jsonl 刷出几百条。

        /// <summary>
        /// 结束计划的纯数据快照。在后台线程一次性构造好，UI 线程只读它，绝不在 UI 线程碰 WMI 或进程句柄。
        /// </summary>
        private sealed class TreePlan
        {
            public TreePlan(int rootPid) { RootPid = rootPid; }

            public int RootPid { get; }

            /// <summary>实际执行顺序：后代（深的在前）+ 根进程放最后。先杀父进程会把子进程变成孤儿继续跑。</summary>
            public List<int> Order { get; } = new List<int>();

            /// <summary>后代 PID，不含根进程。</summary>
            public List<int> Descendants { get; } = new List<int>();

            /// <summary>后代 PID → 进程名。整棵树都要过黑名单，所以名字全部解析，只是确认框只展示前 10 个。</summary>
            public Dictionary<int, string> Names { get; } = new Dictionary<int, string>();

            /// <summary>拿不到名字（已退出/权限不足）时退化成 pid:1234，保证确认框和日志里不会有空名字。</summary>
            public string NameOf(int pid)
            {
                string? name;
                if (Names.TryGetValue(pid, out name) && !string.IsNullOrEmpty(name)) return name!;
                return "pid:" + pid.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>结束进程树的执行结果。失败明细留给弹窗与日志，不做"只报一个总数"的黑箱。</summary>
        private sealed class TreeKillOutcome
        {
            public int SuccessCount { get; set; }
            public List<string> Failures { get; } = new List<string>();
            public List<string> FailedNames { get; } = new List<string>();
            public int FailedCount => Failures.Count;
        }

        /// <summary>
        /// 结束进程树入口。整个流程：UI 线程取选中行 → 后台线程查一次 WMI 并解析名字 →
        /// 回 UI 线程做整棵树黑名单检查与确认 → 后台线程按序执行 → 回 UI 线程记一条日志并汇报。
        /// </summary>
        private void KillTree()
        {
            if (IsReadOnly) return;      // 只读审计模式：按钮已禁用，这里再挡一道
            if (_selectedRow == null) return;
            if (_killingTree) return;    // 双保险：CanExecute 在执行期间已经是 false

            var row = _selectedRow;
            _killingTree = true;
            RelayCommand.RaiseCanExecuteChanged();
            _ = KillTreeCoreAsync(row);
        }

        private async Task KillTreeCoreAsync(ProcessRow row)
        {
            try
            {
                var processName = string.IsNullOrEmpty(row.Name) ? row.DisplayName : row.Name;
                var target = string.IsNullOrEmpty(row.Path) || row.Path == "—" ? processName : row.Path;

                // 第 1 步：进程树只能问 WMI，属冷路径（100~300ms），必须在后台线程。
                // 而且**只查一次**：GetParentMap() 的结果直接复用给 GetFullKillOrder，绝不按进程逐个查。
                var pid = row.Pid;
                var plan = await TaskRunner.RunAsync(() => BuildTreePlan(pid)).ConfigureAwait(true);

                // 第 2 步：黑名单覆盖整棵树（根进程 + 每一个后代），只要命中一个就整棵树都不执行。
                string hitName;
                var hitPid = FindDangerousInTree(row, plan, out hitName);
                if (hitPid >= 0)
                {
                    WriteTreeJournal(row, target, plan, JournalResult.Skipped,
                        "命中内置危险进程黑名单（" + hitName + "），整棵树均未结束");

                    Log.Warn("已拒绝结束进程树：树中包含系统核心进程 " + hitName + " (PID " + hitPid + ")");
                    MessageBox.Show(
                        "进程树中包含系统核心进程，WinKit 已拒绝执行整个进程树。\r\n\r\n"
                        + "命中进程：" + hitName + "（PID " + hitPid + "）\r\n"
                        + "根进程：" + processName + "（PID " + row.Pid + "）\r\n\r\n"
                        + "结束系统核心进程会导致系统蓝屏、立即重启或丢失当前登录会话。\r\n"
                        + "即使它只是这棵树里的一个子进程，WinKit 也不会执行本次操作。\r\n"
                        + "如确需处理其中的普通进程，请改用“结束进程”逐个确认。",
                        "结束进程树（已拒绝执行）", MessageBoxButton.OK, MessageBoxImage.Stop);
                    StatusText = "已拒绝结束进程树：树中包含系统核心进程 " + hitName;
                    return;
                }

                // 第 3 步：没有后代不算错误 —— 明确提示后退化为"只结束根进程"，不拒绝。
                var descendants = plan.Descendants.Count;
                if (descendants == 0)
                {
                    MessageBox.Show(
                        "未发现子进程，将只结束该进程自己：\r\n\r\n" + processName + "（PID " + row.Pid + "）",
                        "结束进程树", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                // 第 4 步：确认框说清"多少个、是谁、不可撤销"。默认按钮是 Cancel。
                var choice = MessageBox.Show(
                    BuildTreeConfirmMessage(row, processName, target, plan),
                    "结束进程树（不可撤销）",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
                if (choice != MessageBoxResult.OK)
                {
                    StatusText = "已取消结束进程树：" + processName;
                    return;
                }

                // 第 5 步：逐个结束，仍然在后台线程（TaskContext 里没有句柄可用，只能靠纯 Process API）。
                var outcome = await TaskRunner.RunAsync(() => KillTreeProcesses(plan)).ConfigureAwait(true);

                // 第 6 步：一个进程树只写一条日志，规模与成败都压在 Note 里。
                var failed = outcome.FailedCount;
                if (failed == 0)
                {
                    WriteTreeJournal(row, target, plan, JournalResult.Ok,
                        "连同 " + plan.Descendants.Count.ToString(CultureInfo.InvariantCulture) + " 个子进程");

                    Log.Info("已结束进程树：" + processName + " (PID " + row.Pid + ") 共 "
                        + plan.Order.Count + " 个进程  " + target);
                    MessageBox.Show(
                        "已结束进程树。\r\n\r\n"
                        + "根进程：" + processName + "（PID " + row.Pid + "）\r\n"
                        + "共结束 " + plan.Order.Count + " 个进程（含根进程）。",
                        "结束进程树", MessageBoxButton.OK, MessageBoxImage.Information);
                    StatusText = "已结束进程树：" + processName + "，共 " + plan.Order.Count + " 个进程";
                }
                else
                {
                    WriteTreeJournal(row, target, plan, JournalResult.Failed,
                        "失败 " + failed.ToString(CultureInfo.InvariantCulture) + " 个："
                        + JoinFirstNames(outcome.FailedNames, MaxTreeFailureNames));

                    Log.Warn("结束进程树部分失败：" + processName + " (PID " + row.Pid + ") 成功 "
                        + outcome.SuccessCount + " 个，失败 " + failed + " 个");
                    Warn(
                        "结束进程树完成，但有部分进程未能结束。\r\n\r\n"
                        + "根进程：" + processName + "（PID " + row.Pid + "）\r\n"
                        + "成功 " + outcome.SuccessCount + " 个，失败 " + failed + " 个\r\n\r\n"
                        + "失败明细：\r\n" + BuildFailureDetail(outcome) + "\r\n\r\n"
                        + "失败的进程可能仍在运行，常见原因是权限不足（请以管理员身份重新运行 WinKit），"
                        + "或该进程受到系统保护、已经自行退出。");
                    StatusText = "结束进程树部分失败：成功 " + outcome.SuccessCount + " 个，失败 " + failed + " 个";
                }

                _ = RefreshAsync(); // 成功失败都刷新一次，让列表跟上实际状态
            }
            catch (Exception ex)
            {
                Log.Error("结束进程树失败：" + ex.Message, ex);
                Warn("结束进程树时发生未预期的错误。\r\n\r\n原因：" + ex.Message);
                StatusText = "结束进程树失败：" + ex.Message;
            }
            finally
            {
                _killingTree = false;
                RelayCommand.RaiseCanExecuteChanged();
            }
        }

        /// <summary>
        /// 后台线程构造结束计划：**只查一次 WMI**，然后把同一份 map 交给 GetFullKillOrder。
        /// 名字解析也在这里做完（整棵树都要过黑名单），UI 线程拿到的就是一份纯数据快照，不再碰进程句柄。
        /// </summary>
        private static TreePlan BuildTreePlan(int rootPid)
        {
            var plan = new TreePlan(rootPid);

            IReadOnlyDictionary<int, int> map;
            try
            {
                map = ProcessTree.GetParentMap();
            }
            catch (Exception ex)
            {
                Log.Warn("读取进程父子关系失败，本次退化为只结束根进程（PID " + rootPid + "）：" + ex.Message, ex);
                map = new Dictionary<int, int>();
            }

            IReadOnlyList<int> order;
            try
            {
                order = ProcessTree.GetFullKillOrder(rootPid, map);
            }
            catch (Exception ex)
            {
                Log.Warn("计算进程树结束顺序失败，本次退化为只结束根进程（PID " + rootPid + "）：" + ex.Message, ex);
                order = new List<int>();
            }

            foreach (var pid in order)
            {
                if (pid <= 0 || pid == rootPid) continue;
                plan.Descendants.Add(pid);
            }

            // 执行顺序 = 后代（深的在前）+ 根进程（最后）。GetFullKillOrder 本来就是这个语义，
            // 这里重新拼一遍是为了让"根进程一定在最后"不依赖上游实现细节。
            plan.Order.AddRange(plan.Descendants);
            if (plan.RootPid > 0) plan.Order.Add(plan.RootPid);

            // 名字全部解析：黑名单必须能查到每一个后代，而确认框只展示前 MaxTreePreviewItems 个。
            foreach (var pid in plan.Descendants)
            {
                plan.Names[pid] = ResolveProcessName(pid);
            }

            return plan;
        }

        /// <summary>尽力把 PID 解析成进程名；拿不到（已退出/权限不足）就显示 pid:1234。</summary>
        private static string ResolveProcessName(int pid)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    var name = process.ProcessName;
                    return string.IsNullOrEmpty(name) ? "pid:" + pid.ToString(CultureInfo.InvariantCulture) : name;
                }
            }
            catch (Exception)
            {
                return "pid:" + pid.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// 整棵树的黑名单检查：根进程命中、或**任意一个后代**命中，都返回命中的 PID（没命中返回 -1）。
        /// 只查根是不够的 —— 核心进程常常挂在别的进程树下，放过它就是放过一次蓝屏。
        /// </summary>
        private static int FindDangerousInTree(ProcessRow root, TreePlan plan, out string hitName)
        {
            var rootName = string.IsNullOrEmpty(root.Name) ? root.DisplayName : root.Name;
            if (IsDangerousName(rootName))
            {
                hitName = rootName;
                return root.Pid;
            }

            foreach (var pid in plan.Descendants)
            {
                string? name;
                if (!plan.Names.TryGetValue(pid, out name) || string.IsNullOrEmpty(name)) continue;
                if (IsDangerousName(name!))
                {
                    hitName = name!;
                    return pid;
                }
            }

            hitName = string.Empty;
            return -1;
        }

        /// <summary>
        /// 确认框文案。产品底线：用户点"确定"之前，必须已经看到"会结束多少个进程"和"它们是谁"。
        /// </summary>
        private static string BuildTreeConfirmMessage(ProcessRow row, string processName, string target, TreePlan plan)
        {
            var descendants = plan.Descendants.Count;
            var text = new StringBuilder();

            text.Append("即将强制结束以下进程树（先结束子进程，最后结束根进程）：\r\n\r\n");
            text.Append("根进程名称：").Append(processName).Append("\r\n");
            text.Append("根进程 PID：").Append(row.Pid).Append("\r\n");
            text.Append("可执行文件：").Append(target).Append("\r\n");
            text.Append("内存占用：").Append(row.MemoryText).Append("\r\n");
            text.Append("将结束的进程总数：").Append(plan.Order.Count).Append(" 个（含根进程自己）\r\n");
            text.Append("其中子进程：").Append(descendants).Append(" 个\r\n\r\n");

            if (descendants == 0)
            {
                text.Append("该进程没有子进程，将只结束它自己。\r\n");
            }
            else
            {
                var take = descendants < MaxTreePreviewItems ? descendants : MaxTreePreviewItems;
                if (descendants <= MaxTreePreviewItems)
                {
                    text.Append("将结束的子进程（共 ").Append(descendants).Append(" 个，全部列出）：\r\n");
                }
                else
                {
                    text.Append("将结束的子进程（共 ").Append(descendants).Append(" 个，最多列出前 ")
                        .Append(MaxTreePreviewItems).Append(" 个）：\r\n");
                }

                for (var i = 0; i < take; i++)
                {
                    var pid = plan.Descendants[i];
                    text.Append("  · ").Append(plan.NameOf(pid)).Append("（PID ").Append(pid).Append("）\r\n");
                }

                if (descendants > take)
                {
                    text.Append("  … 另有 ").Append(descendants - take).Append(" 个子进程未列出\r\n");
                }
            }

            text.Append("\r\n警告：\r\n");
            text.Append("1. 强制结束这些进程可能导致未保存的数据丢失。\r\n");
            text.Append("2. 结束系统关键进程可能导致系统不稳定、蓝屏或立即重启。\r\n");
            text.Append("3. 该操作不可撤销，也不会进入撤销中心。\r\n\r\n");
            text.Append("确定要继续吗？");
            return text.ToString();
        }

        /// <summary>
        /// 按 order 逐个结束（深的先杀，根进程最后）。单个失败不中断整体：一个进程树里混着受保护、
        /// 已自行退出的进程是常态，因为其中一个失败就放弃剩下的，反而会留下一堆孤儿进程。
        /// 权限不足（Win32 错误码 5）由 TryKill 统一翻译成中文原因。
        /// </summary>
        private static TreeKillOutcome KillTreeProcesses(TreePlan plan)
        {
            var outcome = new TreeKillOutcome();
            foreach (var pid in plan.Order)
            {
                var error = TryKill(pid);
                if (error == null)
                {
                    outcome.SuccessCount++;
                    continue;
                }

                var name = plan.NameOf(pid);
                outcome.FailedNames.Add(name);
                outcome.Failures.Add(name + "（PID " + pid.ToString(CultureInfo.InvariantCulture) + "）：" + error);
            }
            return outcome;
        }

        private static string BuildFailureDetail(TreeKillOutcome outcome)
        {
            var take = outcome.Failures.Count < MaxTreeFailureNames ? outcome.Failures.Count : MaxTreeFailureNames;
            var text = new StringBuilder();
            for (var i = 0; i < take; i++) text.Append("  · ").Append(outcome.Failures[i]).Append("\r\n");
            if (outcome.Failures.Count > take)
            {
                text.Append("  … 另有 ").Append(outcome.Failures.Count - take).Append(" 个失败未列出\r\n");
            }
            return text.ToString().TrimEnd('\r', '\n');
        }

        private static string JoinFirstNames(IReadOnlyList<string> names, int max)
        {
            var take = names.Count < max ? names.Count : max;
            var text = new StringBuilder();
            for (var i = 0; i < take; i++)
            {
                if (i > 0) text.Append('、');
                text.Append(names[i]);
            }
            if (names.Count > take)
            {
                text.Append(" 等 ").Append(names.Count.ToString(CultureInfo.InvariantCulture)).Append(" 个");
            }
            return text.ToString();
        }

        /// <summary>
        /// 结束进程树只写**一条**操作日志：一个树可能有几百个后代，逐个写会把 journal.jsonl 刷爆。
        /// 规模与成败都压在 Note 里，审计时一眼能看清这次到底动了多大范围。
        /// </summary>
        private static void WriteTreeJournal(ProcessRow row, string target, TreePlan plan, string result, string note)
        {
            try
            {
                var journal = AppServices.Journal;
                if (journal == null) return;

                var entry = new JournalEntry
                {
                    BatchId = JournalStore.NewBatchId(),
                    TaskId = KillTreeTaskId,
                    Level = DangerLevel.HighRisk,
                    Action = "kill-tree",
                    Target = target,
                    Before = "pid=" + row.Pid
                        + " name=" + row.Name
                        + " memory=" + row.MemoryBytes.ToString(CultureInfo.InvariantCulture)
                        + " path=" + row.Path
                        + " children=" + plan.Descendants.Count.ToString(CultureInfo.InvariantCulture)
                        + " total=" + plan.Order.Count.ToString(CultureInfo.InvariantCulture),
                    After = null,
                    Size = row.MemoryBytes,
                    Result = result,
                    UndoKind = null,   // 结束进程树不可撤销
                    Elevated = AppServices.IsElevated,
                    Note = note
                };
                journal.Append(entry);
            }
            catch (Exception ex)
            {
                Log.Warn("写入操作日志失败：" + ex.Message, ex);
            }
        }

        private static bool IsDangerousName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            // 去掉可能的扩展名再比对，避免把 "lsass" 写成 "lsass.exe" 时漏判
            var candidate = name;
            if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate.Substring(0, candidate.Length - 4);
            }

            foreach (var dangerous in DangerousProcessNames)
            {
                if (string.Equals(candidate, dangerous, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>写操作日志。JournalEntry 在代码里构造（net48 上不能用对象初始化 + init，也用不着匿名类型）。
        /// 审计记录失败不能反过来把功能弄崩，所以整体包一层 try/catch。</summary>
        private static void WriteJournal(
            string taskId, string level, string action, string target,
            string result, string? note, ProcessRow row, string? after)
        {
            try
            {
                var journal = AppServices.Journal;
                if (journal == null) return;

                var entry = new JournalEntry
                {
                    BatchId = JournalStore.NewBatchId(),
                    TaskId = taskId,
                    Level = level,
                    Action = action,
                    Target = target,
                    Before = "pid=" + row.Pid
                        + " name=" + row.Name
                        + " memory=" + row.MemoryBytes.ToString(CultureInfo.InvariantCulture)
                        + " path=" + row.Path
                        + (note == null ? string.Empty : " note=" + note),
                    After = after,
                    Size = row.MemoryBytes,
                    Result = result,
                    UndoKind = null,   // 结束进程不可撤销
                    Elevated = AppServices.IsElevated
                };
                journal.Append(entry);
            }
            catch (Exception ex)
            {
                Log.Warn("写入操作日志失败：" + ex.Message, ex);
            }
        }

        private static void Warn(string message)
        {
            MessageBox.Show(message, "进程管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private bool CanCopyPath() => _selectedRow != null && !string.IsNullOrEmpty(_selectedRow.Path) && _selectedRow.Path != "—";

        private void CopyPath()
        {
            var row = _selectedRow;
            if (row == null) return;

            var path = row.Path;
            if (string.IsNullOrEmpty(path) || path == "—")
            {
                StatusText = "该进程没有可复制的路径（可能是系统进程或权限不足）。";
                return;
            }

            SetClipboardText(path);
            StatusText = "已复制路径：" + path;
        }

        /// <summary>
        /// 剪贴板是全局共享资源，常被其他进程短暂占用（外部异常 COMException），
        /// 所以失败要重试并且绝不把异常抛给用户。
        /// </summary>
        private void SetClipboardText(string text)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt == 2)
                    {
                        Log.Warn("复制到剪贴板失败：" + ex.Message, ex);
                        StatusText = "复制到剪贴板失败，剪贴板可能被其他程序占用。";
                        return;
                    }
                    Thread.Sleep(80);
                }
            }
        }

        // ================================================================ AI 助手（可选联网）
        //
        // 隐私边界（本页最重要的一条）：发送的**只有进程名**，
        // 绝不含文件路径、命令行、PID、内存数值，也不含文件版本描述
        // （版本描述是构建工具写入的自由文本，实测会出现构建机用户名，属于无法可靠脱敏的通道）。
        // 不可用时按钮保持可见，点下去只弹原因，绝不发起任何请求。

        /// <summary>有 AI 回复时结果卡片才出现。</summary>
        public bool HasAiReply
        {
            get => _hasAiReply;
            private set => SetProperty(ref _hasAiReply, value);
        }

        /// <summary>AI 回复正文（几百字，显示在页面底部的只读结果卡片里，不用 MessageBox）。</summary>
        public string AiReplyText
        {
            get => _aiReplyText;
            private set => SetProperty(ref _aiReplyText, value);
        }

        /// <summary>
        /// 结果卡片标题，带上"这次解释的是哪个进程"。
        /// 列表每 2 秒重建行对象，选中行随时会变，不写清对象的话，
        /// 用户很容易把上一个进程的解读误当成现在选中的这个。
        /// </summary>
        public string AiReplySubject
        {
            get => _aiReplySubject;
            private set => SetProperty(ref _aiReplySubject, value);
        }

        /// <summary>按钮旁边那行小字：可用时说明联网流程，不可用时直接显示原因。</summary>
        public string AiAvailabilityHint
        {
            get
            {
                if (AiLauncher.IsAvailable) return AiAvailableHint;
                var reason = AiLauncher.UnavailableReason;
                return string.IsNullOrWhiteSpace(reason) ? "AI 助手当前不可用。" : reason!;
            }
        }

        /// <summary>页面每次显示时调用：重新求值 AI 可用性小字（设置页可能刚改过）。</summary>
        public void RefreshAiAvailability() => OnPropertyChanged(nameof(AiAvailabilityHint));

        /// <summary>
        /// 让 AI 解释选中的进程。顺序不可调换：
        /// AI 可用性 → 有没有选中行 → 构造请求（只放进程名）→ 预览确认 → 显示回复。
        /// </summary>
        private void ExplainWithAi()
        {
            // 第一件事：不可用就只说明原因，绝不发起任何请求
            if (!AiLauncher.IsAvailable)
            {
                ShowAiUnavailable();
                return;
            }

            var row = _selectedRow;
            if (row == null)
            {
                MessageBox.Show(
                    "请先在列表里选中一个进程。",
                    AiTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // 只取进程名（ProcessSample.Name 本身不含 .exe）。
            // **绝不回退到 DisplayName**：DisplayName 在进程名为空时就是文件版本描述，
            // 而那正是"无法可靠脱敏的自由文本通道"。拿不到进程名就什么都不发。
            var name = row.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show(
                    "该进程没有可用的进程名，无法让 AI 解释。",
                    AiTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var request = PromptBuilder.ForProcess(name);
            ApplyAiReply(ShowAiPreview(request, "用 AI 解释这个进程"), name);
        }

        /// <summary>AI 不可用时的唯一提示路径。</summary>
        private static void ShowAiUnavailable()
        {
            var reason = AiLauncher.UnavailableReason;
            if (string.IsNullOrWhiteSpace(reason)) reason = "AI 助手当前不可用。";

            MessageBox.Show(
                reason + "\r\n\r\n" + AiEnableHint,
                AiTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        /// <summary>
        /// 弹出预览窗口（模态，内部完成"确认 → 流式 → 返回"）。
        /// ViewModel 不在可视树上，拿不到 Window.GetWindow(this)，页面都挂在主窗口里，
        /// 所以主窗口就是等价的 owner。返回 null = 用户取消或失败。
        /// </summary>
        private static string? ShowAiPreview(AiRequest request, string title)
        {
            var app = Application.Current;
            var owner = app == null ? null : app.MainWindow;
            try
            {
                return AiLauncher.ShowPreview(owner, request, title);
            }
            catch (Exception ex)
            {
                Log.Error("AI 解释进程失败：" + ex.Message, ex);
                MessageBox.Show(
                    "AI 请求失败：" + ex.Message,
                    AiTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return null;
            }
        }

        /// <summary>null（用户取消或失败）时什么都不做；空回复也不显示空卡片。</summary>
        private void ApplyAiReply(string? reply, string subject)
        {
            if (string.IsNullOrEmpty(reply)) return;
            AiReplySubject = AiReplyHeader + "：" + subject;
            AiReplyText = reply!;
            HasAiReply = true;
        }

        private void CopyAiReply()
        {
            var text = _aiReplyText;
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                // 剪贴板是全局共享资源，失败只提示、不抛异常
                Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                Log.Warn("复制 AI 回复失败：" + ex.Message, ex);
                MessageBox.Show(
                    "复制失败，剪贴板可能被其他程序占用。",
                    AiTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void ClearAiReply()
        {
            AiReplySubject = AiReplyHeader;
            AiReplyText = string.Empty;
            HasAiReply = false;
        }
    }
}
