using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using WinKit.App.Ai;
using WinKit.App.Mvvm;
using WinKit.App.Services;
using WinKit.Core.Ai;
using WinKit.Core.Diagnostics;
using WinKit.Core.Journal;
using WinKit.Core.Text;

namespace WinKit.App.ViewModels
{
    /// <summary>批次列表的一行（只读包装，构造后不再变化）。</summary>
    public sealed class BatchRow
    {
        public BatchRow(string batchId, string timeText, string taskText, int count, int undoableCount, string summaryText)
        {
            BatchId = batchId;
            TimeText = timeText;
            TaskText = taskText;
            Count = count;
            UndoableCount = undoableCount;
            SummaryText = summaryText;
        }

        public string BatchId { get; }

        public string TimeText { get; }

        public string TaskText { get; }

        public int Count { get; }

        public int UndoableCount { get; }

        public string SummaryText { get; }

        public bool CanUndo => UndoableCount > 0;

        /// <summary>列表里显示的"N 项（M 可撤销）"。</summary>
        public string CountText => Count + " 项（" + UndoableCount + " 可撤销）";

        public override string ToString() => TimeText + " " + SummaryText;
    }

    /// <summary>批次明细的一行（只读包装）。</summary>
    public sealed class EntryRow
    {
        public EntryRow(JournalEntry entry, bool undoable)
        {
            var e = entry ?? throw new ArgumentNullException(nameof(entry));
            TimeText = e.Timestamp;
            Action = e.Action;
            Target = e.Target;
            SizeText = e.Size > 0 ? ByteSize.Format(e.Size) : "—";
            Result = e.Result;
            Level = e.Level;
            Undoable = undoable;
        }

        public string TimeText { get; }

        public string Action { get; }

        public string Target { get; }

        public string SizeText { get; }

        public string Result { get; }

        public string Level { get; }

        public bool Undoable { get; }

        public string UndoText => Undoable ? "可撤销" : "不可撤销";
    }

    /// <summary>
    /// 撤销中心。数据来源是操作日志（journal.jsonl）：
    /// 按 BatchId 聚合成批次列表，选中批次后再展开明细；撤销以"批次"为单位整体倒序回滚。
    ///
    /// 线程约定：读日志可能很大，一律走 TaskRunner.RunAsync 到线程池，
    /// await 之后继续在 UI 线程更新 ObservableCollection（不做跨线程集合修改）。
    /// </summary>
    public sealed class JournalViewModel : ObservableObject
    {
        // ---- AI 助手（可选联网）----

        private const string AiTitle = "用 AI 诊断";

        /// <summary>可用时按钮旁边那行小字：必须如实说明"会联网 + 发送前先给你看"。</summary>
        private const string AiAvailableHint = "会联网：将先显示要发送的内容，确认后才联网。";

        private const string AiEnableHint = "可在「设置 → AI 助手」启用并填写 API Key。";

        private readonly string _journalPath;
        private bool _refreshing;
        private BatchRow? _selectedBatch;
        private string _statusText = "正在读取操作日志…";
        private string _selectedBatchText = "未选择批次";
        private int _undoableTotal;

        private string _aiReplyText = string.Empty;
        private bool _hasAiReply;

        public JournalViewModel()
        {
            _journalPath = AppServices.Journal == null ? "—" : AppServices.Journal.FilePath;

            RefreshCommand = new AsyncRelayCommand(RefreshAsync);
            UndoBatchCommand = new AsyncRelayCommand(UndoBatchAsync, CanUndoSelectedBatch);
            OpenJournalFolderCommand = new RelayCommand(OpenJournalFolderEx);

            // AI 入口：CanExecute 里**故意不判断 AiLauncher.IsAvailable** ——
            // 不可用时按钮保持可见、可点，点下去只弹"为什么不能用"。
            DiagnoseWithAiCommand = new RelayCommand(DiagnoseWithAi);
            CopyAiReplyCommand = new RelayCommand(CopyAiReply);
            ClearAiReplyCommand = new RelayCommand(ClearAiReply);

            // 构造时就预热一次：日志大时，等 Loaded 再读会有明显空窗。
            _ = RefreshAsync();
        }

