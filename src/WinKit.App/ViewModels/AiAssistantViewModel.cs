using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WinKit.App.Mvvm;
using WinKit.App.Services;
using WinKit.Core.Ai;
using WinKit.Core.Diagnostics;
using WinKit.Platform.Ai;

namespace WinKit.App.ViewModels
{
    /// <summary>
    /// 预览 → 确认 → 流式收流的全部状态机。
    ///
    /// **这个类是隐私承诺的用户可见部分**：用户点"发送"之前，网络上一个字节都不会出去。
    /// 发送的正文就是 <see cref="UserMessageText"/> 写回请求后的结果 ——
    /// 编辑框里的改动会真的被发出去，不做"只改界面不改请求"的假动作。
    ///
    /// 日志纪律：只记类别标签与字符数，**绝不记 API Key、也绝不记请求或回复内容**。
    /// </summary>
    public sealed class AiAssistantViewModel : ObservableObject
    {
        private readonly AiRequest _request;
        private readonly string _categoryLabel;

        private readonly string _title;
        private readonly string _disclosure;
        private readonly string _systemPromptText;

        private string _userMessageText = string.Empty;
        private string _replyText = string.Empty;
        private bool _isStreaming;
        private bool _hasRedactionNotice;
        private string _statusText = "尚未发送。请先核对下面的内容；在你点「发送」之前，不会有任何网络请求。";

        /// <summary>进行中的请求。只在 UI 线程读写。</summary>
        private CancellationTokenSource? _cts;

        /// <summary>窗口是否已关闭：关闭后不再产生任何 UI 更新（后台任务可能还在收尾）。</summary>
        private volatile bool _closed;

        public AiAssistantViewModel(AiRequest request, string title)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            _request = request;
            _title = string.IsNullOrWhiteSpace(title) ? "AI 助手 · 发送前预览" : title;
            _disclosure = request.Disclosure == null ? string.Empty : request.Disclosure;
            _systemPromptText = ExtractSystemText(request);
            _userMessageText = ExtractLastUserText(request);
            _categoryLabel = InferCategoryLabel(_userMessageText);

            SendCommand = new RelayCommand(OnSendOrStop);
            CopyReplyCommand = new RelayCommand(CopyReply);
        }

        /// <summary>窗口标题（由调用方给出的场景名）。</summary>
        public string Title => _title;

        /// <summary>本次发送什么数据的披露说明。**必须显示在最上方**，用户据此决定要不要点发送。</summary>
        public string Disclosure => _disclosure;

        public bool HasDisclosure => !string.IsNullOrWhiteSpace(_disclosure);

        /// <summary>随请求一起发送的系统指令：只读展示，让用户能展开核对。</summary>
        public string SystemPromptText => _systemPromptText;

        /// <summary>将要发送的正文，**可编辑** —— 这是"用户能改将要发送的内容"的落点。</summary>
        public string UserMessageText
        {
            get => _userMessageText;
            set
            {
                // 脱敏在 setter 里做，而不是在发送前偷偷改：
                // 编辑框里的内容**永远等于**将要发送的内容，用户看见什么就发出去什么。
                // （PromptBuilder 在构造请求时已经脱敏过一次；用户新粘进来的路径仍可能带用户名，
                //   所以这里补一道同源、幂等的脱敏。TextRedactor 是纯函数，可被单测覆盖。）
                var incoming = value ?? string.Empty;
                var sanitized = TextRedactor.Redact(incoming);

                // 注意：不能先 SetProperty(incoming) 再写回 sanitized —— 那会连发两次
                // PropertyChanged，WPF 会把 TextBox 的回车/光标状态重置掉。这里只发一次。
                if (string.Equals(_userMessageText, sanitized, StringComparison.Ordinal)) return;

                var redacted = !string.Equals(sanitized, incoming, StringComparison.Ordinal);
                _userMessageText = sanitized;

                OnPropertyChanged(nameof(UserMessageText));
                OnPropertyChanged(nameof(CharacterCountText));
                if (redacted) HasRedactionNotice = true;
            }
        }

