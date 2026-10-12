using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WinKit.App.Ai;
using WinKit.App.Mvvm;
using WinKit.App.Services;
using WinKit.Core.Ai;
using WinKit.Core.Cleanup;
using WinKit.Core.Diagnostics;
using WinKit.Core.Journal;
using WinKit.Core.Rules;
using WinKit.Core.Serialization;
using WinKit.Core.Tasks;
using WinKit.Core.Text;
using WinKit.Platform.Cleanup;
using WinKit.Platform.Security;

namespace WinKit.App.ViewModels
{
    /// <summary>
    /// 垃圾清理页的 ViewModel。
    ///
    /// 本页的产品承诺是「危险操作必须先预览、可回滚」，所以三条硬约束都落在这个类里：
    ///
    /// 1) **先预览后执行**：扫描只收集候选，绝不删除；执行前必须弹确认框，把"选中多少项 / 合计多大 /
    ///    用什么方式处置 / 能不能撤销"逐条说清楚。彻底删除（Permanent）额外多一道更严厉的确认。
    /// 2) **默认最安全**：默认处置方式是可一键还原的"隔离区"，候选默认全部勾选
    ///    （用户必须显式取消，而不是显式勾选才知道自己删了什么）。
    /// 3) **只读审计模式一刀切**：执行、撤销、清空回收站、清空隔离区四个入口全部拦截，界面按钮同时禁用。
    ///
    /// 线程模型：扫描与执行都交给 TaskRunner 在后台跑，进度/完成回调由 TaskRunner 封送回 UI 线程，
    /// 因此这里刷新 ObservableCollection 不需要自己再封送 Dispatcher。
    /// </summary>
    public sealed class CleanupViewModel : ObservableObject
    {
        private const string ScanTaskId = "cleanup.scan";
        private const string ExecuteTaskId = "cleanup.execute";
        private const int MaxItemsPerRule = 20000;

        /// <summary>提权清理任务标识，与 App 的 headless 分发器、ElevatedCleanupPayload.TaskId 三处必须一致。</summary>
        private const string ElevatedCleanupTaskId = ElevatedCleanupPayload.TaskId;

        private const string UnknownText = "—";
        private const string NotScannedText = "尚未扫描";
        private const string ReadyText = "就绪：请先扫描。扫描只是列出候选，不会删除任何东西。";

        // ---- AI 助手（可选联网）----

        private const string AiTitle = "用 AI 分析规则";

        /// <summary>可用时按钮旁边那行小字：必须如实说明"会联网 + 发送前先给你看"。</summary>
        private const string AiAvailableHint = "会联网：将先显示要发送的内容，确认后才联网。";

        private const string AiEnableHint = "可在「设置 → AI 助手」启用并填写 API Key。";

        private string _aiReplyText = string.Empty;
        private bool _hasAiReply;

        /// <summary>当前的取消源（扫描或执行共用）。不为 null 表示有任务在跑。</summary>
        private CancellationTokenSource? _cts;

        /// <summary>最近一次成功执行的批次号，供"撤销上一次清理"使用。</summary>
        private string? _lastBatchId;

        /// <summary>批量修改勾选状态时抑制逐条统计刷新（否则 2 万行会触发 2 万次字符串拼接）。</summary>
        private bool _suppressStats;

        /// <summary>处置方式用"当前哪一种"这一个字段表示，三个 bool 属性都由它推导，天生互斥。</summary>
        private CleanupDisposal _disposal = CleanupDisposal.Quarantine;

        /// <summary>
        /// 需要管理员权限的规则 Id 集合，随规则表一起在构造时定下来（规则目录是静态数据，不会变）。
        /// 提权入口每次勾选都要用它判断"这一项要不要提权"，所以预先建好而不是每次现算。
        /// </summary>
        private readonly HashSet<string> _adminRuleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>选中的候选中"需要管理员权限"的项数。由 UpdateSelectionStats 在同一个循环里数出来。</summary>
        private int _selectedAdminCount;

        private bool _isScanning;
        private bool _isExecuting;
        private bool _storageRefreshRunning;
        private double _progress;
        private string _progressText = string.Empty;
        private string _statusText = ReadyText;
        private string _selectedSummary = "已选中 0 项，共 0 B";
        private string _quarantineInfoText = "隔离区：正在读取…";
        private string _recycleBinInfoText = "回收站：正在读取…";
        private string _lastResultText = string.Empty;

        public CleanupViewModel()
        {
            // 规则目录是纯数据（无副作用），构造时建好：界面一打开就能看到"到底要清理什么"
            foreach (var rule in CleanupRuleCatalog.CreateDefault())
            {
                Rules.Add(new RuleRow(rule));
                if (rule.RequiresAdmin) _adminRuleIds.Add(rule.Id);
            }

            ScanCommand = new RelayCommand(StartScan, CanStartScan);
            CancelCommand = new RelayCommand(Cancel, CanCancel);
            SelectAllCommand = new RelayCommand(SelectAll, CanSelectAny);
            SelectNoneCommand = new RelayCommand(SelectNone, CanSelectAny);
            ExecuteCommand = new RelayCommand(Execute, CanExecuteCleanup);
            ElevateAndCleanCommand = new RelayCommand(ElevateAndClean, CanElevate);
            UndoLastCommand = new RelayCommand(UndoLast, CanUndoLast);
            EmptyRecycleBinCommand = new RelayCommand(EmptyRecycleBin, CanEmptyRecycleBin);
            PurgeQuarantineCommand = new RelayCommand(PurgeQuarantine, CanPurgeQuarantine);
            OpenQuarantineFolderCommand = new RelayCommand(OpenQuarantineFolder, CanOpenQuarantineFolder);
            RefreshStorageCommand = new RelayCommand(RecalculateStorageInfo);

            // AI 入口：CanExecute 里**故意不判断 AiLauncher.IsAvailable** ——
            // 不可用时按钮保持可见、可点，点下去只弹"为什么不能用"。
            // 本命令只读已勾选的规则，不会触发扫描或清理。
            AnalyzeRulesWithAiCommand = new RelayCommand(AnalyzeRulesWithAi);
            CopyAiReplyCommand = new RelayCommand(CopyAiReply);
            ClearAiReplyCommand = new RelayCommand(ClearAiReply);
        }

        // ================================================================ 包装类

        /// <summary>
        /// 界面上的一条清理规则。包装 CleanupRule 的原因：显示文本（扫描结果摘要）
        /// 与交互状态（是否勾选）都是界面概念，不该塞进 Core 的领域模型。
        /// </summary>
        public sealed class RuleRow : ObservableObject
        {
            private bool _isEnabled;
            private string _summaryText = NotScannedText;
            private bool _hasResult;

            public RuleRow(CleanupRule rule)
            {
                Rule = rule ?? throw new ArgumentNullException(nameof(rule));
                _isEnabled = rule.DefaultEnabled;
            }

