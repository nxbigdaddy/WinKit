using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using WinKit.App.Ai;
using WinKit.App.Mvvm;
using WinKit.App.Services;
using WinKit.Core.Ai;
using WinKit.Core.Cleanup;
using WinKit.Core.Diagnostics;
using WinKit.Core.Journal;
using WinKit.Core.Tasks;
using WinKit.Platform.Cleanup;
using WinKit.Platform.Metrics;

namespace WinKit.App.ViewModels
{
    /// <summary>
    /// 一键体检页的 ViewModel。
    ///
    /// 产品定位：**只扫描、只判断、绝不动手**。本页因此没有任何写操作入口 ——
    /// 不清理、不删除、不改注册表、不改配置，连隔离区都只是量一下大小。
    /// 它的价值是告诉用户"哪里有问题、该去哪一页处理"，而不是替用户做决定。
    ///
    /// 评估逻辑全在 <see cref="HealthCheck"/>（WinKit.Core）里，本类只做两件事：
    ///   1) 从 Platform 收集实测数据，填出一个 <see cref="HealthInputs"/>；
    ///   2) 把返回的 <see cref="HealthReport"/> 渲染成界面行。
    /// 评估规则被刻意挡在 Core 里，所以它能被单测毫秒级跑遍边界，而不必真去动系统。
    ///
    /// 线程模型：磁盘枚举、进程采样、清理扫描都在 TaskRunner 的后台线程上跑，
    /// 进度与完成回调由 TaskRunner 封送回 UI 线程，因此这里刷新 ObservableCollection
    /// 不需要自己再封送 Dispatcher。
    /// </summary>
    public sealed class HealthViewModel : ObservableObject
    {
        private const string TaskId = "health.check";
        private const int MaxItemsPerRule = 20000;

        /// <summary>
        /// 进程 CPU 占用是两次采样的差值，所以必须隔一段时间采第二次。
        /// TaskRunner 起的线程带 <see cref="CancellationToken"/>，这里用它做可中断等待：
        /// 点"取消"时最多等这一个采样间隔就会返回，不会把 700ms 睡满。
        /// </summary>
        private const int CpuSampleIntervalMs = 700;

        private const string ReadyText = "就绪：点「开始体检」扫描本机状态。体检只做扫描，不会清理或修改任何东西。";

        /// <summary>本页的只读承诺，页面底部与状态栏都用它，避免两处口径不一致。</summary>
        private const string ScanOnlyText = "体检只做扫描，不会清理或修改任何东西。";

        // ---- AI 助手（可选联网）----

        /// <summary>AI 弹窗的标题，同时也是"这是 AI 功能"的标识。</summary>
        private const string AiTitle = "用 AI 解释";

        /// <summary>
        /// 可用时按钮旁边那行小字。必须如实说明会联网这件事 ——
        /// AI 不是本地功能，不能让它看起来和"开始体检"一样。
        /// </summary>
        private const string AiAvailableHint = "会联网：将先显示要发送的内容，确认后才联网。";

        /// <summary>不可用时给用户的下一步动作。</summary>
        private const string AiEnableHint = "可在「设置 → AI 助手」启用并填写 API Key。";

        private CancellationTokenSource? _cts;

        /// <summary>
        /// 最近一次体检的原始报告。AI 解读需要它（<see cref="PromptBuilder.ForHealthReport"/> 接受领域对象），
        /// 界面上的 <see cref="Items"/> 只是它的显示副本，不能拿来还原报告。
        /// </summary>
        private HealthReport? _lastReport;

        private string _aiReplyText = string.Empty;
        private bool _hasAiReply;

        private int _score;
        private string _scoreText = "—";
        private string _verdict = string.Empty;
        private string _summaryText = string.Empty;
        private string _lastRunText = string.Empty;
        private bool _hasResult;
        private bool _isRunning;
        private double _progress;
        private string _progressText = string.Empty;
        private string _statusText = ReadyText;