        /// <summary>本次编辑触发了自动脱敏：界面上要明确告诉用户"有内容被替换了"。</summary>
        public bool HasRedactionNotice
        {
            get => _hasRedactionNotice;
            private set => SetProperty(ref _hasRedactionNotice, value);
        }

        /// <summary>模型回复，流式追加。</summary>
        public string ReplyText
        {
            get => _replyText;
            private set
            {
                if (!SetProperty(ref _replyText, value)) return;
                OnPropertyChanged(nameof(HasReply));
            }
        }

        public bool HasReply => !string.IsNullOrEmpty(_replyText);

        public bool IsStreaming
        {
            get => _isStreaming;
            private set
            {
                if (!SetProperty(ref _isStreaming, value)) return;
                OnPropertyChanged(nameof(SendButtonText));
                OnPropertyChanged(nameof(IsSendAvailable));
                // RelayCommand.CanExecute 的变化靠 CommandManager 重新求值，这里显式催一次，
                // 保证"发送↔停止"切换后按钮可用状态立刻跟上。
                RelayCommand.RaiseCanExecuteChanged();
            }
        }

        /// <summary>false 表示本机还没配置好：按钮置灰，点不到自然发不出请求。</summary>
        public bool IsSendAvailable => AppServices.IsAiReady;

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        public string SendButtonText => _isStreaming ? "停止" : "发送";

        public string CharacterCountText => "本次将发送 " + (UserMessageText == null ? 0 : UserMessageText.Length) + " 个字符";

        public RelayCommand SendCommand { get; }

        public RelayCommand CopyReplyCommand { get; }

