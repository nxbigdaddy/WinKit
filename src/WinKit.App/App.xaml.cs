using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WinKit.App.Services;
using WinKit.App.ViewModels;
using WinKit.Core.Cleanup;
using WinKit.Core.Diagnostics;
using WinKit.Core.Rules;
using WinKit.Core.Serialization;
using WinKit.Core.Tasks;
using WinKit.Platform.Cleanup;
using WinKit.Platform.Security;
using ShellWindow = WinKit.App.Views.MainWindow;

namespace WinKit.App
{
    /// <summary>
    /// 应用入口。启动顺序刻意固定，不要随意调整：
    ///   1) 初始化数据目录/日志/配置
    ///   2) headless 提权子进程通道（有 --task 就直接干完活退出，不建窗口）
    ///   3) 单实例（已有实例就把它的窗口叫到前台，然后自己退出）
    ///   4) 应用主题
    ///   5) 创建主窗口
    ///
    /// 另一个铁律：任何单点异常都不允许把整个程序带走 —— 能记日志就记日志，能弹对话框就弹对话框。
    /// </summary>
    public partial class App : Application
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private const int PayloadPreviewLength = 200;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ---- 1) 数据目录 / 日志 / 配置 ----
            try
            {
                AppServices.Initialize(AppDomain.CurrentDomain.BaseDirectory);
            }
            catch (Exception ex)
            {
                Log.Error("初始化数据目录失败：" + ex.Message, ex);
                TryShowFatal("WinKit 初始化失败，程序即将退出。" + Environment.NewLine + Environment.NewLine
                    + ex.Message + Environment.NewLine + Environment.NewLine + "日志：" + DescribeLogFile());
                Shutdown(1);
                return;
            }

            // 全局异常兜底尽早挂上，之后的任何异常都不会让程序静默消失
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

            var args = e.Args;

            // ---- 2) headless 模式（提权子进程通道）----
            var taskId = GetArgument(args, "--task");
            if (!string.IsNullOrWhiteSpace(taskId))
            {
                RunHeadlessTask(taskId!, args);
                Shutdown(0);
                return;
            }

            // ---- 3) 单实例 ----
            if (!SingleInstance.TryAcquire())
            {
                Log.Info("检测到 WinKit 已在运行，尝试唤醒已有窗口");
                SingleInstance.NotifyExistingInstance();
                Shutdown(0);
                return;
            }
            SingleInstance.StartListener(ActivateMainWindow);

            // ---- 4) 主题（配置里的 System 会被解析成 Dark/Light）----
            ThemeManager.Current.Apply(AppServices.Config.Current.Theme);

            // ---- 5) 主窗口 ----
            var viewModel = new MainViewModel();
            var window = new ShellWindow { DataContext = viewModel };
            MainWindow = window;
            window.Show();
            Log.Info("主窗口已显示");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                PersistWindowState();
            }
            catch (Exception ex)
            {
                Log.Warn("保存窗口状态失败：" + ex.Message);
            }

            Log.Info("WinKit 退出");
            SingleInstance.Release();
            base.OnExit(e);
        }

        /// <summary>
        /// headless：以管理员身份被拉起的子进程在这里处理 --task。
        /// 注意提权进程的 stdout/stderr 不回传（ShellExecute+runas 不继承管道），
        /// 所以结果**只能**通过 Elevation.WriteResult 写结果文件。
        ///
        /// 这里是提权通道的入口，安全上只有一条铁律：**只接受结构化载荷 + 按任务白名单分发**。
        /// 任何"把 --payload 当成命令行来执行"的写法都会让这个通道变成自带的 UAC 绕过后门。
        /// </summary>
        private static void RunHeadlessTask(string taskId, string[]? args)
        {
            var payload = GetArgument(args, "--payload");
            var resultPath = GetArgument(args, "--result");
            var token = GetArgument(args, "--token");

            var payloadText = DecodePayload(payload);

            // 清理载荷里是完整的文件路径（可能含用户名等隐私），所以这一条任务只记长度、不记内容
            var preview = string.Equals(taskId, ElevatedCleanupPayload.TaskId, StringComparison.OrdinalIgnoreCase)
                ? "(路径清单，" + (payloadText == null ? "未提供" : payloadText.Length + " 字符") + "，内容不入日志)"
                : (payloadText == null
                    ? "(未提供)"
                    : (payloadText.Length > PayloadPreviewLength
                        ? payloadText.Substring(0, PayloadPreviewLength) + "…"
                        : payloadText));

            Log.Info("headless 模式启动：任务=" + taskId
                + "，payload=" + preview
                + (string.IsNullOrEmpty(token) ? "，缺少 --token" : string.Empty));

            if (string.IsNullOrWhiteSpace(resultPath))
            {
                Log.Error("headless 模式缺少 --result 参数，结果无法回传（stdout 不可用），此任务视为失败");
                return;
            }

            var resultWritten = false;
            try
            {
                if (string.Equals(taskId, ElevatedCleanupPayload.TaskId, StringComparison.OrdinalIgnoreCase))
                {
                    RunElevatedCleanup(payloadText, resultPath!, token ?? string.Empty, ref resultWritten);
                }
                else
                {
                    // 白名单之外的任务：保持与旧版本一致的行为，只回一句"未实现"
                    GuardedWriteResult(resultPath!, token ?? string.Empty, false, null,
                        "尚未实现该提权动作：" + taskId, ref resultWritten);
                }
            }
            catch (Exception ex)
            {
                // 兜底：任何异常都必须写回结果文件 —— 否则父进程只能等到超时，
                // 而"超时"看起来像卡死，完全看不出真正发生了什么。
                Log.Error("headless 任务执行异常：" + taskId + " → " + ex.Message, ex);
                GuardedWriteResult(resultPath!, token ?? string.Empty, false, null,
                    "提权任务执行异常：" + ex.Message, ref resultWritten);
            }
        }

        // ================================================================ 提权清理

        /// <summary>单个提权任务允许处理的项数上限（父进程按单规则 2 万收集，这里是一道兜底）。</summary>
        private const int MaxElevatedItems = 20000;

        /// <summary>
        /// 提权清理：把父进程筛好的、需要管理员的项搬进隔离区或回收站。
        ///
        /// 提权侧刻意**不做任何新的"该不该删"判断**（父进程已经预览并确认过），只做三件事：
        ///   1) 校验载荷来自结构化契约，且处置方式只能是隔离区/回收站；
        ///   2) 剔除非法路径（非绝对路径、位于 WinKit 数据目录之下、已经不存在）；
        ///   3) 复用与普通清理**完全相同**的 CleanupExecutor 与操作日志。
        ///
        /// 第 3 点很关键：父进程与提权子进程共用同一份数据目录与 journal，
        /// 所以子进程写下的记录会直接出现在「撤销中心」里 —— 父进程**绝不重复记日志**。
        /// </summary>
        private static void RunElevatedCleanup(string? payloadText, string resultPath, string token, ref bool resultWritten)
        {
            Log.Info("提权清理：收到任务 " + ElevatedCleanupPayload.TaskId);

            var payload = ParseCleanupPayload(payloadText);
            if (payload == null)
            {
                GuardedWriteResult(resultPath, token, false, null, "载荷无法解析", ref resultWritten);
                return;
            }

            // 处置方式白名单：Permanent 或任何无法识别的值一律拒绝。
            // "提权 + 不可撤销的删除"是最危险的组合，从设计上就不给它入口。
            if (!TryParseElevatedDisposal(payload.Disposal, out var disposal))
            {
                Log.Warn("提权清理：拒绝了非法处置方式「" + (payload.Disposal ?? "(空)") + "」");
                GuardedWriteResult(resultPath, token, false, null,
                    "提权清理不接受彻底删除，只允许隔离区或回收站", ref resultWritten);
                return;
            }

            if (payload.Items != null && payload.Items.Count > MaxElevatedItems)
            {
                Log.Warn("提权清理：待清理项过多（" + payload.Items.Count + " > " + MaxElevatedItems + "）");
                GuardedWriteResult(resultPath, token, false, null, "待清理项过多", ref resultWritten);
                return;
            }

            var candidates = FilterElevatedCandidates(payload.Items, out var skipped);
            var passed = payload.Items == null ? 0 : payload.Items.Count - skipped;
            Log.Info("提权清理：校验通过 " + passed + " 项，跳过 " + skipped + " 项，处置方式=" + disposal);

            if (candidates.Count == 0)
            {
                Log.Info("提权清理：没有通过校验的项，未触碰任何文件");
                GuardedWriteResult(resultPath, token, false, null,
                    "没有通过校验的待清理项（可能已被移动或删除、路径非法、或位于 WinKit 数据目录之下）",
                    ref resultWritten);
                return;
            }

            AppServices.Quarantine.EnsureCreated();

            var result = CleanupExecutor.Execute(
                candidates,
                disposal,
                AppServices.Quarantine,
                AppServices.Journal,
                new TaskContext(ElevatedCleanupPayload.TaskId, CancellationToken.None));

            Log.Info("提权清理执行结果：批次 " + result.BatchId
                + "，成功 " + result.SuccessCount + " 项，失败 " + result.FailedCount + " 项");
            if (skipped > 0) Log.Info("提权清理：另有 " + skipped + " 项在校验阶段被跳过");

            // 批次号必须回传：父进程要靠它告诉用户"可在撤销中心一键还原"
            var output = result.SummaryText + "；批次 " + result.BatchId;
            var error = result.FailedCount > 0
                ? "有 " + result.FailedCount + " 项未能清理（可能被程序占用、权限不足或已不存在），它们仍留在原处；"
                    + "已成功的 " + result.SuccessCount + " 项可在撤销中心还原。"
                : null;

            GuardedWriteResult(resultPath, token, result.FailedCount == 0, output, error, ref resultWritten);
        }

        /// <summary>解密 + 反序列化载荷。任何一步失败都返回 null，由调用方写回"载荷无法解析"。</summary>
        private static ElevatedCleanupPayload? ParseCleanupPayload(string? payloadText)
        {
            if (string.IsNullOrWhiteSpace(payloadText)) return null;

            try
            {
                // 反序列化本身是严格的：未知字段忽略，但类型不符会抛异常，正好用来拒绝畸形载荷
                return Json.Deserialize<ElevatedCleanupPayload>(payloadText!);
            }
            catch (Exception ex)
            {
                Log.Warn("提权清理：载荷反序列化失败 → " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 处置方式白名单。**只接受** Quarantine 与 RecycleBin；
        /// Permanent 与任何无法识别的字符串（含 null、空串、大小写变体之外的拼写）一律拒绝。
        /// 这里显式列出允许值而不是"排除 Permanent"，是为了让将来新增枚举值时默认落到拒绝一侧。
        /// </summary>
        private static bool TryParseElevatedDisposal(string? text, out CleanupDisposal disposal)
        {
            disposal = CleanupDisposal.Quarantine;
            if (string.IsNullOrWhiteSpace(text)) return false;

            if (string.Equals(text, nameof(CleanupDisposal.Quarantine), StringComparison.OrdinalIgnoreCase))
            {
                disposal = CleanupDisposal.Quarantine;
                return true;
            }
            if (string.Equals(text, nameof(CleanupDisposal.RecycleBin), StringComparison.OrdinalIgnoreCase))
            {
                disposal = CleanupDisposal.RecycleBin;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 逐项校验并转换成 CleanupCandidate。被剔除的项计入 skipped，不视为任务失败。
        ///
        /// 为什么要在这里再校验一遍（父进程不是已经筛过了吗）：
        /// 提权侧拿到的是**外部输入**（命令行里的 base64），必须当作不可信数据处理。
        /// 尤其"位于 WinKit 数据目录之下"这一条 —— 隔离区就在数据目录里，
        /// 一旦让它参与清理，就会出现"隔离区把自己搬进隔离区"的自毁。
        /// </summary>
        private static List<CleanupCandidate> FilterElevatedCandidates(
            IReadOnlyList<ElevatedCleanupItem>? items, out int skipped)
        {
            var candidates = new List<CleanupCandidate>();
            skipped = 0;
            if (items == null) return candidates;

            // 数据目录的规范化形式只算一次。
            // 这里必须容忍"取值过程中出任何岔子"：AppServices.Data 是 null! 声明的（初始化前为 null），
            // 数据目录是整个提权侧**唯一**不能碰的地方，所以宁可显式判空、也绝不赌它一定可用。
            var dataRoot = string.Empty;
            try
            {
                if (AppServices.IsInitialized && AppServices.Data != null)
                {
                    dataRoot = PathGuard.Normalize(AppServices.Data.Directory);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("提权清理：读取数据目录失败：" + ex.Message);
            }
            if (string.IsNullOrEmpty(dataRoot))
            {
                Log.Error("提权清理：数据目录不可用，无法保证不触碰 WinKit 自身数据，本次拒绝执行");
                return candidates;
            }

            foreach (var item in items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Path))
                {
                    skipped++;
                    continue;
                }

                // ① 必须是绝对路径：相对路径的含义取决于当前工作目录，
                //    而提权进程的工作目录不由父进程完全控制，接受它等于接受一个不可预测的删除目标
                if (!System.IO.Path.IsPathRooted(item.Path))
                {
                    Log.Warn("提权清理：跳过非绝对路径的项");
                    skipped++;
                    continue;
                }

                var normalized = PathGuard.Normalize(item.Path);

                // ② 绝不接受 WinKit 数据目录之下的路径（大小写不敏感，带分隔符边界）
                if (!string.IsNullOrEmpty(dataRoot) && PathGuard.IsUnder(normalized, dataRoot))
                {
                    Log.Warn("提权清理：跳过位于 WinKit 数据目录之下的项");
                    skipped++;
                    continue;
                }

                // ③ 已经不存在的项直接跳过（扫描到确认之间被程序自己删掉是常态）
                if (!System.IO.File.Exists(normalized) && !System.IO.Directory.Exists(normalized))
                {
                    skipped++;
                    continue;
                }

                // 载荷里只有 RuleId，没有规则显示名 —— 用它兜底，界面/日志里仍然能看出这条来自哪条规则
                var ruleId = string.IsNullOrEmpty(item.RuleId) ? "cleanup.elevated" : item.RuleId;

                candidates.Add(new CleanupCandidate(
                    ruleId: ruleId,
                    ruleName: ruleId,
                    path: normalized,
                    isDirectory: item.IsDirectory,
                    size: item.Size,
                    lastWriteTimeUtc: DateTime.UtcNow));   // 提权侧不做年龄过滤：父进程已经过滤过了
            }

            return candidates;
        }

        /// <summary>
        /// 写结果文件的唯一出口。每个结果文件只允许写一次：
        /// 否则内层的具体失败原因会被外层 catch 的"执行异常"覆盖，用户就看不到真正的原因了。
        /// </summary>
        private static void GuardedWriteResult(
            string resultPath, string token, bool success, string? output, string? error, ref bool resultWritten)
        {
            if (resultWritten) return;

            try
            {
                Elevation.WriteResult(resultPath, token, success, output, error);
                resultWritten = true;
                Log.Info("headless 模式已写入结果文件：" + resultPath);
            }
            catch (Exception ex)
            {
                Log.Error("headless 模式写入结果文件失败：" + ex.Message, ex);
            }
        }

        /// <summary>解析 --name value 或 --name=value；缺失、缺值都返回 null（不抛异常）。</summary>
        private static string? GetArgument(string[]? args, string name)
        {
            if (args == null) return null;

            for (var i = 0; i < args.Length; i++)
            {
                var current = args[i];
                if (string.IsNullOrEmpty(current)) continue;

                if (string.Equals(current, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && !string.IsNullOrEmpty(args[i + 1])) return args[i + 1];
                    return null;
                }

                if (current!.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                {
                    return current.Substring(name.Length + 1);
                }
            }
            return null;
        }

        private static string? DecodePayload(string? base64)
        {
            if (string.IsNullOrWhiteSpace(base64)) return null;

            try
            {
                return Utf8NoBom.GetString(Convert.FromBase64String(base64!));
            }
            catch (FormatException)
            {
                Log.Warn("--payload 不是合法的 base64，已忽略");
                return null;
            }
        }

        /// <summary>第二个实例把已有窗口叫到前台。本方法由 SingleInstance 切回 UI 线程后调用。</summary>
        private static void ActivateMainWindow()
        {
            var application = Application.Current;
            if (application == null) return;

            var window = application.MainWindow;
            if (window == null) return;

            try
            {
                if (!window.IsVisible) window.Show();
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
                window.Topmost = true;
                window.Topmost = false;
                window.Focus();
                Log.Info("已把主窗口切换到前台");
            }
            catch (Exception ex)
            {
                Log.Warn("激活主窗口失败：" + ex.Message);
            }
        }

        /// <summary>把窗口尺寸/最大化状态写回配置。最大化时用 RestoreBounds 记住"还原尺寸"。</summary>
        private static void PersistWindowState()
        {
            var application = Application.Current;
            if (application == null) return;

            var window = application.MainWindow;
            if (window == null || !AppServices.IsInitialized) return;

            var config = AppServices.Config;
            if (config == null) return;

            var state = window.WindowState;
            var width = window.Width;
            var height = window.Height;

            if (state == WindowState.Normal && window.ActualWidth > 0 && window.ActualHeight > 0)
            {
                width = window.ActualWidth;
                height = window.ActualHeight;
            }
            else
            {
                var bounds = window.RestoreBounds;
                if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
                {
                    width = bounds.Width;
                    height = bounds.Height;
                }
            }

            if (!double.IsNaN(width) && !double.IsInfinity(width) && width > 0) config.Current.WindowWidth = width;
            if (!double.IsNaN(height) && !double.IsInfinity(height) && height > 0) config.Current.WindowHeight = height;
            config.Current.WindowMaximized = state == WindowState.Maximized;
            config.Save();

            // 外壳 VM 订阅了 ThemeManager 的静态事件，退出时解绑
            if (window.DataContext is IDisposable disposable) disposable.Dispose();
        }

        /// <summary>界面线程异常：记日志 + 弹窗告知日志位置，然后吞掉，绝不让程序退出。</summary>
        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            var detail = e.Exception == null ? "未知错误" : e.Exception.Message;
            Log.Error("界面线程未处理异常：" + detail, e.Exception);

            try
            {
                MessageBox.Show(
                    "WinKit 遇到了一个未处理的错误，已经记录到日志文件，程序会继续运行。"
                    + Environment.NewLine + Environment.NewLine
                    + detail
                    + Environment.NewLine + Environment.NewLine
                    + "日志文件：" + Environment.NewLine + DescribeLogFile(),
                    "WinKit · 发生错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (Exception)
            {
                // 连对话框都弹不出来时不能反过来把程序带崩
            }

            e.Handled = true;
        }

        /// <summary>兜底：非 UI 线程的未处理异常（此时进程通常已经保不住了，只能留证据）。</summary>
        private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            Log.Error("应用域未处理异常（进程即将结束）：" + (exception == null ? "未知错误" : exception.Message), exception);
        }

        private static string DescribeLogFile()
        {
            var current = Log.CurrentFile;
            return string.IsNullOrEmpty(current) ? "(尚未生成日志文件)" : current!;
        }

        private static void TryShowFatal(string message)
        {
            try
            {
                MessageBox.Show(message, "WinKit · 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception)
            {
                // 非交互场景下弹不出对话框，只能靠日志
            }
        }
    }
}
