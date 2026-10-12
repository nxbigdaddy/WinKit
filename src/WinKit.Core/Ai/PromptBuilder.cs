using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using WinKit.Core.Cleanup;
using WinKit.Core.Diagnostics;

namespace WinKit.Core.Ai
{
    /// <summary>
    /// 发送前脱敏。**纯函数、可单测** —— 这是隐私边界的守门人，不能靠人工核对。
    ///
    /// 为什么必须做：日志与路径里天然带着用户名、计算机名、客户/项目目录名。
    /// 直接丢给外部模型就等于把用户的目录结构交出去了。
    /// </summary>
    public static class TextRedactor
    {
        public const string UserPlaceholder = "<用户>";
        public const string MachinePlaceholder = "<计算机>";

        private static readonly Regex UserProfilePath = new Regex(
            @"([A-Za-z]:\\Users\\)[^\\\r\n\t]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex DocumentsAndSettingsPath = new Regex(
            @"([A-Za-z]:\\Documents and Settings\\)[^\\\r\n\t]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>看起来像密钥的串（sk- 开头、或 32 位以上连续十六进制）一律抹掉。</summary>
        private static readonly Regex SecretLike = new Regex(
            @"\bsk-[A-Za-z0-9_\-]{8,}|\b[0-9a-fA-F]{32,}\b", RegexOptions.Compiled);

        /// <summary>
        /// 对将要发送给外部模型的文本做脱敏。
        /// 处理内容：用户目录名、计算机名、当前用户名、疑似密钥的串。
        /// **不做**的事：不删路径结构（诊断报错需要它）、不改写文件名（那是用户要问的对象）。
        /// </summary>
        public static string Redact(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var result = text!;

            result = UserProfilePath.Replace(result, "$1" + UserPlaceholder);
            result = DocumentsAndSettingsPath.Replace(result, "$1" + UserPlaceholder);

            // 用户名/计算机名可能在路径之外出现（例如 "用户 Administrator 正在运行"）
            var userName = SafeUserName();
            if (userName.Length >= 3)
            {
                result = Regex.Replace(result, Regex.Escape(userName), UserPlaceholder, RegexOptions.IgnoreCase);
            }

            var machineName = SafeMachineName();
            if (machineName.Length >= 3)
            {
                result = Regex.Replace(result, Regex.Escape(machineName), MachinePlaceholder, RegexOptions.IgnoreCase);
            }

            result = SecretLike.Replace(result, "<已抹除的疑似密钥>");

            return result;
        }

        /// <summary>把完整路径压成"…\父目录\文件名"，用于只想知道"这是个什么进程/什么文件"的场景。</summary>
        public static string ShortenPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;

            var text = path!.Trim();
            var parts = text.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 2) return Redact(text);

            var name = parts[parts.Length - 1];
            var parent = parts[parts.Length - 2];
            return "…\\" + Redact(parent) + "\\" + name;
        }

        /// <summary>只保留文件名（进程解释场景连父目录都不需要）。</summary>
        public static string FileNameOnly(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;

            var text = path!.Trim();
            var index = text.LastIndexOfAny(new[] { '\\', '/' });
            return index >= 0 && index + 1 < text.Length ? text.Substring(index + 1) : text;
        }

        /// <summary>
        /// 把所有"盘符开头的路径"或 UNC 路径整段换成占位符。
        ///
        /// 与 <see cref="Redact"/> 的分工很关键：
        /// - <c>Redact</c> **保留路径结构**（只抹掉用户名/机器名）—— 日志诊断需要看到路径才能定位问题；
        /// - <c>RemovePaths</c> 直接把路径整段抹掉 —— 用于**路径对模型毫无价值**的场景。
        ///
        /// 为什么需要它：单元测试实测发现，体检报告与清理规则说明里原本把
        /// "C:\Windows\Temp"、"D:\Clients\acme-corp\secret-project\out.log" 这类内容原样发出去，
        /// 而这两处的披露说明都写着"不含任何文件路径" —— 披露与行为不一致比不做披露更糟。
        /// </summary>
        public static string RemovePaths(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var result = DrivePath.Replace(text!, PathPlaceholder);
            result = UncPath.Replace(result, PathPlaceholder);
            // 光秃秃的盘符，例如体检里的 "C:\ 剩余 12.3 GB"
            result = BareDrive.Replace(result, PathPlaceholder);
            return result;
        }

        public const string PathPlaceholder = "<路径>";

        /// <summary>盘符开头的路径，直到空白或中文标点为止。</summary>
        private static readonly Regex DrivePath = new Regex(
            @"[A-Za-z]:\\[^\s，。；、）\)\]】]*", RegexOptions.Compiled);

        private static readonly Regex UncPath = new Regex(
            @"\\\\[^\s，。；、）\)\]】]+", RegexOptions.Compiled);

        /// <summary>没有反斜杠的裸盘符，例如 "C:" 或 "C:\"。</summary>
        private static readonly Regex BareDrive = new Regex(
            @"\b[A-Za-z]:\\?(?=\s|$|[，。；、）\)\]】])", RegexOptions.Compiled);