        public HealthViewModel()
        {
            RunCommand = new RelayCommand(Run, CanRun);
            CancelCommand = new RelayCommand(Cancel, CanCancel);

            // AI 入口：CanExecute 里**故意不判断 AiLauncher.IsAvailable** ——
            // 不可用时按钮保持可见、可点，点下去只弹"为什么不能用"，用户才知道有这个功能。
            ExplainWithAiCommand = new RelayCommand(ExplainWithAi);
            CopyAiReplyCommand = new RelayCommand(CopyAiReply);
            ClearAiReplyCommand = new RelayCommand(ClearAiReply);
        }

        // ================================================================ 包装类

        /// <summary>
        /// 界面上的一条体检结果。包装 <see cref="HealthItem"/> 而不直接绑定它，
        /// 是为了把"要不要显示建议行"这类界面判断留在 ViewModel 里，
        /// Core 的领域模型保持纯粹。
        /// </summary>
        public sealed class HealthItemRow
        {
            public HealthItemRow(HealthItem item)
            {
                if (item == null) throw new ArgumentNullException(nameof(item));

                Title = item.Title;
                StatusText = item.StatusText;
                Detail = item.Detail;
                Suggestion = item.Suggestion;
                Status = item.Status;
                NeedsAction = item.NeedsAction;
                HasSuggestion = !string.IsNullOrEmpty(item.Suggestion);
            }

            public string Title { get; }

            /// <summary>"正常" / "可以留意" / "建议处理" / "需尽快处理"。</summary>
            public string StatusText { get; }

            /// <summary>实测数值，例如"系统盘剩余 12.3 GB（占 4.2%）"。</summary>
            public string Detail { get; }

            /// <summary>建议动作，可能为空。</summary>
            public string Suggestion { get; }

            public HealthStatus Status { get; }

            /// <summary>Warning 或 Critical。</summary>
            public bool NeedsAction { get; }

            public bool HasSuggestion { get; }
        }

        // ================================================================ 结果

        public ObservableCollection<HealthItemRow> Items { get; } = new ObservableCollection<HealthItemRow>();

        public int Score
        {
            get => _score;
            private set => SetProperty(ref _score, value);
        }

        /// <summary>评分文本。单独给一个字符串属性，界面就不用为了显示整数去配格式化。</summary>
        public string ScoreText
        {
            get => _scoreText;
            private set => SetProperty(ref _scoreText, value);
        }

        public string Verdict
        {
            get => _verdict;
            private set => SetProperty(ref _verdict, value);
        }

        public string SummaryText
        {
            get => _summaryText;
            private set => SetProperty(ref _summaryText, value);
        }

        /// <summary>最近一次体检的时间。没跑过时为空。</summary>
        public string LastRunText
        {
            get => _lastRunText;
            private set => SetProperty(ref _lastRunText, value);
        }

        /// <summary>是否已经跑过一次体检。结论卡片靠它决定显不显示。</summary>
        public bool HasResult
        {
            get => _hasResult;
            private set => SetProperty(ref _hasResult, value);
        }