        /// <summary>
        /// 窗口关闭前调用：取消进行中的请求，并标记窗口已关闭。
        /// 取消后后台收尾即使还在跑，也不会再往已经关掉的窗口封送更新。
        /// 这里只置标志、只取消令牌，不碰 UI，所以可以从窗口的关闭路径安全调用。
        /// </summary>
        public void CancelIfStreaming()
        {
            _closed = true;

            var cts = _cts;
            if (cts == null) return;

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已经结束，忽略
            }
            catch (Exception ex)
            {
                Log.Warn("取消 AI 请求失败：" + ex.Message);
            }
        }

        private void OnSendOrStop()
        {
            if (_isStreaming)
            {
                CancelIfStreaming();
                StatusText = "正在取消…";
                return;
            }

            BeginSend();
        }

        private void BeginSend()
        {
            // 第一道拦截：总开关 + 是否已保存 key（AppServices.IsAiReady 是唯一允许联网的判定入口）
            if (!AppServices.IsAiReady)
            {
                StatusText = AppServices.AiUnavailableReason;
                ShowUnavailable(AppServices.AiUnavailableReason);
                return;
            }

            // 第二道拦截：密钥文件在、但解不开（换了 Windows 用户 / 换了机器 / 文件损坏）
            if (AppServices.AiKeys == null)
            {
                StatusText = "本机密钥存储不可用，无法发送。";
                ShowUnavailable(StatusText);
                return;
            }

            var key = AppServices.AiKeys.Load();
            if (string.IsNullOrWhiteSpace(key))
            {
                StatusText = "无法读取已保存的 API Key（可能换了 Windows 用户或密钥已损坏）。请到「设置 → AI 助手」重新填写。";
                ShowUnavailable(StatusText);
                return;
            }

            // 第三道拦截：用户在预览窗口里没点发送就永远走不到这里；走到这里还要求内容非空
            var text = UserMessageText == null ? string.Empty : UserMessageText.Trim();
            if (text.Length == 0)
            {
                StatusText = "要发送的内容为空，请先输入问题。";
                MessageBox.Show(
                    "要发送的内容为空。请先输入问题，再点「发送」。",
                    "AI 助手",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            UserMessageText = text;

            // 写回请求时以属性为准（setter 里可能又做了一次脱敏），保证"界面=发送内容"这条等式
            var outgoing = UserMessageText;

            // 把编辑后的文本写回请求 —— 预览窗口里改的内容就是真正发出去的内容
            if (!TryApplyUserText(_request, outgoing))
            {
                StatusText = "请求里没有可替换的用户消息，已中止发送（不会联网）。";
                return;
            }

            OnPropertyChanged(nameof(CharacterCountText));

            _request.Stream = true;

            // 只记类别与字符数：内容里可能有客户名、项目名，绝不能进日志
            Log.Info("AI 请求开始：" + _categoryLabel + "，发送 " + _request.TotalCharacters + " 字符");

            // baseUrl 留空 = 用 DeepSeekClient 的内置默认值，界面层不重复写一遍默认地址
            var baseUrl = ResolveBaseUrl();

            var cts = new CancellationTokenSource();
            _cts = cts;

            IsStreaming = true;
            StatusText = "正在连接 DeepSeek…";

            var dispatcher = Application.Current == null ? null : Application.Current.Dispatcher;

            // 网络读写与 SSE 解析全在线程池上：UI 线程只负责追加文本，不会因为等 HTTP 而卡死
            // 调用前已校验 key（IsAiReady + Load 判空）；这两行只是让可空分析收口，
            // 而不是用 ! 去抑制分析器 —— 抑制掉的警告会掩盖将来真正的空引用。
            var effectiveKey = key;
            if (string.IsNullOrEmpty(effectiveKey)) return;
            // effectiveKey 在同方法上方已判空；用 ! 是因为**可空分析不跨越 lambda 边界**
            // （捕获的局部变量在 lambda 里不继承外层收窄），不是为了压掉一个真实风险。
            var safeKey = effectiveKey!;
            Task.Run(() => StreamAsync(safeKey, baseUrl, cts, dispatcher));
        }

        private async Task StreamAsync(string key, string? baseUrl, CancellationTokenSource cts, Dispatcher? dispatcher)
        {
            DeepSeekClient? client = null;
            AiException? failure = null;
            Exception? unexpected = null;
            var received = 0;

            try
            {
                client = new DeepSeekClient(key, baseUrl);

                Action<string> onDelta = delta =>
                {
                    if (_closed) return;
                    if (string.IsNullOrEmpty(delta)) return;
                    received += delta.Length;
                    AppendOnUiThread(dispatcher, delta);
                };

                await client.StreamAsync(_request, onDelta, cts.Token).ConfigureAwait(false);
            }
            catch (AiException ex)
            {
                failure = ex;
            }
            catch (OperationCanceledException)
            {
                failure = new AiException(AiErrorKind.Cancelled, "用户取消了请求");
            }
            catch (Exception ex)
            {
                unexpected = ex;
            }
            finally
            {
                if (client != null) client.Dispose();
            }

            if (_closed) return;

            // 收尾一律回到 UI 线程，避免跨线程改状态 / 触发绑定
            Action finish = () =>
            {
                _cts = null;
                IsStreaming = false;

                if (failure != null)
                {
                    if (failure.Kind == AiErrorKind.Cancelled)
                    {
                        StatusText = received > 0
                            ? "已取消（已收到 " + received + " 字符，内容不完整）"
                            : "已取消";
                    }
                    else
                    {
                        StatusText = failure.UserMessage;
                        AppendNotice("[中断] " + failure.UserMessage);
                        // 只记分类，不记请求/回复内容
                        Log.Warn("AI 请求失败：" + _categoryLabel + "，错误=" + failure.Kind);
                    }
                    return;
                }

                if (unexpected != null)
                {
                    StatusText = "请求中断：发生了未预期的错误，回复可能不完整。";
                    AppendNotice("[中断] 发生了未预期的错误，回复可能不完整。");
                    Log.Error("AI 请求异常：" + _categoryLabel, unexpected);
                    return;
                }

                StatusText = received > 0 ? "完成" : "完成（没有收到任何内容）。";
                Log.Info("AI 请求完成：" + _categoryLabel + "，收到 " + received + " 字符");
            };

            // BeginInvoke 返回 DispatcherOperation（可 await），丢弃它是有意的：这里就是"投递后不等"。
            if (dispatcher != null) _ = dispatcher.BeginInvoke(finish);
            else finish();
        }

        /// <summary>
        /// 把增量封送回 UI 线程后追加。用 BeginInvoke（异步投递）而不是 Invoke：
        /// 后台线程永远不等 UI 线程，界面即使卡顿也不会把网络读取拖死。
        /// </summary>
        private void AppendOnUiThread(Dispatcher? dispatcher, string delta)
        {
            if (dispatcher == null)
            {
                Append(delta);
                return;
            }

            dispatcher.BeginInvoke(new Action(() => Append(delta)));
        }

        private void Append(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            ReplyText = string.Concat(ReplyText, delta);
        }

        /// <summary>把失败/中断说明追加到回复区，让用户在回复区也能看到发生了什么。</summary>
        private void AppendNotice(string notice)
        {
            if (string.IsNullOrEmpty(notice)) return;
            var separator = ReplyText.Length == 0 ? string.Empty : Environment.NewLine + Environment.NewLine;
            ReplyText = ReplyText + separator + notice;
        }

        private static void ShowUnavailable(string reason)
        {
            MessageBox.Show(
                (string.IsNullOrEmpty(reason) ? "AI 助手当前不可用。" : reason)
                + Environment.NewLine + Environment.NewLine
                + "请到「设置 → AI 助手」启用并填写 DeepSeek API Key 后重试。"
                + Environment.NewLine
                + "未启用时 WinKit 不会发起任何网络请求。",
                "AI 助手未就绪",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void CopyReply()
        {
            if (string.IsNullOrEmpty(ReplyText)) return;

            try
            {
                Clipboard.SetText(ReplyText);
                StatusText = "已复制回复（" + ReplyText.Length + " 字符）";
            }
            catch (Exception ex)
            {
                // 剪贴板被别的进程占用是常见情况，不该让它把窗口搞崩
                StatusText = "复制失败：剪贴板正被其它程序占用。";
                Log.Warn("复制 AI 回复失败：" + ex.Message);
            }
        }

        /// <summary>把用户编辑后的文本写回请求里最后一条 user 消息。</summary>
        private static bool TryApplyUserText(AiRequest request, string text)
        {
            var messages = request.Messages;
            if (messages == null) return false;

            for (var i = messages.Count - 1; i >= 0; i--)
            {
                var message = messages[i];
                if (message != null && message.Role == AiRole.User)
                {
                    message.Content = text;
                    return true;
                }
            }

            return false;
        }

        private static string ExtractLastUserText(AiRequest request)
        {
            var messages = request.Messages;
            if (messages == null) return string.Empty;

            for (var i = messages.Count - 1; i >= 0; i--)
            {
                var message = messages[i];
                if (message != null && message.Role == AiRole.User)
                {
                    return message.Content ?? string.Empty;
                }
            }

            return string.Empty;
        }

        private static string ExtractSystemText(AiRequest request)
        {
            var messages = request.Messages;
            if (messages == null) return string.Empty;

            var parts = new List<string>();
            foreach (var message in messages)
            {
                if (message != null && message.Role == AiRole.System && !string.IsNullOrEmpty(message.Content))
                {
                    parts.Add(message.Content!);
                }
            }

            return string.Join(Environment.NewLine + Environment.NewLine, parts.ToArray());
        }

        /// <summary>日志里只允许出现这个粗略类别标签，绝不出现正文。</summary>
        private static string InferCategoryLabel(string userText)
        {
            if (string.IsNullOrEmpty(userText)) return "未知类别";
            if (userText.IndexOf("体检", StringComparison.Ordinal) >= 0) return "体检报告解读";
            if (userText.IndexOf("清理", StringComparison.Ordinal) >= 0) return "清理规则解释";
            if (userText.IndexOf("日志", StringComparison.Ordinal) >= 0) return "日志诊断";
            if (userText.IndexOf("进程", StringComparison.Ordinal) >= 0) return "进程解释";
            return "通用问答";
        }

        /// <summary>暂时没有 UI 可改的服务地址，返回 null 表示使用 DeepSeekClient 的默认地址。</summary>
        private static string? ResolveBaseUrl()
        {
            return null;
        }
    }
}