        /// <summary>从日志文本里挑出错误行附近的片段，避免整份日志外发。</summary>
        public static string ExtractLogExcerpt(string? logText, int maxLines = 40)
        {
            if (string.IsNullOrWhiteSpace(logText)) return string.Empty;

            var lines = logText!.Replace("\r\n", "\n").Split('\n');
            var picked = new List<string>();
            var errorIndexes = new List<int>();

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf("[ERROR]", StringComparison.OrdinalIgnoreCase) >= 0
                    || lines[i].IndexOf("[WARN ", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    errorIndexes.Add(i);
                }
            }

            if (errorIndexes.Count == 0)
            {
                // 没有错误行时给最后若干行，至少让模型看到上下文
                var start = Math.Max(0, lines.Length - maxLines);
                for (var i = start; i < lines.Length; i++) picked.Add(lines[i]);
                return Redact(string.Join(Environment.NewLine, picked.ToArray()));
            }

            // 每个错误行连同它后面的 3 行（异常堆栈）一起取，总量不超过 maxLines
            var seen = new HashSet<int>();
            foreach (var index in errorIndexes)
            {
                for (var i = index; i <= index + 3 && i < lines.Length; i++)
                {
                    if (seen.Add(i)) picked.Add(lines[i]);
                    if (picked.Count >= maxLines) break;
                }
                if (picked.Count >= maxLines) break;
            }