        // ================================================================ 运行状态

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (!SetProperty(ref _isRunning, value)) return;
                OnPropertyChanged(nameof(IsIdle));
            }
        }

        /// <summary>没有在跑。绑定用（BoolToVisibilityInverted 也能用，但直接暴露更省事）。</summary>
        public bool IsIdle => !_isRunning;

        /// <summary>0.0 ~ 1.0。</summary>
        public double Progress
        {
            get => _progress;
            private set
            {
                if (!SetProperty(ref _progress, value)) return;
                OnPropertyChanged(nameof(ProgressPercent));
            }
        }

        /// <summary>0 ~ 100。直接绑给 ProgressBar.Value，省掉一个转换器。</summary>
        public double ProgressPercent
        {
            get
            {
                var percent = _progress * 100d;
                if (percent < 0d) return 0d;
                if (percent > 100d) return 100d;
                return percent;
            }
        }

        public string ProgressText
        {
            get => _progressText;
            private set => SetProperty(ref _progressText, value);
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        /// <summary>只读审计模式。本页全是只读操作，所以只提示、不拦截。</summary>
        public bool IsReadOnly => AppServices.IsReadOnly;

        public string ReadOnlyNotice =>
            "只读审计模式已开启。体检本身只读，不受影响；但去其它页面处理问题时写操作会被禁用。";

        /// <summary>本页的只读承诺，界面上必须一直可见。</summary>
        public string ScanOnlyNotice => ScanOnlyText;

        // ================================================================ 命令

        public RelayCommand RunCommand { get; }

        public RelayCommand CancelCommand { get; }

        /// <summary>
        /// 页面卸载时调用：取消仍在跑的体检。
        /// 本页没有定时器要停，但后台任务必须放掉，否则用户切页后任务还在扫盘并往 UI 线程投进度。
        /// </summary>
        public void Stop()
        {
            CancelInternal();
        }

        // ================================================================ 体检主流程

        private bool CanRun() => !IsRunning;

        private bool CanCancel() => IsRunning;

        private void Run()
        {
            if (IsRunning) return;

            // 结果引用用局部变量承接：worker 在后台线程写它自己的局部变量，
            // 完成回调在 UI 线程读 —— Task 完成本身保证了两者之间的可见性。
            HealthReport? report = null;

            IsRunning = true;
            Progress = 0d;
            ProgressText = "正在体检…";
            StatusText = "正在体检…" + ScanOnlyText;
            RelayCommand.RaiseCanExecuteChanged();

            _cts = TaskRunner.Run(
                TaskId,
                ctx => { report = CollectAndEvaluate(ctx); },
                OnProgress,
                error => OnCompleted(report, error));
        }

        private void OnProgress(TaskProgress progress)
        {
            // TaskRunner 用 Progress<T> 封送，回调在 UI 线程执行，可以直接改属性
            if (progress.IsIndeterminate) return;
            Progress = progress.Fraction;
            if (!string.IsNullOrEmpty(progress.Message)) ProgressText = progress.Message!;
        }

        /// <summary>完成回调。TaskRunner 保证它已经在 UI 线程上。</summary>
        private void OnCompleted(HealthReport? report, Exception? error)
        {
            _cts = null;
            IsRunning = false;
            RelayCommand.RaiseCanExecuteChanged();

            if (error != null)
            {
                Progress = 0d;
                ProgressText = string.Empty;
                StatusText = "体检失败：" + error.Message + "。本次没有做任何修改。";
                Log.Error("一键体检失败：" + error.Message, error);
                return;
            }

            if (report == null)
            {
                // 被用户取消（或采样期间被取消）：不产出半截结果，避免"看了一半的分数"
                Progress = 0d;
                ProgressText = string.Empty;
                StatusText = "已取消体检。" + ScanOnlyText;
                return;
            }

            ApplyReport(report);
        }

        private void ApplyReport(HealthReport report)
        {
            // 新报告替换旧报告后，上一份解读就不再成立了，先收起结果卡片
            ClearAiReply();
            _lastReport = report;

            Items.Clear();
            foreach (var item in report.Items)
            {
                Items.Add(new HealthItemRow(item));
            }

            Score = report.Score;
            ScoreText = report.Score.ToString(CultureInfo.InvariantCulture);
            Verdict = report.Verdict;
            SummaryText = report.SummaryText;
            LastRunText = "体检时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            HasResult = true;

            Progress = 1d;
            ProgressText = "体检完成";

            StatusText = "体检完成：" + report.SummaryText
                + "。每一项都写清了哪里有问题、建议去哪里处理 —— "
                + ScanOnlyText;
        }

        private void Cancel()
        {
            if (!IsRunning) return;
            CancelInternal();
            StatusText = "正在取消体检…（当前阶段会尽快停下来）";
        }

        private void CancelInternal()
        {
            var cts = _cts;
            _cts = null;
            if (cts == null) return;
            try { cts.Cancel(); }
            catch (Exception ex) { Log.Debug("取消体检任务时出错：" + ex.Message); }
        }

        // ================================================================ 数据收集（后台线程）

        /// <summary>
        /// 后台收集 + 评估。全程只读：只查、只算，不写任何东西。
        /// 每个阶段之间检查一次取消标记，保证点了"取消"能较快停下来。
        /// </summary>
        private static HealthReport? CollectAndEvaluate(TaskContext ctx)
        {
            var inputs = new HealthInputs();

            ctx.Report(0.05, "正在检查磁盘…");
            CollectSystemDrive(inputs);

            ctx.Report(0.20, "正在检查内存…");
            CollectMemory(inputs);

            ctx.Report(0.35, "正在采样进程 CPU…");
            if (!CollectProcesses(ctx, inputs)) return null;

            ctx.Report(0.55, "正在扫描可清理内容（只读）…");
            CollectCleanupCandidates(ctx, inputs);

            ctx.Report(0.90, "正在读取回收站与隔离区…");
            CollectStorage(inputs);

            ctx.Report(0.97, "正在汇总体检结论…");
            CollectHistory(inputs);

            ctx.ThrowIfCancelled();

            // 评估是纯计算，微秒级；放在这里是为了让整个流程只有一个"产出点"
            return HealthCheck.Evaluate(inputs);
        }

        /// <summary>系统盘：按 <see cref="Environment.SystemDirectory"/> 的根去找对应的卷，找不到就退而取第一个固定盘。</summary>
        private static void CollectSystemDrive(HealthInputs inputs)
        {
            try
            {
                var systemRoot = string.Empty;
                try { systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? string.Empty; }
                catch (Exception) { systemRoot = string.Empty; }

                var volumes = DiskInfo.Query();
                if (volumes.Count == 0) return;

                DiskVolume? system = null;
                if (systemRoot.Length > 0)
                {
                    foreach (var volume in volumes)
                    {
                        if (string.Equals(volume.Name, systemRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            system = volume;
                            break;
                        }
                    }
                }

                // 找不到系统盘（盘符被重定向、卷读不到）时取第一个：宁可显示"某个盘"，
                // 也不要因为缺一条数据把整个体检结果打残
                if (system == null) system = volumes[0];

                inputs.SystemDriveName = string.IsNullOrEmpty(system.Name) ? systemRoot : system.Name;
                inputs.SystemDriveTotalBytes = system.TotalBytes;
                inputs.SystemDriveFreeBytes = system.FreeBytes;
            }
            catch (Exception ex)
            {
                // 单条数据收集失败不应让整次体检失败：HealthCheck 对 0 值会跳过该项
                Log.Warn("体检读取磁盘信息失败：" + ex.Message, ex);
            }
        }

        private static void CollectMemory(HealthInputs inputs)
        {
            try
            {
                var memory = MemoryInfo.Query();
                inputs.MemoryTotalBytes = memory.TotalBytes;
                inputs.MemoryUsedBytes = memory.UsedBytes;
            }
            catch (Exception ex)
            {
                Log.Warn("体检读取内存信息失败：" + ex.Message, ex);
            }
        }

        /// <summary>
        /// 进程采样。CPU 需要两次采样的差值，所以这里采两次、中间等一个采样间隔。
        /// 取消时返回 false，让上层放弃产出结果。
        /// </summary>
        private static bool CollectProcesses(TaskContext ctx, HealthInputs inputs)
        {
            try
            {
                var sampler = new ProcessSampler();

                // 第一次采样只建立 CPU 时间基准，所有进程的 CpuPercent 都是 0，结果直接丢掉
                sampler.Sample();

                if (!SleepInterruptible(ctx, CpuSampleIntervalMs)) return false;

                var samples = sampler.Sample();
                inputs.ProcessCount = samples.Count;

                ProcessSample? top = null;
                foreach (var sample in samples)
                {
                    if (sample == null) continue;
                    if (top == null || sample.CpuPercent > top.CpuPercent) top = sample;
                }

                if (top != null)
                {
                    var name = top.DisplayName;
                    if (string.IsNullOrEmpty(name)) name = top.Name;
                    inputs.TopCpuProcessName = name;
                    inputs.TopCpuProcessPercent = top.CpuPercent;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("体检采样进程 CPU 失败：" + ex.Message, ex);
            }
            return true;
        }

        /// <summary>
        /// 可中断等待。取消时返回 false。
        /// 用 Task.Delay 而不是 Thread.Sleep，是为了让"取消"不必等满整个采样间隔。
        /// </summary>
        private static bool SleepInterruptible(TaskContext ctx, int milliseconds)
        {
            try
            {
                System.Threading.Tasks.Task.Delay(milliseconds, ctx.Token).Wait();
                return true;
            }
            catch (AggregateException ex)
            {
                if (ex.InnerException is OperationCanceledException) return false;
                throw;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>
        /// 可清理内容：只做**只读扫描**，用不需要管理员权限的规则。
        ///
        /// ExcludeRoots **必须**包含 WinKit 自己的数据目录：隔离区就在 &lt;数据目录&gt;\quarantine 下，
        /// 一旦数据目录被某条规则命中（例如用户的 %TEMP% 恰好落在数据目录里），
        /// 体检就会把"可撤销的退路"也算进垃圾里 —— 那既不准确，也会误导用户去清它。
        /// </summary>
        private static void CollectCleanupCandidates(TaskContext ctx, HealthInputs inputs)
        {
            try
            {
                var options = new ScanOptions { MaxItemsPerRule = MaxItemsPerRule };

                var data = AppServices.Data;
                if (data != null && !string.IsNullOrWhiteSpace(data.Directory))
                {
                    options.ExcludeRoots.Add(data.Directory);
                }

                var rules = new List<CleanupRule>();
                foreach (var rule in CleanupRuleCatalog.CreateDefault())
                {
                    // 不申请提权是体检的硬约束：体检不能因为要看一眼就弹 UAC
                    if (rule.RequiresAdmin) continue;
                    rules.Add(rule);
                }

                var scanner = new CleanupScanner(AppServices.Guard);
                var result = scanner.Scan(rules, ctx, options);

                inputs.CleanupCandidateCount = result.TotalItems;
                inputs.CleanupCandidateBytes = result.TotalSize;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 扫描失败就按"没扫出东西"处理（0 项），体检其余项目照常出结果
                Log.Warn("体检扫描可清理内容失败：" + ex.Message, ex);
                inputs.CleanupCandidateCount = 0;
                inputs.CleanupCandidateBytes = 0L;
            }
        }

        private static void CollectStorage(HealthInputs inputs)
        {
            // 回收站：查不到就是 -1，HealthCheck 对 -1 的处理是"该项不出现"
            inputs.RecycleBinBytes = -1L;
            try
            {
                inputs.RecycleBinBytes = RecycleBin.GetSizeBytes();
            }
            catch (Exception ex)
            {
                Log.Warn("体检读取回收站大小失败：" + ex.Message, ex);
            }

            try
            {
                // 隔离区的大小是"发现的问题之一"（占用过大可以去清理），这里只量一下，绝不改动它
                inputs.QuarantineBytes = AppServices.Quarantine.GetTotalSize();
            }
            catch (Exception ex)
            {
                Log.Warn("体检读取隔离区大小失败：" + ex.Message, ex);
            }
        }

        private static void CollectHistory(HealthInputs inputs)
        {
            inputs.RunningAsAdmin = AppServices.IsElevated;

            // -1 = 未知。日志读不出来时留 -1，比谎报"0 条失败"诚实
            inputs.RecentFailedOperations = -1;
            try
            {
                var failed = 0;
                foreach (var entry in AppServices.Journal.ReadRecent(500))
                {
                    if (entry == null) continue;
                    if (string.Equals(entry.Result, JournalResult.Failed, StringComparison.Ordinal)) failed++;
                }
                inputs.RecentFailedOperations = failed;
            }
            catch (Exception ex)
            {
                Log.Warn("体检读取操作日志失败：" + ex.Message, ex);
            }
        }

        // ================================================================ AI 助手（可选联网）
        //
        // 本页的 AI 入口统一走「先看要发送什么 → 用户确认 → 才联网」：
        //   1) 不可用时按钮保持可见，点下去只弹原因，绝不发起任何请求；
        //   2) 只收集最少必要的数据，且经 PromptBuilder 收口（它只取分数与各项文字，不含路径）；
        //   3) 真正的网络调用全部在 AiLauncher / AiPreviewWindow 里 —— 本文件没有任何网络 API。

        /// <summary>有 AI 回复时结果卡片才出现（没有回复时整块折叠）。</summary>
        public bool HasAiReply
        {
            get => _hasAiReply;
            private set => SetProperty(ref _hasAiReply, value);
        }

        /// <summary>AI 回复正文。回复常有几百字，所以显示在只读结果卡片里，而不是弹 MessageBox。</summary>
        public string AiReplyText
        {
            get => _aiReplyText;
            private set => SetProperty(ref _aiReplyText, value);
        }

        /// <summary>
        /// 按钮旁边那行小字：可用时说明"会联网 + 发送前先给你看"，不可用时直接给出原因。
        /// 每次页面显示时由 <see cref="RefreshAiAvailability"/> 重新求值（用户可能刚在设置里启用）。
        /// </summary>
        public string AiAvailabilityHint
        {
            get
            {
                if (AiLauncher.IsAvailable) return AiAvailableHint;
                var reason = AiLauncher.UnavailableReason;
                return string.IsNullOrWhiteSpace(reason) ? "AI 助手当前不可用。" : reason!;
            }
        }

        public RelayCommand ExplainWithAiCommand { get; }

        public RelayCommand CopyAiReplyCommand { get; }

        public RelayCommand ClearAiReplyCommand { get; }

        /// <summary>页面每次显示时调用：重新求值 AI 可用性小字（设置页可能刚改过）。</summary>
        public void RefreshAiAvailability() => OnPropertyChanged(nameof(AiAvailabilityHint));

        /// <summary>
        /// 让 AI 解读最近一次体检结果。顺序不可调换：
        /// AI 可用性 → 有没有体检结果 → 构造请求 → 预览确认 → 显示回复。
        /// </summary>
        private void ExplainWithAi()
        {
            // 第一件事：不可用就只说明原因，绝不发起任何请求
            if (!AiLauncher.IsAvailable)
            {
                ShowAiUnavailable();
                return;
            }

            var report = _lastReport;
            if (!HasResult || report == null)
            {
                MessageBox.Show(
                    "请先点「开始体检」，再让 AI 解读。",
                    AiTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // 数据范围：分数、结论、各检查项的 Status/Title/Detail/Suggestion。
            // 体检项本身不含路径；卷标/用户名等在 HealthCheck 里已经只映射成文字说明。
            var request = PromptBuilder.ForHealthReport(report);
            ApplyAiReply(ShowAiPreview(request, "用 AI 解释体检结果"));
        }

        /// <summary>AI 不可用时的唯一提示路径。按钮不禁用，所以这里是用户唯一能知道原因的地方。</summary>
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
        ///
        /// ViewModel 不在可视树上，拿不到 Window.GetWindow(this)，而且本页最终都挂在主窗口里，
        /// 所以主窗口就是等价的 owner（预览窗会正确居中并阻塞主窗口）。
        ///
        /// 返回值语义与 <see cref="AiLauncher.ShowPreview"/> 一致：null = 用户取消或失败。
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
                // 预览/请求本身的异常不该让整个页面崩掉：记日志 + 如实告知
                Log.Error("AI 解读体检结果失败：" + ex.Message, ex);
                MessageBox.Show(
                    "AI 请求失败：" + ex.Message,
                    AiTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return null;
            }
        }

        /// <summary>null（用户取消或失败）时什么都不做；回复为空也不显示一张空卡片。</summary>
        private void ApplyAiReply(string? reply)
        {
            if (string.IsNullOrEmpty(reply)) return;
            AiReplyText = reply!;
            HasAiReply = true;
        }

        private void CopyAiReply()
        {
            var text = _aiReplyText;
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                // 剪贴板是全局共享资源，常被其他进程短暂占用，失败不能把异常抛给用户
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
            AiReplyText = string.Empty;
            HasAiReply = false;
        }

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "体检 {0} 分，{1} 项结果", Score, Items.Count);
    }
}