            public CleanupRule Rule { get; }

            public string Id => Rule.Id;

            public string Name => Rule.Name;

            public string Description => Rule.Description;

            public string Impact => Rule.Impact;

            public string AdminHint => Rule.AdminHint;

            public bool RequiresAdmin => Rule.RequiresAdmin;

            /// <summary>是否勾选参与扫描。初值取规则自身的默认值（需要管理员的规则默认关闭）。</summary>
            public bool IsEnabled
            {
                get => _isEnabled;
                set => SetProperty(ref _isEnabled, value);
            }

            /// <summary>扫描前显示"尚未扫描"；扫描后取对应 CleanupRuleSummary.SummaryText。</summary>
            public string SummaryText
            {
                get => _summaryText;
                private set => SetProperty(ref _summaryText, value);
            }

            /// <summary>是否已经有扫描结果，界面据此区分"没有找到"和"还没扫"。</summary>
            public bool HasResult
            {
                get => _hasResult;
                private set => SetProperty(ref _hasResult, value);
            }

            public void ApplyResult(CleanupRuleSummary? summary)
            {
                if (summary == null)
                {
                    ResetResult();
                    return;
                }
                HasResult = true;
                SummaryText = summary.SummaryText;
            }

            public void ResetResult()
            {
                HasResult = false;
                SummaryText = NotScannedText;
            }
        }

        /// <summary>
        /// 界面上的一个待清理项。直接持有 Core 的 CleanupCandidate（而不是复制一份），
        /// 执行时把这些原对象交给 CleanupExecutor 即可，不会出现两份状态不一致。
        /// </summary>
        public sealed class CandidateRow : ObservableObject
        {
            private readonly CleanupViewModel _owner;

            public CandidateRow(CleanupCandidate candidate, CleanupViewModel owner)
            {
                Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            }

            public CleanupCandidate Candidate { get; }

            public string RuleName => Candidate.RuleName;

            public string Path => Candidate.Path;

            public string SizeText => Candidate.SizeText;

            public string LastWriteText => Candidate.LastWriteText;

            public bool IsDirectory => Candidate.IsDirectory;

            public string KindText => Candidate.IsDirectory ? "目录" : "文件";

            /// <summary>
            /// 勾选状态。写回 Core 对象的同时刷新"已选中 N 项"，
            /// 并让"执行清理"按钮的可用状态跟着变（0 项时必须变灰）。
            /// </summary>
            public bool Selected
            {
                get => Candidate.Selected;
                set
                {
                    if (Candidate.Selected == value) return;
                    Candidate.Selected = value;
                    OnPropertyChanged();

                    if (_owner._suppressStats) return;
                    _owner.UpdateSelectionStats();
                    RelayCommand.RaiseCanExecuteChanged();
                }
            }
        }

        // ================================================================ 只读 / 集合

        /// <summary>只读审计模式。界面用它显示提示条并禁用所有写操作按钮。</summary>
        public bool IsReadOnly => AppServices.IsReadOnly;

        public string ReadOnlyNotice => "只读审计模式已开启：清理、清空回收站、清空隔离区均被禁用";

        public ObservableCollection<RuleRow> Rules { get; } = new ObservableCollection<RuleRow>();

        public ObservableCollection<CandidateRow> Candidates { get; } = new ObservableCollection<CandidateRow>();

        // ================================================================ 状态属性

        public bool IsScanning
        {
            get => _isScanning;
            private set
            {
                if (!SetProperty(ref _isScanning, value)) return;
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(ShowProgress));
            }
        }

