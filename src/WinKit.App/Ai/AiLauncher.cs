using System;
using System.Windows;
using WinKit.App.Services;
using WinKit.App.ViewModels;
using WinKit.App.Views.Ai;
using WinKit.Core.Ai;
using WinKit.Core.Diagnostics;
using WinKit.Platform.Ai;

namespace WinKit.App.Ai
{
    /// <summary>
    /// 全项目**唯一**的 AI 调用入口。
    ///
    /// 它存在的意义不是"少写几行代码"，而是把隐私承诺收窄成一个必须经过的漏斗：
    /// 任何人想让 AI 解释点什么，都必须走 <see cref="ShowPreview"/>，
    /// 于是"发送前给用户看将要发送的确切文本、并且用户可以改"就不可能被某个调用点漏掉。
    /// 直接 new DeepSeekClient 去发请求的代码在这里不存在，也不该被写出来。
    ///
    /// 三道拦截按顺序生效（任何一道不过都不联网）：
    ///   1. <see cref="AppServices.IsAiReady"/> 为假（未启用 / 无 key）→ 不弹窗、不建连；
    ///   2. <see cref="ApiKeyStore.Load"/> 解不开密钥 → 在预览窗口里提示重新填写，仍不联网；
    ///   3. 用户在预览窗口里不点"发送" → 永远停在本地。
    /// </summary>
    public static class AiLauncher
    {
        private const string DefaultTitle = "AI 助手 · 发送前预览";

        /// <summary>= <see cref="AppServices.IsAiReady"/>。界面上的 AI 按钮用它决定是否可用。</summary>
        public static bool IsAvailable => AppServices.IsAiReady;

        /// <summary>= <see cref="AppServices.AiUnavailableReason"/>，可直接显示的中文原因。</summary>
        public static string UnavailableReason => AppServices.AiUnavailableReason;

        /// <summary>
        /// 弹出预览窗口（模态）。用户确认并完成后返回完整回复；取消、失败或未就绪返回 null。
        /// **在用户按下"发送"之前不会有任何网络活动。**
        /// </summary>
        public static string? ShowPreview(Window? owner, AiRequest request, string title)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            if (!IsAvailable)
            {
                // 兜底：调用点本该先问 IsAvailable，这里再挡一次，保证"没启用就一定不联网"是结构性事实。
                Log.Info("AI 预览被拒绝：AI 未就绪（" + UnavailableReason + "）");
                return null;
            }

            var viewModel = new AiAssistantViewModel(request, string.IsNullOrWhiteSpace(title) ? DefaultTitle : title);
            var window = new AiPreviewWindow(viewModel);

            if (owner != null)
            {
                // Owner 必须在 ShowDialog 之前赋值，否则 CenterOwner 会退化成屏幕居中
                window.Owner = owner;
            }

            var confirmed = window.ShowDialog();
            if (confirmed != true) return null;

            var reply = viewModel.ReplyText;
            return string.IsNullOrWhiteSpace(reply) ? null : reply;
        }
    }
}