            return Redact(string.Join(Environment.NewLine, picked.ToArray()));
        }

        private static string SafeUserName()
        {
            try { return Environment.UserName ?? string.Empty; }
            catch (Exception) { return string.Empty; }
        }

        private static string SafeMachineName()
        {
            try { return Environment.MachineName ?? string.Empty; }
            catch (Exception) { return string.Empty; }
        }
    }

    /// <summary>
    /// 把结构化结果拼成 AI 请求。**纯函数**：输入相同则输出完全相同，
    /// 因此"这次会发送什么"既能被预览窗口如实展示，也能被单元测试逐字断言。
    ///
    /// 每类请求都对应一个明确的披露说明（<see cref="AiRequest.Disclosure"/>），
    /// 写的是"本次发送的数据范围"，不能含糊 —— 用户是据此决定要不要点的。
    /// </summary>
    public static class PromptBuilder
    {
        public const string DefaultModel = "deepseek-chat";

        private const string BaseSystemPrompt =
            "你是 Windows 系统工具 WinKit 的内置助手。回答要求："
            + "1) 用简体中文；2) 直接给结论，不要客套；3) 不确定就说不确定，不要编造；"
            + "4) 涉及删除/修改系统时，提醒用户先预览并确认，不要鼓动用户直接执行；"
            + "5) 回答控制在 300 字以内，必要时用短条目。";

        /// <summary>
        /// 自由文本的净化：先脱敏（用户名/机器名/密钥），再把路径整段抹掉。
        /// 体检报告与清理规则说明用这个 —— 这两处路径对模型没有价值，
        /// 而它们的披露说明都承诺了"不含任何文件路径"。
        /// </summary>
        private static string SanitizeFreeText(string? text)
        {
            return TextRedactor.RemovePaths(TextRedactor.Redact(text));
        }

        /// <summary>体检报告解读。发送的只有分数、各检查项的 Id/状态/净化后的文案，**不含任何路径**。</summary>
        public static AiRequest ForHealthReport(HealthReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));

            var sb = new StringBuilder();
            sb.AppendLine("以下是我这台 Windows 电脑的体检结果，请帮我解读：");
            sb.AppendLine("总分：" + report.Score + " / 100");
            sb.AppendLine("结论：" + report.Verdict);
            sb.AppendLine("检查项：");
            foreach (var item in report.Items)
            {
                // Detail 是自由文本：它由 HealthCheck 拼出来，可能含盘符甚至完整路径
                sb.Append("- [").Append(item.Status).Append("] ").Append(SanitizeFreeText(item.Title))
                  .Append("：").Append(SanitizeFreeText(item.Detail));
                if (!string.IsNullOrEmpty(item.Suggestion))
                {
                    sb.Append("（建议：").Append(SanitizeFreeText(item.Suggestion)).Append("）");
                }
                sb.AppendLine();
            }
            sb.AppendLine();
            sb.Append("请告诉我：1) 最该先处理的是哪一项、为什么；2) 哪些其实可以不用管。");

            return new AiRequest
            {
                Model = DefaultModel,
                Disclosure = "本次只发送体检分数、各检查项的状态与文字说明（其中的文件路径已替换为占位符）；不含用户名或任何文件内容。",
                Messages = new List<AiMessage>
                {
                    new AiMessage(AiRole.System, BaseSystemPrompt),
                    new AiMessage(AiRole.User, sb.ToString().TrimEnd()),
                },
            };
        }

        /// <summary>
        /// 进程解释。**只发进程名**。
        ///
        /// 这里刻意**不接收**可执行文件的"文件描述"（<c>FileDescription</c>）——
        /// 它是构建工具写进去的**无界自由文本**，实测会出现「由 alice 编译」这种内容：
        /// 构建机的用户名、公司名、内网路径都可能出现在里面，而脱敏函数不可能知道
        /// 哪个词是人名。既然发不出可靠的脱敏，就不要开这条通道。
        ///
        /// 这个决定来自单元测试的真实发现（AiPrivacyTests 里那条 DoesNotContain 断言
        /// 当场找到了 "由 alice 编译"）。宁可让模型少一点上下文，也不留一个说不清的数据出口。
        /// </summary>
        public static AiRequest ForProcess(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName)) throw new ArgumentException("进程名不能为空", nameof(processName));

            var name = processName.Trim();
            var sb = new StringBuilder();
            sb.Append("Windows 进程「").Append(TextRedactor.Redact(name)).Append("」是做什么的？");
            sb.AppendLine();
            sb.Append("请用一两句话说明它的作用，并告诉我：正常吗？可以结束吗？如果不能结束，说明后果。");

            return new AiRequest
            {
                Model = DefaultModel,
                Disclosure = "本次只发送进程名；不含文件路径、命令行参数、文件描述或任何文件内容。",
                Messages = new List<AiMessage>
                {
                    new AiMessage(AiRole.System, BaseSystemPrompt),
                    new AiMessage(AiRole.User, sb.ToString()),
                },
            };
        }

        /// <summary>清理项解释。发送规则 Id 与数量统计，**不发具体文件路径**。</summary>
        public static AiRequest ForCleanupRules(IEnumerable<CleanupRule> rules, int candidateCount, long totalBytes)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));

            var lines = new List<string>();
            foreach (var rule in rules)
            {
                lines.Add("- " + rule.Id + "（" + rule.Name + "）：待清理 " + rule.Targets.Count
                    + " 个位置，需要管理员权限=" + (rule.RequiresAdmin ? "是" : "否")
                    + "；说明：" + SanitizeFreeText(rule.Description));
            }

            var sb = new StringBuilder();
            sb.AppendLine("我准备用 WinKit 清理这些类别，共扫描到 " + candidateCount + " 项、"
                + Text.ByteSize.Format(totalBytes) + "：");
            sb.AppendLine(string.Join(Environment.NewLine, lines.ToArray()));
            sb.AppendLine();
            sb.Append("请逐类告诉我：这类东西可以放心清理吗？有没有哪一类是我应该谨慎对待的？"
                + "清理后会有什么可感知的影响？");

            return new AiRequest
            {
                Model = DefaultModel,
                Disclosure = "本次只发送清理规则的 Id、名称、说明与数量统计；不含任何文件路径或文件内容。",
                Messages = new List<AiMessage>
                {
                    new AiMessage(AiRole.System, BaseSystemPrompt),
                    new AiMessage(AiRole.User, sb.ToString().TrimEnd()),
                },
            };
        }

        /// <summary>
        /// 日志诊断。文本在进入本方法前**必须**已经过 <see cref="TextRedactor.ExtractLogExcerpt"/> 脱敏。
        ///
        /// 与另外三处的区别（这一点必须对用户讲清楚）：日志**保留路径结构** ——
        /// 诊断"写不进去"这类问题必须看到路径。因此路径里的目录名（客户名、项目名）
        /// 有可能被发出去，而脱敏函数不可能知道哪个词是敏感的业务名称。
        ///
        /// 兜底手段不是"再猜一个正则"，而是**预览窗口**：用户能逐字看到将要发送的内容、
        /// 并在发送前删掉不希望外发的部分。所以这里的 Disclosure 明确写出"包含文件路径，
        /// 请检查后再发送"，把不可自动判定的部分交还给用户，而不是假装已经处理干净。
        /// </summary>
        public static AiRequest ForLogDiagnosis(string redactedExcerpt)
        {
            if (string.IsNullOrWhiteSpace(redactedExcerpt))
            {
                throw new ArgumentException("日志片段为空", nameof(redactedExcerpt));
            }

            var sb = new StringBuilder();
            sb.AppendLine("这是 WinKit 的操作日志片段（已抹除用户名与计算机名）。请帮我判断：");
            sb.AppendLine("```");
            sb.AppendLine(redactedExcerpt.Trim());
            sb.AppendLine("```");
            sb.AppendLine();
            sb.Append("请说明：1) 最可能的失败原因；2) 我该怎么排查；3) 如果是我自己用错了，指出用错在哪。");

            return new AiRequest
            {
                Model = DefaultModel,
                Disclosure = "本次发送已脱敏的日志片段：用户名与计算机名已替换为占位符、疑似密钥已抹除，"
                    + "但**日志里的文件路径会被保留**（诊断需要它）。请在上方编辑框里检查一遍，"
                    + "删掉你不希望外发的目录名（例如客户名、项目名）后再点发送。",
                Messages = new List<AiMessage>
                {
                    new AiMessage(AiRole.System, BaseSystemPrompt),
                    new AiMessage(AiRole.User, sb.ToString().TrimEnd()),
                },
            };
        }
    }
}