        public bool IsExecuting
        {
            get => _isExecuting;
            private set
            {
                if (!SetProperty(ref _isExecuting, value)) return;
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(ShowProgress));
            }
        }

        /// <summary>扫描或执行正在进行。界面上所有危险按钮都要看它。</summary>
        public bool IsBusy => _isScanning || _isExecuting;

        /// <summary>进度条是否可见。</summary>
        public bool ShowProgress => IsBusy;

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

        /// <summary>形如"已选中 128 项，共 1.2 GB"。</summary>
        public string SelectedSummary
        {
            get => _selectedSummary;
            private set => SetProperty(ref _selectedSummary, value);
        }

        /// <summary>最近一次执行结果摘要。除了弹窗，页面上也留一句，方便回看。</summary>
        public string LastResultText
        {
            get => _lastResultText;
            private set
            {
                if (!SetProperty(ref _lastResultText, value)) return;
                OnPropertyChanged(nameof(HasLastResult));
            }
        }

        public bool HasLastResult => !string.IsNullOrEmpty(_lastResultText);

        // ================================================================ 处置方式（三选一）

        // 下面三个属性都由 _disposal 推导，天然互斥。
        //
        // 为什么 setter 里 false 不用处理：RadioButton 同组时，WPF 在选中新的那个之前
        // 会先把原来选中的置为 false，并把 false 写回绑定 —— 如果我们在这里也响应 false，
        // 就会出现"先清空、再设置"的瞬时非法状态。只认 true，再让 SetDisposal 统一收口，
        // 三个属性的 PropertyChanged 一起发出，界面按唯一事实重新对账。
        public bool UseQuarantine
        {
            get => _disposal == CleanupDisposal.Quarantine;
            set { if (value) SetDisposal(CleanupDisposal.Quarantine); }
        }

        public bool UseRecycleBin
        {
            get => _disposal == CleanupDisposal.RecycleBin;
            set { if (value) SetDisposal(CleanupDisposal.RecycleBin); }
        }

        public bool UsePermanent
        {
            get => _disposal == CleanupDisposal.Permanent;
            set { if (value) SetDisposal(CleanupDisposal.Permanent); }
        }

        /// <summary>由三个互斥开关推导出来的处置方式。</summary>
        public CleanupDisposal Disposal => _disposal;

        /// <summary>当前处置方式的后果说明。页面上的解释与确认框共用同一份文案，避免两处口径不一致。</summary>
        public string DisposalExplanation => DisposalDescription(_disposal);

        public string QuarantineInfoText
        {
            get => _quarantineInfoText;
            private set => SetProperty(ref _quarantineInfoText, value);
        }

        public string RecycleBinInfoText
        {
            get => _recycleBinInfoText;
            private set => SetProperty(ref _recycleBinInfoText, value);
        }

        /// <summary>三个开关最终都落到同一个字段，因此永远互斥，不会出现"两个同时选中"。</summary>
        private void SetDisposal(CleanupDisposal disposal)
        {
            if (_disposal == disposal) return;
            _disposal = disposal;

            OnPropertyChanged(nameof(UseQuarantine));
            OnPropertyChanged(nameof(UseRecycleBin));
            OnPropertyChanged(nameof(UsePermanent));
            OnPropertyChanged(nameof(Disposal));
            OnPropertyChanged(nameof(DisposalExplanation));
            NotifyElevationChanged();
        }

        /// <summary>处置方式的中文说法（用于"处置方式：xxx"这一行）。</summary>
        private static string DisposalName(CleanupDisposal disposal)
        {
            switch (disposal)
            {
                case CleanupDisposal.RecycleBin: return "回收站";
                case CleanupDisposal.Permanent: return "彻底删除";
                default: return "隔离区";
            }
        }

        /// <summary>
        /// 处置方式的后果说明。文案按产品要求写死：
        /// 隔离区 →"移入 WinKit 隔离区，可在撤销中心一键还原"；
        /// 回收站 →"移入 Windows 回收站，WinKit 内无法撤销，需手动从回收站还原"；
        /// 彻底删除 →"永久删除，无法恢复"。
        /// </summary>
        private static string DisposalDescription(CleanupDisposal disposal)
        {
            switch (disposal)
            {
                case CleanupDisposal.RecycleBin:
                    return "移入 Windows 回收站，WinKit 内无法撤销，需手动从回收站还原";
                case CleanupDisposal.Permanent:
                    return "永久删除，无法恢复";
                default:
                    return "移入 WinKit 隔离区，可在撤销中心一键还原";
            }
        }

        // ================================================================ 命令

        public RelayCommand ScanCommand { get; }

        public RelayCommand CancelCommand { get; }

        public RelayCommand SelectAllCommand { get; }

        public RelayCommand SelectNoneCommand { get; }

        public RelayCommand ExecuteCommand { get; }

        /// <summary>
        /// 以管理员身份清理：只把选中项里"需要管理员权限"的那些交给提权子进程。
        /// 普通项不走提权 —— 弹一次 UAC 只为几个临时文件，用户下次会条件反射地点"是"。
        /// </summary>
        public RelayCommand ElevateAndCleanCommand { get; }

        public RelayCommand UndoLastCommand { get; }

        public RelayCommand EmptyRecycleBinCommand { get; }

        public RelayCommand PurgeQuarantineCommand { get; }

        public RelayCommand OpenQuarantineFolderCommand { get; }

        /// <summary>刷新回收站/隔离区信息。合成一个命令，免得两张卡片各放一个同名按钮。</summary>
        public RelayCommand RefreshStorageCommand { get; }

        public RelayCommand AnalyzeRulesWithAiCommand { get; }

        public RelayCommand CopyAiReplyCommand { get; }

        public RelayCommand ClearAiReplyCommand { get; }

        // ================================================================ 生命周期

        /// <summary>
        /// 页面装载时调用：刷新隔离区/回收站信息。
        /// 这里**刻意不起定时器** —— 两处的大小只会被本页自己的操作改变，
        /// 用完主动刷新即可，定时轮询纯属浪费磁盘 IO。
        /// </summary>
        public void Start()
        {
            // 页面被缓存复用：每次重新显示都刷一次 AI 可用性小字（用户可能刚在设置里启用）
            RefreshAiAvailability();

            UpdateSelectionStats();
            RecalculateStorageInfo();
            StatusText = ReadyText;
        }

        /// <summary>
        /// 页面卸载时调用：取消正在进行的扫描/执行。
        /// 没有定时器要停，但后台任务必须放掉，否则用户切页后任务还在跑并往 UI 线程投进度。
        /// </summary>
        public void Stop()
        {
            CancelInternal();
        }

        // ================================================================ 扫描

        private bool CanStartScan() => !IsBusy;

        private void StartScan()
        {
            if (IsBusy) return;

            var selectedRules = new List<CleanupRule>();
            foreach (var row in Rules)
            {
                if (row.IsEnabled) selectedRules.Add(row.Rule);
            }

            // 一条规则都没勾选时必须明确提示，不能"扫出 0 项"让用户以为磁盘很干净
            if (selectedRules.Count == 0)
            {
                MessageBox.Show(
                    "请至少勾选一条清理规则。\r\n\r\n"
                    + "没有勾选任何规则时无法扫描 —— 否则页面会显示「没有可清理的内容」，"
                    + "让人误以为磁盘很干净。",
                    "垃圾清理",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // 重新扫描 = 丢弃上一次的候选与摘要，避免"旧结果 + 新结果"混在一起
            Candidates.Clear();
            foreach (var row in Rules) row.ResetResult();
            UpdateSelectionStats();

            IsScanning = true;
            Progress = 0d;
            ProgressText = "正在准备扫描…";
            StatusText = "正在扫描 " + selectedRules.Count + " 条规则…";

            // 结果用局部变量承接：worker 跑在后台线程，写它自己的局部变量；
            // 完成回调在 UI 线程读 —— Task 完成本身保证了两个线程之间的可见性。
            CleanupScanResult? result = null;
            _cts = TaskRunner.Run(
                ScanTaskId,
                ctx => { result = ScanWorker(selectedRules, ctx); },
                OnProgress,
                error => OnScanCompleted(result, error));
        }

        /// <summary>
        /// 后台扫描。**只收集候选，绝不删除**：真正的删除在 CleanupExecutor 里，
        /// 中间隔着用户确认这一步。取消时返回 null。
        /// </summary>
        private static CleanupScanResult? ScanWorker(IReadOnlyList<CleanupRule> rules, TaskContext ctx)
        {
            var options = new ScanOptions { MaxItemsPerRule = MaxItemsPerRule };

            // 必须排除 WinKit 自己的数据目录：隔离区就在 <数据目录>\quarantine 下，
            // 一旦数据目录被某条规则命中（例如用户的 %TEMP% 恰好落在数据目录里），
            // 清理临时文件就会把隔离区自己删掉 —— 那等于把"可撤销"的退路一起删了。
            var dataDirectory = AppServices.DataDirectoryText;
            if (!string.IsNullOrWhiteSpace(dataDirectory)
                && !string.Equals(dataDirectory, UnknownText, StringComparison.Ordinal))
            {
                options.ExcludeRoots.Add(dataDirectory);
            }

            var scanner = new CleanupScanner(AppServices.Guard);
            try
            {
                return scanner.Scan(rules, ctx, options);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        private void OnScanCompleted(CleanupScanResult? result, Exception? error)
        {
            IsScanning = false;
            _cts = null;

            if (error != null)
            {
                Progress = 0d;
                ProgressText = string.Empty;
                StatusText = "扫描失败：" + error.Message;
                Log.Error("垃圾清理扫描失败：" + error.Message, error);
                MessageBox.Show("扫描失败：" + error.Message, "垃圾清理", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (result == null)
            {
                // 被用户取消：扫描器在规则之间检查取消标记，中途放弃时不产出结果
                Progress = 0d;
                ProgressText = "已取消扫描";
                StatusText = "已取消扫描";
                foreach (var row in Rules) row.ResetResult();
                UpdateSelectionStats();
                return;
            }

            ApplyScanResult(result);
        }

        private void ApplyScanResult(CleanupScanResult result)
        {
            // 结果按规则分组返回，这里按 Id 回填到对应的规则行
            var byRuleId = new Dictionary<string, CleanupRuleSummary>(StringComparer.OrdinalIgnoreCase);
            foreach (var summary in result.Summaries)
            {
                if (summary.Rule == null) continue;
                byRuleId[summary.Rule.Id] = summary;
            }

            foreach (var row in Rules)
            {
                CleanupRuleSummary? summary;
                row.ApplyResult(byRuleId.TryGetValue(row.Id, out summary) ? summary : null);
            }

            // 重建候选列表：默认全部选中（用户必须显式取消，而不是显式勾选才知道自己删了什么）
            _suppressStats = true;
            try
            {
                Candidates.Clear();
                foreach (var candidate in result.AllCandidates())
                {
                    Candidates.Add(new CandidateRow(candidate, this));
                }
            }
            finally
            {
                _suppressStats = false;
            }

            Progress = 1d;
            ProgressText = "扫描完成：共 " + result.TotalItems + " 项，" + ByteSize.Format(result.TotalSize);
            StatusText = "扫描完成：可清理 " + result.TotalItems + " 项，共 " + ByteSize.Format(result.TotalSize)
                + "。请核对下面的清单，确认后再执行。";
            UpdateSelectionStats();
            NotifyElevationChanged();
        }

        private void OnProgress(TaskProgress progress)
        {
            // TaskRunner 用 Progress<T> 封送，回调在 UI 线程执行，可以直接改属性
            Progress = progress.IsIndeterminate ? 0d : progress.Fraction;
            if (!string.IsNullOrEmpty(progress.Message)) ProgressText = progress.Message!;
        }

        private bool CanCancel() => IsBusy;

        /// <summary>取消扫描/执行。取消是协作式的：扫描器在每条规则、每个目标之间检查取消标记。</summary>
        private void Cancel()
        {
            if (!IsBusy) return;
            CancelInternal();
            StatusText = "正在取消…（当前操作会尽快停下来）";
        }

        private void CancelInternal()
        {
            var cts = _cts;
            _cts = null;
            if (cts == null) return;
            try { cts.Cancel(); }
            catch (Exception ex) { Log.Debug("取消清理任务时出错：" + ex.Message); }
        }

        // ================================================================ 勾选

        private bool CanSelectAny() => Candidates.Count > 0 && !IsExecuting;

        private void SelectAll() => SetAllSelected(true);

        private void SelectNone() => SetAllSelected(false);

        /// <summary>
        /// 批量勾选。刻意抑制逐条统计刷新：2 万个候选逐条拼字符串会让界面明显卡顿，
        /// 所以整体改完再算一次。
        /// </summary>
        private void SetAllSelected(bool selected)
        {
            _suppressStats = true;
            try
            {
                foreach (var row in Candidates) row.Selected = selected;
            }
            finally
            {
                _suppressStats = false;
            }

            UpdateSelectionStats();
            RelayCommand.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// 重算"已选中 N 项，共 X"，顺带把"其中有多少项需要管理员权限"一起数出来。
        /// 两者在同一个循环里完成：勾选是逐条触发的（2 万行全选就是 2 万次），
        /// 多走一遍列表、多建一个 HashSet 都会实打实地拖慢界面。
        /// </summary>
        private void UpdateSelectionStats()
        {
            var count = 0;
            var adminCount = 0;
            long size = 0;
            foreach (var row in Candidates)
            {
                if (!row.Selected) continue;
                count++;
                size += row.Candidate.Size;
                if (_adminRuleIds.Contains(row.Candidate.RuleId)) adminCount++;
            }

            _selectedAdminCount = adminCount;
            SelectedSummary = "已选中 " + count + " 项，共 " + ByteSize.Format(size);

            // 提权提示与"以管理员身份清理"按钮都依赖"选中了哪些项"，这里统一跟着刷新
            NotifyElevationChanged();
        }

        // ================================================================ 提权清理

        /// <summary>
        /// "以管理员身份清理"是否可用：只读模式/正在执行/已经是管理员时都不可用，
        /// 而且必须**确实选中了需要管理员的项**才点亮 —— 否则按钮常年亮着，
        /// 用户点下去只会弹出"选中的项都不需要管理员权限"。
        /// </summary>
        public bool CanElevate()
        {
            if (IsReadOnly) return false;
            if (IsExecuting) return false;
            if (AppServices.IsElevated) return false;
            return _selectedAdminCount > 0;
        }

        /// <summary>
        /// 一行面向界面的提示，把"现在能不能提权、为什么"说清楚。
        /// 纯计算属性（不落字段），唯一事实来源始终是选中项与规则表，不需要额外维护状态。
        /// </summary>
        public string ElevationHint
        {
            get
            {
                if (AppServices.IsElevated)
                {
                    return "当前已是管理员权限，直接点「执行清理」即可。";
                }

                if (_selectedAdminCount > 0)
                {
                    return "选中的项里有 " + _selectedAdminCount + " 项需要管理员权限，可点「以管理员身份清理」。";
                }
                return "未选中需要管理员权限的项目。";
            }
        }

        /// <summary>
        /// 收集选中的、需要管理员权限的候选。
        /// 返回值就是**传给提权子进程的全部内容** —— 普通项不进载荷，所以提权进程能碰的文件面尽可能小。
        /// </summary>
        private List<CleanupCandidate> CollectSelectedAdminItems()
        {
            var list = new List<CleanupCandidate>();
            if (_adminRuleIds.Count == 0) return list;

            foreach (var row in Candidates)
            {
                if (!row.Selected) continue;
                if (_adminRuleIds.Contains(row.Candidate.RuleId)) list.Add(row.Candidate);
            }
            return list;
        }

        /// <summary>选中项/执行状态变化后调用：让依赖选中内容的提权提示与按钮可用性重新求值。</summary>
        private void NotifyElevationChanged()
        {
            OnPropertyChanged(nameof(ElevationHint));
            OnPropertyChanged(nameof(CanElevate));
            RelayCommand.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// 以管理员身份清理。流程刻意与 Execute() 保持同一套节奏：
        /// 只读拦截 → 空选拦截 → 预览确认（默认取消）→ 后台提权 → 完成回调。
        /// </summary>
        private void ElevateAndClean()
        {
            if (IsReadOnly)
            {
                MessageBox.Show(
                    "只读审计模式已开启，无法执行清理。\r\n\r\n请先到「设置 → 安全」关闭只读审计模式。",
                    "垃圾清理",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (IsExecuting) return;

            // 只提权处理"需要管理员"的那些项：普通项交给普通清理即可，没必要为它们弹一次 UAC
            var adminItems = CollectSelectedAdminItems();
            if (adminItems.Count == 0)
            {
                MessageBox.Show(
                    "选中的项都不需要管理员权限，直接点「执行清理」即可。",
                    "垃圾清理",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            long totalSize = 0;
            foreach (var candidate in adminItems) totalSize += candidate.Size;

            var disposal = _disposal;
            var sizeText = ByteSize.Format(totalSize);

            var confirm = new StringBuilder();
            confirm.Append("即将以管理员身份清理以下内容：\r\n\r\n");
            confirm.Append("需要管理员权限的项：").Append(adminItems.Count).Append(" 项\r\n");
            confirm.Append("合计大小：").Append(sizeText).Append("\r\n");
            confirm.Append("处置方式：").Append(DisposalName(disposal)).Append("\r\n\r\n");
            confirm.Append("接下来会弹出 Windows 的 UAC 授权窗口，需要你点「是」才会继续；\r\n");
            confirm.Append("点了「否」则什么都不会发生。\r\n\r\n");
            confirm.Append("只处理上面这批需要管理员权限的项，本次勾选中的其他普通项不会被动到。\r\n\r\n");

            if (disposal == CleanupDisposal.Quarantine)
            {
                confirm.Append("清理结果会进 WinKit 隔离区，可在「撤销中心」一键还原。\r\n\r\n");
            }
            else
            {
                confirm.Append("清理结果会进 Windows 回收站，WinKit 内无法撤销，需要到回收站手动还原。\r\n\r\n");
            }

            confirm.Append("请对照清单再确认一遍：确认无误点「确定」，否则点「取消」。");

            var answer = ShowConfirm(confirm.ToString(), "以管理员身份清理确认", MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;

            // ---- 后台提权 + 执行。UAC 弹窗期间界面必须禁用重复点击 ----
            IsExecuting = true;
            Progress = 0d;
            ProgressText = "正在等待管理员授权…";
            StatusText = "已请求管理员权限，请在 UAC 窗口中确认…（" + adminItems.Count + " 项，" + sizeText + "）";
            NotifyElevationChanged();

            // 载荷只装结构化字段，绝不拼命令行字符串 —— 提权侧只认 ElevatedCleanupPayload 这个契约
            var payload = new ElevatedCleanupPayload
            {
                Items = new List<ElevatedCleanupItem>(adminItems.Count),
                Disposal = disposal.ToString(),
            };
            foreach (var candidate in adminItems)
            {
                payload.Items.Add(new ElevatedCleanupItem
                {
                    Path = candidate.Path,
                    Size = candidate.Size,
                    IsDirectory = candidate.IsDirectory,
                    RuleId = candidate.RuleId,
                });
            }

            var payloadJson = Json.Serialize(payload);
            var requested = adminItems.ToList();

            // 结果用局部变量承接，由 TaskRunner 的完成回调（已封送回 UI 线程）读取 —— 与扫描/执行同一套写法
            ElevatedOutcome? outcome = null;

            // RunElevatedTask 会阻塞等待提权子进程（含 UAC 弹窗），必须在后台线程调用
            _cts = TaskRunner.Run(
                ElevatedCleanupTaskId,
                ctx =>
                {
                    outcome = Elevation.RunElevatedTask(
                        ElevatedCleanupTaskId,
                        payloadJson,
                        TimeSpan.FromMinutes(5));
                },
                null,
                error => OnElevateCompleted(outcome, requested, error));
        }

        /// <summary>
        /// 提权清理的完成回调。TaskRunner 会把回调封送回 UI 线程，所以这里可以直接改属性与集合。
        /// 第一件事必须是复位 IsExecuting：否则一旦中途返回，按钮就永远灰着了。
        ///
        /// 这里**刻意不写操作日志**：提权子进程与父进程共用同一份数据目录与 journal 文件，
        /// CleanupExecutor 在搬移每一项时已经写过 JournalEntry 了。父进程再记一笔，
        /// 「撤销中心」里就会出现两份记录（而且父进程那份没有 UndoKind，根本撤不了）。
        /// </summary>
        private void OnElevateCompleted(ElevatedOutcome? outcome, List<CleanupCandidate> requested, Exception? error)
        {
            IsExecuting = false;
            _cts = null;

            try
            {
                if (error != null)
                {
                    Progress = 0d;
                    ProgressText = string.Empty;
                    StatusText = "提权清理失败：" + error.Message;
                    Log.Error("提权清理执行失败：" + error.Message, error);
                    MessageBox.Show(
                        "提权清理失败：" + error.Message,
                        "垃圾清理",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (outcome == null)
                {
                    Progress = 0d;
                    ProgressText = string.Empty;
                    StatusText = "提权清理未能执行：没有拿到提权结果。";
                    Log.Warn("提权清理未返回结果");
                    MessageBox.Show(
                        "提权清理未能执行：没有拿到提权结果。",
                        "垃圾清理",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (outcome.UserCancelled)
                {
                    Progress = 0d;
                    ProgressText = string.Empty;
                    StatusText = "已取消：未获得管理员权限。";
                    Log.Info("提权清理：用户取消了 UAC 授权");
                    MessageBox.Show(
                        "已取消清理：没有获得管理员权限，本次没有改动任何文件。\r\n\r\n"
                        + "需要管理员权限的项可以改用标准权限方式处理，或者右键以管理员身份重新运行 WinKit。",
                        "垃圾清理",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                Progress = 1d;
                ProgressText = outcome.Success ? "提权清理完成" : "提权清理未完全成功";

                if (outcome.Success)
                {
                    var message = new StringBuilder();
                    message.Append(string.IsNullOrEmpty(outcome.Output) ? "提权清理已完成。" : outcome.Output);
                    message.Append("\r\n\r\n清理结果可在「撤销中心」一键还原。");

                    LastResultText = message.ToString();
                    StatusText = "提权清理完成：" + (string.IsNullOrEmpty(outcome.Output) ? "已按要求处理。" : outcome.Output);

                    MessageBox.Show(
                        message.ToString(),
                        "提权清理完成",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    var reason = !string.IsNullOrEmpty(outcome.Error) ? outcome.Error : outcome.Output;
                    var text = "提权清理未完成：" + (string.IsNullOrEmpty(reason) ? "提权子进程未返回具体原因。" : reason);

                    LastResultText = text;
                    StatusText = text;

                    MessageBox.Show(text, "提权清理未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                if (outcome.Success)
                {
                    // 【已知取舍】父进程拿不到提权子进程的**逐项**结果（只回一句汇总 + 批次号），
                    // 所以这里只能把"选中项里需要管理员权限的那些"整批从清单移除。
                    // 子进程那边对不存在/被占用的项是"跳过并计入失败"的 —— 那些项其实还在磁盘上，
                    // 但清单里已经看不到了。取舍的理由：这批项本来就已经被用户确认过要清理，
                    // 留在清单里只会在下一次扫描时原样出现，反而更容易让人以为"点了没反应"。
                    // 想核对真实结果的用户，可以看「撤销中心」的批次记录，或直接重新扫描一遍。
                    RemoveElevatedCandidates(requested);

                    if (requested.Count > 0)
                    {
                        Log.Info("提权清理完成，已从清单移除 " + requested.Count + " 项（子进程未回传逐项结果，按整批移除）");
                    }
                }
            }
            finally
            {
                RefreshStorageAsync();
                NotifyElevationChanged();
            }
        }

        /// <summary>把提权清理涉及的那批候选从清单里移除，并重算统计。</summary>
        private void RemoveElevatedCandidates(List<CleanupCandidate> requested)
        {
            if (requested.Count == 0) return;

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in requested)
            {
                if (!string.IsNullOrEmpty(candidate.Path)) paths.Add(candidate.Path);
            }

            _suppressStats = true;
            try
            {
                for (var i = Candidates.Count - 1; i >= 0; i--)
                {
                    if (paths.Contains(Candidates[i].Candidate.Path)) Candidates.RemoveAt(i);
                }
            }
            finally
            {
                _suppressStats = false;
            }

            UpdateSelectionStats();
            RelayCommand.RaiseCanExecuteChanged();
        }

        // ================================================================ 执行

        private bool CanExecuteCleanup()
        {
            if (IsReadOnly || IsExecuting) return false;
            return CountSelected() > 0;
        }

        private int CountSelected()
        {
            var count = 0;
            foreach (var row in Candidates)
            {
                if (row.Selected) count++;
            }
            return count;
        }

        private List<CleanupCandidate> CollectSelected()
        {
            var list = new List<CleanupCandidate>();
            foreach (var row in Candidates)
            {
                if (row.Selected) list.Add(row.Candidate);
            }
            return list;
        }

        /// <summary>
        /// 执行清理。顺序不可调换：
        /// 只读拦截 → 空选拦截 → 预览确认（默认"取消"）→ 彻底删除再加一道确认 → 后台执行。
        /// </summary>
        private void Execute()
        {
            if (IsReadOnly)
            {
                MessageBox.Show(
                    "只读审计模式已开启，无法执行清理。\r\n\r\n请先到「设置 → 安全」关闭只读审计模式。",
                    "垃圾清理",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var selected = CollectSelected();
            if (selected.Count == 0)
            {
                MessageBox.Show(
                    "请至少勾选一个待清理项。\r\n\r\n本页不会替你决定删什么 —— 每一条都会在清单里列出来。",
                    "垃圾清理",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (IsExecuting) return;

            long totalSize = 0;
            foreach (var candidate in selected) totalSize += candidate.Size;

            var disposal = _disposal;

            // ---- 第一道：预览确认（默认按钮是"取消"）----
            var confirm = new StringBuilder();
            confirm.Append("即将清理以下内容：\r\n\r\n");
            confirm.Append("选中项数：").Append(selected.Count).Append(" 项\r\n");
            confirm.Append("合计大小：").Append(ByteSize.Format(totalSize)).Append("\r\n");
            confirm.Append("处置方式：").Append(DisposalName(disposal)).Append("（").Append(DisposalDescription(disposal)).Append("）\r\n\r\n");

            if (disposal == CleanupDisposal.Quarantine)
            {
                confirm.Append("清理完成后可在「撤销中心」按批次一键还原，误删也能找回。");
            }
            else if (disposal == CleanupDisposal.RecycleBin)
            {
                confirm.Append("WinKit 不会记录还原点；如需还原请到 Windows 回收站手动操作。");
            }
            else
            {
                confirm.Append("警告：彻底删除不经过回收站，也不会进入隔离区。");
            }

            confirm.Append("\r\n\r\n请对照上面的清单再确认一遍：确认无误点「确定」，否则点「取消」。");

            var answer = ShowConfirm(
                confirm.ToString(),
                disposal == CleanupDisposal.Permanent ? "执行清理（不可恢复）" : "执行清理确认",
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;

            // ---- 第二道：只有"彻底删除"才需要，文案更严厉 ----
            if (disposal == CleanupDisposal.Permanent)
            {
                var second =
                    "此操作不可撤销。\r\n\r\n"
                    + "接下来将永久删除 " + selected.Count + " 项（共 " + ByteSize.Format(totalSize) + "）：\r\n"
                    + "· 不进入 Windows 回收站\r\n"
                    + "· 不进入 WinKit 隔离区\r\n"
                    + "· 撤销中心里没有任何还原点\r\n\r\n"
                    + "确认要继续永久删除吗？";

                var finalAnswer = ShowConfirm(second, "永久删除确认（不可撤销）", MessageBoxImage.Stop);
                if (finalAnswer != MessageBoxResult.OK)
                {
                    StatusText = "已取消：永久删除未执行。";
                    return;
                }
            }

            // ---- 后台执行 ----
            IsExecuting = true;
            Progress = 0d;
            ProgressText = "正在清理…";
            StatusText = "正在清理 " + selected.Count + " 项…";

            CleanupExecutionResult? result = null;
            _cts = TaskRunner.Run(
                ExecuteTaskId,
                ctx => { result = ExecuteWorker(selected, disposal, ctx); },
                OnProgress,
                error => OnExecuteCompleted(result, error));
        }

        private static CleanupExecutionResult? ExecuteWorker(
            IReadOnlyList<CleanupCandidate> selected, CleanupDisposal disposal, TaskContext ctx)
        {
            var quarantine = AppServices.Quarantine;
            if (quarantine == null)
            {
                // 隔离区没就绪时宁可什么都不做，也不降级成"直接删除"
                throw new InvalidOperationException("隔离区尚未初始化，为避免不可撤销的删除，本次清理已中止。");
            }

            if (disposal == CleanupDisposal.Quarantine)
            {
                quarantine.EnsureCreated();
            }

            return CleanupExecutor.Execute(selected, disposal, quarantine, AppServices.Journal, ctx);
        }

        private void OnExecuteCompleted(CleanupExecutionResult? result, Exception? error)
        {
            IsExecuting = false;
            _cts = null;

            if (error != null)
            {
                Progress = 0d;
                ProgressText = string.Empty;
                StatusText = "清理失败：" + error.Message;
                Log.Error("垃圾清理执行失败：" + error.Message, error);
                MessageBox.Show("清理失败：" + error.Message, "垃圾清理", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (result == null)
            {
                Progress = 0d;
                ProgressText = "已取消清理";
                StatusText = "已取消清理";
                return;
            }

            Progress = 1d;
            ProgressText = result.SummaryText;

            var message = new StringBuilder();
            message.Append(string.IsNullOrEmpty(result.SummaryText) ? "清理已完成。" : result.SummaryText);

            if (result.Disposal == CleanupDisposal.Quarantine && result.SuccessCount > 0)
            {
                message.Append("\r\n\r\n已在撤销中心记录，可一键还原。");
            }
            if (result.Cancelled)
            {
                message.Append("\r\n\r\n本次操作被取消，已处理的部分保持生效。");
            }

            LastResultText = message.ToString();
            _lastBatchId = string.IsNullOrEmpty(result.BatchId) ? null : result.BatchId;

            // FreedBytes 是"成功处理项的大小合计"，不是磁盘真正释放的字节数：
            // 隔离区和回收站仍然占着磁盘，只有彻底删除才真的腾出空间。
            // 这里按处置方式分开措辞，免得用户以为点了清理磁盘就多出这么多空闲空间。
            var sizeLabel = result.Disposal == CleanupDisposal.Permanent ? "释放 " : "涉及 ";
            StatusText = "清理完成：成功 " + result.SuccessCount + " 项，失败 " + result.FailedCount
                + " 项，" + sizeLabel + ByteSize.Format(result.FreedBytes)
                + (string.IsNullOrEmpty(_lastBatchId) ? string.Empty : "（批次 " + _lastBatchId + "）");

            RemoveCleanedCandidates(result);
            RefreshStorageAsync();

            MessageBox.Show(
                message.ToString(),
                result.FailedCount > 0 ? "清理完成（有失败项）" : "清理完成",
                MessageBoxButton.OK,
                result.FailedCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        /// <summary>按路径匹配执行结果，把已清理掉的项从清单移除；失败的项留在清单里让用户看见。</summary>
        private void RemoveCleanedCandidates(CleanupExecutionResult result)
        {
            var cleaned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (result.Items != null)
            {
                foreach (var item in result.Items)
                {
                    if (item == null || !item.Success) continue;
                    if (!string.IsNullOrEmpty(item.Path)) cleaned.Add(item.Path);
                }
            }

            if (cleaned.Count == 0) return;

            _suppressStats = true;
            try
            {
                for (var i = Candidates.Count - 1; i >= 0; i--)
                {
                    if (cleaned.Contains(Candidates[i].Candidate.Path)) Candidates.RemoveAt(i);
                }
            }
            finally
            {
                _suppressStats = false;
            }

            UpdateSelectionStats();
            RelayCommand.RaiseCanExecuteChanged();
        }

        // ================================================================ 撤销

        private bool CanUndoLast()
        {
            if (IsReadOnly || IsExecuting) return false;
            return !string.IsNullOrEmpty(_lastBatchId) && AppServices.Undo != null;
        }

        /// <summary>撤销上一次清理。撤销本身就是一次文件搬回，放线程池执行，避免界面卡住。</summary>
        private async void UndoLast()
        {
            if (IsReadOnly)
            {
                MessageBox.Show(
                    "只读审计模式已开启，无法撤销。\r\n\r\n请先到「设置 → 安全」关闭只读审计模式。",
                    "垃圾清理",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var batchId = _lastBatchId;
            if (string.IsNullOrEmpty(batchId)) return;

            UndoResult result;
            try
            {
                result = await TaskRunner.RunAsync(() => AppServices.Undo.UndoBatch(batchId!)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Error("撤销清理批次失败：" + batchId + " → " + ex.Message, ex);
                MessageBox.Show("撤销失败：" + ex.Message, "垃圾清理", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(
                result.Message,
                result.Success ? "撤销完成" : "撤销未完成",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);

            StatusText = "撤销批次 " + batchId + "：" + result.Message;

            if (result.Success)
            {
                // 撤销后这批内容回到原位置，清单已经对不上了，所以清掉批次号并要求重新扫描
                _lastBatchId = null;
                LastResultText = result.Message + "（该批次已撤销，清单可能已过期，建议重新扫描）";
            }
            else
            {
                LastResultText = result.Message;
            }

            RefreshStorageAsync();
            RelayCommand.RaiseCanExecuteChanged();
        }

        // ================================================================ 回收站 / 隔离区

        private bool CanEmptyRecycleBin() => !IsReadOnly && !IsBusy && AppServices.Config != null;

        private void EmptyRecycleBin()
        {
            if (IsReadOnly) return;

            var answer = MessageBox.Show(
                "确认清空 Windows 回收站？\r\n\r\n"
                + "清空回收站不可撤销：回收站里的所有内容都会被永久删除，WinKit 无法帮你找回。\r\n\r\n"
                + "如果只是想清理本页勾选的临时文件，请用上面的「执行清理」按钮。",
                "清空回收站（不可撤销）",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK) return;

            string? error;
            var succeeded = RecycleBin.Empty(out error);

            if (!succeeded)
            {
                var reason = string.IsNullOrEmpty(error) ? "系统未返回具体原因。" : error!;
                Log.Warn("清空回收站失败：" + reason);
                MessageBox.Show("清空回收站失败：" + reason, "垃圾清理", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusText = "清空回收站失败：" + reason;
                return;
            }

            Log.Info("已清空 Windows 回收站");
            MessageBox.Show("回收站已清空。", "垃圾清理", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText = "已清空回收站";
            RefreshStorageAsync();
        }

        private bool CanPurgeQuarantine() => !IsReadOnly && !IsBusy && AppServices.Quarantine != null;

        private void PurgeQuarantine()
        {
            if (IsReadOnly) return;

            var quarantine = AppServices.Quarantine;
            if (quarantine == null) return;

            var itemCount = quarantine.GetItemCount();
            var totalSize = quarantine.GetTotalSize();

            if (itemCount == 0)
            {
                MessageBox.Show("隔离区是空的，没有需要清空的内容。", "垃圾清理", MessageBoxButton.OK, MessageBoxImage.Information);
                RecalculateStorageInfo();
                return;
            }

            // 第一次确认：说清后果
            var first = "确认清空隔离区？\r\n\r\n"
                + "隔离区当前有 " + itemCount + " 项，共 " + ByteSize.Format(totalSize) + "。\r\n"
                + "清空后这些内容将无法通过撤销中心还原。";

            var firstAnswer = ShowConfirm(first, "清空隔离区", MessageBoxImage.Warning);
            if (firstAnswer != MessageBoxResult.OK) return;

            // 第二次确认：把"永久"和"无法恢复"说死
            var second = "将永久删除隔离区中的所有内容，无法恢复。\r\n\r\n"
                + "涉及 " + itemCount + " 项、共 " + ByteSize.Format(totalSize) + "。\r\n"
                + "确认继续吗？";

            var secondAnswer = ShowConfirm(second, "清空隔离区（不可恢复）", MessageBoxImage.Stop);
            if (secondAnswer != MessageBoxResult.OK)
            {
                StatusText = "已取消：隔离区未被清空。";
                return;
            }

            long freed;
            int failed;
            var succeeded = quarantine.Purge(out freed, out failed);

            var text = "隔离区已清空：释放 " + ByteSize.Format(freed) + "。";
            if (failed > 0)
            {
                text += "\r\n\r\n有 " + failed + " 项无法删除（可能仍被程序占用），它们仍保留在隔离区目录中。";
            }
            if (!succeeded)
            {
                text = "清空隔离区未完全成功：释放 " + ByteSize.Format(freed) + "，失败 " + failed + " 项。";
            }

            Log.Info("清空隔离区：释放 " + ByteSize.Format(freed) + "，失败 " + failed + " 项");
            MessageBox.Show(
                text,
                succeeded ? "清空隔离区完成" : "清空隔离区未完全成功",
                MessageBoxButton.OK,
                failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            StatusText = "隔离区已清空：释放 " + ByteSize.Format(freed)
                + (failed > 0 ? "，失败 " + failed + " 项" : string.Empty);
            RefreshStorageAsync();
        }

        private bool CanOpenQuarantineFolder() => AppServices.Quarantine != null;

        private void OpenQuarantineFolder()
        {
            try
            {
                var root = AppServices.Quarantine == null ? null : AppServices.Quarantine.Root;
                if (string.IsNullOrEmpty(root))
                {
                    MessageBox.Show("隔离区目录尚未初始化。", "垃圾清理", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                Process.Start("explorer.exe", root!);
            }
            catch (Exception ex)
            {
                Log.Warn("打开隔离区目录失败：" + ex.Message, ex);
                MessageBox.Show(
                    "无法打开隔离区目录：" + ex.Message,
                    "垃圾清理",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        // ================================================================ 存储信息

        /// <summary>
        /// 重新读取隔离区与回收站的大小。目录枚举与 Shell 调用都走线程池，避免 UI 卡住；
        /// 完成后回到 UI 线程更新文案。
        /// </summary>
        private async void RefreshStorageAsync()
        {
            if (_storageRefreshRunning) return;
            _storageRefreshRunning = true;
            try
            {
                // 刚清空回收站时资源管理器还没重算，稍等一下数字才不会显示成旧值
                await Task.Delay(250).ConfigureAwait(true);

                int quarantineCount;
                long quarantineSize;
                try
                {
                    var quarantine = AppServices.Quarantine;
                    quarantineCount = quarantine == null ? 0 : quarantine.GetItemCount();
                    quarantineSize = quarantine == null ? 0L : quarantine.GetTotalSize();
                }
                catch (Exception ex)
                {
                    Log.Debug("读取隔离区信息失败：" + ex.Message);
                    quarantineCount = 0;
                    quarantineSize = 0L;
                }

                long recycleBinSize;
                try
                {
                    recycleBinSize = await TaskRunner.RunAsync(() => RecycleBin.GetSizeBytes()).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Log.Debug("读取回收站大小失败：" + ex.Message);
                    recycleBinSize = -1L;
                }

                QuarantineInfoText = "隔离区：" + quarantineCount + " 项 / " + ByteSize.Format(quarantineSize);
                RecycleBinInfoText = recycleBinSize < 0L
                    ? "回收站：无法读取（系统未返回大小）"
                    : "回收站：" + ByteSize.Format(recycleBinSize);
            }
            finally
            {
                _storageRefreshRunning = false;
            }
        }

        private void RecalculateStorageInfo()
        {
            QuarantineInfoText = "隔离区：正在读取…";
            RecycleBinInfoText = "回收站：正在读取…";
            RefreshStorageAsync();
        }

        // ================================================================ 小工具

        /// <summary>
        /// 危险操作的确认框。默认按钮**固定**是"取消"（Button2）：
        /// 用户顺手敲回车时必须落在"什么都不做"上，而不是落在删除上。
        /// </summary>
        private static MessageBoxResult ShowConfirm(string message, string caption, MessageBoxImage icon)
        {
            return MessageBox.Show(
                message,
                caption,
                MessageBoxButton.OKCancel,
                icon,
                MessageBoxResult.Cancel,
                MessageBoxOptions.None);
        }

        // ================================================================ AI 助手（可选联网）
        //
        // 隐私边界：发送的只有**已勾选规则本身**（Id/名称/说明/是否需要管理员）与数量统计，
        // 绝不含任何候选文件的路径。不可用时按钮保持可见，点下去只弹原因，绝不发起任何请求。

        /// <summary>有 AI 回复时结果卡片才出现。</summary>
        public bool HasAiReply
        {
            get => _hasAiReply;
            private set => SetProperty(ref _hasAiReply, value);
        }

        /// <summary>AI 回复正文（几百字，显示在只读结果卡片里，不用 MessageBox）。</summary>
        public string AiReplyText
        {
            get => _aiReplyText;
            private set => SetProperty(ref _aiReplyText, value);
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
        /// 用 AI 分析已勾选的清理规则。顺序不可调换：
        /// AI 可用性 → 有没有勾选规则 → 构造请求（只有规则与统计）→ 预览确认 → 显示回复。
        /// </summary>
        private void AnalyzeRulesWithAi()
        {
            // 第一件事：不可用就只说明原因，绝不发起任何请求
            if (!AiLauncher.IsAvailable)
            {
                ShowAiUnavailable();
                return;
            }

            var selectedRules = new List<CleanupRule>();
            foreach (var row in Rules)
            {
                if (row.IsEnabled) selectedRules.Add(row.Rule);
            }

            if (selectedRules.Count == 0)
            {
                MessageBox.Show(
                    "请至少勾选一条清理规则。",
                    AiTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // 数量统计取的是"扫描到的候选中"：还没扫描过时是 0 项 0 B，
            // 这时模型会据规则本身回答"这类东西能不能放心清理"，同样有意义。
            var candidateCount = Candidates.Count;
            long totalBytes = 0L;
            foreach (var row in Candidates) totalBytes += row.Candidate.Size;

            // PromptBuilder 只输出规则的 Id/名称/说明/是否需要管理员与两个统计值，路径不进请求
            var request = PromptBuilder.ForCleanupRules(selectedRules, candidateCount, totalBytes);
            ApplyAiReply(ShowAiPreview(request, "用 AI 分析清理规则"));
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
                Log.Error("AI 分析清理规则失败：" + ex.Message, ex);
                MessageBox.Show(
                    "AI 请求失败：" + ex.Message,
                    AiTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return null;
            }
        }

        /// <summary>null（用户取消或失败）时什么都不做；空回复也不显示空卡片。</summary>
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
            AiReplyText = string.Empty;
            HasAiReply = false;
        }

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "清理规则 {0} 条，候选 {1} 项", Rules.Count, Candidates.Count);
    }
}
