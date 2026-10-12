using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace WinKit.Core.Ai
{
    public enum AiRole
    {
        System = 0,
        User = 1,
        Assistant = 2,
    }

    [DataContract]
    public sealed class AiMessage
    {
        public AiMessage()
        {
        }

        public AiMessage(AiRole role, string content)
        {
            Role = role;
            Content = content ?? string.Empty;
        }

        [DataMember(Name = "role", Order = 0)]
        public string RoleName
        {
            get
            {
                switch (Role)
                {
                    case AiRole.System: return "system";
                    case AiRole.Assistant: return "assistant";
                    default: return "user";
                }
            }
            set
            {
                if (string.Equals(value, "system", StringComparison.OrdinalIgnoreCase)) Role = AiRole.System;
                else if (string.Equals(value, "assistant", StringComparison.OrdinalIgnoreCase)) Role = AiRole.Assistant;
                else Role = AiRole.User;
            }
        }

        [IgnoreDataMember]
        public AiRole Role { get; set; }

        [DataMember(Name = "content", Order = 1)]
        public string Content { get; set; } = string.Empty;
    }

    /// <summary>
    /// 一次 AI 请求。
    ///
    /// **这个对象就是"将要发送到外部的内容"的完整定义** —— 界面的预览窗口直接渲染它，
    /// 用户确认后原样发出。因此"到底发送了什么"可以被单元测试断言，
    /// 而不是靠阅读代码去推断。
    /// </summary>
    [DataContract]
    public sealed class AiRequest
    {
        [DataMember(Name = "model", Order = 0)]
        public string Model { get; set; } = "deepseek-chat";

        [DataMember(Name = "messages", Order = 1)]
        public List<AiMessage> Messages { get; set; } = new List<AiMessage>();

        [DataMember(Name = "temperature", Order = 2)]
        public double Temperature { get; set; } = 0.3;

        [DataMember(Name = "max_tokens", Order = 3)]
        public int MaxTokens { get; set; } = 1200;

        [DataMember(Name = "stream", Order = 4)]
        public bool Stream { get; set; } = true;

        /// <summary>
        /// 面向用户的"这次会发送什么"摘要，用于预览窗口标题下方的说明行。
        /// 由构造请求的一方填写，必须如实描述数据范围。
        /// </summary>
        [IgnoreDataMember]
        public string Disclosure { get; set; } = string.Empty;

        /// <summary>把消息拼成一段纯文本，供预览窗口展示与日志（**日志只记摘要不记内容**）。</summary>
        public string ToPreviewText()
        {
            var parts = new List<string>();
            foreach (var message in Messages)
            {
                var label = message.Role == AiRole.System ? "【系统指令】"
                    : message.Role == AiRole.Assistant ? "【模型回复】" : "【你的问题】";
                parts.Add(label + Environment.NewLine + message.Content);
            }
            return string.Join(Environment.NewLine + Environment.NewLine, parts.ToArray());
        }

        /// <summary>字符数，用于"本次将发送约 N 个字符"这类如实提示。</summary>
        public int TotalCharacters
        {
            get
            {
                var n = 0;
                foreach (var message in Messages) n += message.Content == null ? 0 : message.Content.Length;
                return n;
            }
        }
    }

    /// <summary>AI 调用的失败分类 —— 界面据此给不同的中文提示与后续动作。</summary>
    public enum AiErrorKind
    {
        None = 0,

        /// <summary>没配置 key。</summary>
        NotConfigured = 1,

        /// <summary>key 无效或已失效（HTTP 401/403）。</summary>
        Unauthorized = 2,

        /// <summary>被限流或余额不足（HTTP 429/402）。</summary>
        RateLimited = 3,

        /// <summary>网络不可达 / DNS 失败 / 超时。</summary>
        NetworkUnavailable = 4,

        /// <summary>服务端返回了非预期内容（解析失败、5xx）。</summary>
        ServerError = 5,

        /// <summary>用户主动取消。</summary>
        Cancelled = 6,
    }

    public sealed class AiException : Exception
    {
        public AiException(AiErrorKind kind, string message, Exception? inner = null)
            : base(message, inner)
        {
            Kind = kind;
        }

        public AiErrorKind Kind { get; }

        /// <summary>面向用户的中文说明与建议动作。</summary>
        public string UserMessage
        {
            get
            {
                switch (Kind)
                {
                    case AiErrorKind.NotConfigured:
                        return "尚未配置 DeepSeek API Key。请到「设置 → AI 助手」填入后重试。";
                    case AiErrorKind.Unauthorized:
                        return "API Key 无效或已被吊销。请到「设置 → AI 助手」重新填写。";
                    case AiErrorKind.RateLimited:
                        return "请求过于频繁或账户余额不足，请稍后再试。";
                    case AiErrorKind.NetworkUnavailable:
                        return "无法连接到 DeepSeek 服务。请检查网络连接或代理设置；本工具在未启用 AI 时不会联网。";
                    case AiErrorKind.Cancelled:
                        return "已取消本次请求。";
                    default:
                        return "AI 服务返回了异常结果，请稍后重试。";
                }
            }
        }
    }
}