        public ObservableCollection<BatchRow> Batches { get; } = new ObservableCollection<BatchRow>();

        public ObservableCollection<EntryRow> Entries { get; } = new ObservableCollection<EntryRow>();

        public BatchRow? SelectedBatch
        {
            get => _selectedBatch;
            set
            {
                if (!SetProperty(ref _selectedBatch, value)) return;
                OnSelectedBatchChanged();
            }
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        /// <summary>当前选中批次的一句话描述，未选中时提示先选批次。</summary>
        public string SelectedBatchText
        {
            get => _selectedBatchText;
            private set => SetProperty(ref _selectedBatchText, value);
        }

        public string JournalPathText => _journalPath;

        public int UndoableTotal
        {
            get => _undoableTotal;
            private set
            {
                if (!SetProperty(ref _undoableTotal, value)) return;
                OnPropertyChanged(nameof(UndoableTotalText));
            }
        }

        public string UndoableTotalText => UndoableTotal + " 项可撤销";

        public AsyncRelayCommand RefreshCommand { get; }

        public AsyncRelayCommand UndoBatchCommand { get; }

        public RelayCommand OpenJournalFolderCommand { get; }

        public RelayCommand DiagnoseWithAiCommand { get; }

        public RelayCommand CopyAiReplyCommand { get; }

        public RelayCommand ClearAiReplyCommand { get; }

        /// <summary>
        /// 重新读日志。文件可能很大，整段读取 + 聚合都在线程池里做，
        /// 只有结果落回 UI 线程时才动集合。
        /// </summary>
        private async Task RefreshAsync()
        {
            if (_refreshing) return;
            _refreshing = true;
            RefreshCommand.RaiseCanExecuteChanged();
            StatusText = "正在读取操作日志…";

            try
            {
                var store = AppServices.Journal;
                if (store == null)
                {
                    ClearAll();
                    StatusText = "操作日志不可用";
                    return;
                }

                var keepId = _selectedBatch?.BatchId;
                var snapshot = await TaskRunner.RunAsync(() =>
                {
                    var batchIds = store.ListBatches();
                    var all = store.ReadAll();

                    var byBatch = new Dictionary<string, List<JournalEntry>>(StringComparer.Ordinal);
                    foreach (var entry in all)
                    {
                        if (string.IsNullOrEmpty(entry.BatchId)) continue;
                        List<JournalEntry>? list;
                        if (!byBatch.TryGetValue(entry.BatchId, out list))
                        {
                            list = new List<JournalEntry>();
                            byBatch.Add(entry.BatchId, list);
                        }
                        list.Add(entry);
                    }

                    var rows = new List<BatchRow>(batchIds.Count);
                    var total = 0;
                    foreach (var batchId in batchIds)
                    {
                        List<JournalEntry>? items;
                        if (!byBatch.TryGetValue(batchId, out items)) continue;

                        var undoable = AppServices.Undo.CountUndoable(items);
                        total += undoable;
                        rows.Add(new BatchRow(
                            batchId,
                            items[items.Count - 1].Timestamp,
                            items[0].TaskId,
                            items.Count,
                            undoable,
                            items[items.Count - 1].Summary));
                    }

                    return new Snapshot(rows, total, store.CorruptedLineCount);
                }).ConfigureAwait(true);

                Batches.Clear();
                foreach (var row in snapshot.Rows) Batches.Add(row);
                UndoableTotal = snapshot.Total;

                RestoreSelection(keepId);
                UpdateStatus(snapshot.Corrupted);
            }
            catch (Exception ex)
            {
                StatusText = "读取操作日志失败：" + ex.Message;
                Log.Error("撤销中心读取日志失败：" + ex.Message, ex);
            }
            finally
            {
                _refreshing = false;
                RefreshCommand.RaiseCanExecuteChanged();
                RelayCommand.RaiseCanExecuteChanged();
            }
        }

        private void RestoreSelection(string? keepId)
        {
            BatchRow? match = null;
            if (!string.IsNullOrEmpty(keepId))
            {
                foreach (var row in Batches)
                {
                    if (string.Equals(row.BatchId, keepId, StringComparison.Ordinal))
                    {
                        match = row;
                        break;
                    }
                }
            }

            SelectedBatch = match ?? (Batches.Count > 0 ? Batches[0] : null);
            if (SelectedBatch == null)
            {
                Entries.Clear();
                SelectedBatchText = "未选择批次";
                UpdateStatus(0);
            }
        }

        private void OnSelectedBatchChanged()
        {
            LoadEntries();

            var batch = SelectedBatch;
            SelectedBatchText = batch == null
                ? "未选择批次"
                : batch.TimeText + " · " + batch.TaskText + " · " + batch.CountText;

            UpdateStatus(0);
            RelayCommand.RaiseCanExecuteChanged();
        }

        private void LoadEntries()
        {
            Entries.Clear();
            var batch = SelectedBatch;
            if (batch == null) return;

            IReadOnlyList<JournalEntry> items;
            try
            {
                items = AppServices.Journal.ReadBatch(batch.BatchId);
            }
            catch (Exception ex)
            {
                StatusText = "读取批次明细失败：" + ex.Message;
                Log.Warn("读取批次明细失败：" + batch.BatchId + " → " + ex.Message);
                return;
            }

            foreach (var entry in items)
            {
                Entries.Add(new EntryRow(entry, AppServices.Undo.CanUndo(entry)));
            }
        }

        private void UpdateStatus(int corrupted)
        {
            var parts = new List<string>(3)
            {
                "共 " + Batches.Count + " 个批次",
                "可撤销 " + UndoableTotal + " 项",
            };
            var batch = SelectedBatch;
            if (batch != null) parts.Add("当前批次 " + batch.Count + " 项（" + batch.UndoableCount + " 可撤销）");
            if (corrupted > 0) parts.Add("已跳过 " + corrupted + " 行损坏记录");
            if (AppServices.IsReadOnly) parts.Add("只读审计模式：撤销不可用");

            StatusText = string.Join(" · ", parts);
        }

        private void ClearAll()
        {
            Batches.Clear();
            Entries.Clear();
            SelectedBatch = null;
            UndoableTotal = 0;
        }

        private bool CanUndoSelectedBatch()
        {
            var batch = SelectedBatch;
            return batch != null && batch.CanUndo && !AppServices.IsReadOnly;
        }

        /// <summary>
        /// 按批次撤销：只读模式拦截 → 确认弹窗 → 调 UndoEngine.UndoBatch → 结果反馈 → 刷新列表。
        /// 撤销本身要搬文件，放到线程池执行，避免 UI 卡住。
        /// </summary>
        private async Task UndoBatchAsync()
        {
            if (AppServices.IsReadOnly)
            {
                MessageBox.Show(
                    "只读审计模式已开启，无法撤销。\n\n请先到「设置 → 安全」关闭只读审计模式。",
                    "撤销中心",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var batch = SelectedBatch;
            if (batch == null)
            {
                MessageBox.Show("请先在左侧选择一个批次。", "撤销中心", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirmText = "确认撤销该批次？\n\n"
                + "批次：" + batch.BatchId + "\n"
                + "时间：" + batch.TimeText + "\n"
                + "任务：" + batch.TaskText + "\n"
                + "条目数：" + batch.Count + " 项（其中 " + batch.UndoableCount + " 项可撤销）\n\n"
                + "撤销会把文件搬回原位置；不可撤销的条目会被跳过。该操作同样记入操作日志。";

            var answer = MessageBox.Show(
                confirmText,
                "撤销确认",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;

            UndoResult result;
            try
            {
                var batchId = batch.BatchId;
                result = await TaskRunner.RunAsync(() => AppServices.Undo.UndoBatch(batchId)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Error("撤销批次失败：" + batch.BatchId + " → " + ex.Message, ex);
                MessageBox.Show("撤销失败：" + ex.Message, "撤销中心", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (result.Success)
            {
                Log.Info("已撤销批次 " + batch.BatchId + "：" + result.Message);
                MessageBox.Show(result.Message, "撤销完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                Log.Warn("撤销批次 " + batch.BatchId + " 未完全成功：" + result.Message);
                MessageBox.Show(result.Message, "撤销未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // 撤销后日志里被撤销条目的状态可能已变，整表刷新最省心
            await RefreshAsync().ConfigureAwait(true);
        }

        private void OpenJournalFolderEx()
        {
            try
            {
                if (string.IsNullOrEmpty(_journalPath) || _journalPath == "—") return;
                Process.Start("explorer.exe", "/select,\"" + _journalPath + "\"");
            }
            catch (Exception ex)
            {
                Log.Warn("打开操作日志位置失败：" + ex.Message);
                MessageBox.Show("无法打开文件位置：" + ex.Message, "撤销中心", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ================================================================ AI 助手（可选联网）
        //
        // 隐私边界（本页最重的一条）：发送的**只有经过 TextRedactor.ExtractLogExcerpt 脱敏的片段**。
        // 原始日志一律不外出 —— 里面天然带着用户名、计算机名与目录结构。
        // 不可用时按钮保持可见，点下去只弹原因，绝不发起任何请求。

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
        /// 让 AI 诊断操作日志。顺序不可调换：
        /// AI 可用性 → 日志文件是否存在/可读 → **脱敏** → 构造请求 → 预览确认 → 显示回复。
        ///
        /// 日志显式用 UTF8 读：写入端（JournalStore）也是 UTF8，两边口径必须一致，
        /// 否则中文在 GBK 默认代码页下会变成乱码。
        /// </summary>
        private void DiagnoseWithAi()
        {
            // 第一件事：不可用就只说明原因，绝不发起任何请求
            if (!AiLauncher.IsAvailable)
            {
                ShowAiUnavailable();
                return;
            }

            var store = AppServices.Journal;
            var path = store == null ? null : store.FilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                MessageBox.Show(
                    "找不到操作日志文件（可能还没有任何操作记录），暂时无法让 AI 诊断。",
                    AiTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            string raw;
            try
            {
                raw = File.ReadAllText(path!, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                // 日志被占用/被删/权限不足都走这里：只提示，不抛异常
                Log.Warn("读取操作日志失败（AI 诊断）：" + ex.Message, ex);
                MessageBox.Show(
                    "读取操作日志失败，暂时无法让 AI 诊断。\r\n\r\n原因：" + ex.Message,
                    AiTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // 必需的脱敏步骤：ExtractLogExcerpt 内部会调用 TextRedactor.Redact，
            // 并且只挑出错行及其后 3 行（最多 40 行），避免整份日志外发。
            var excerpt = TextRedactor.ExtractLogExcerpt(raw);
            if (string.IsNullOrWhiteSpace(excerpt))
            {
                MessageBox.Show(
                    "操作日志里没有可用于诊断的内容，暂时无法让 AI 诊断。",
                    AiTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // 契约：进入 ForLogDiagnosis 的文本必须已经过脱敏
            var request = PromptBuilder.ForLogDiagnosis(excerpt);
            ApplyAiReply(ShowAiPreview(request, "用 AI 诊断操作日志"));
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
                Log.Error("AI 诊断操作日志失败：" + ex.Message, ex);
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

        private sealed class Snapshot
        {
            public Snapshot(List<BatchRow> rows, int total, int corrupted)
            {
                Rows = rows;
                Total = total;
                Corrupted = corrupted;
            }

            public List<BatchRow> Rows { get; }

            public int Total { get; }

            public int Corrupted { get; }
        }
    }
}
