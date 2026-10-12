using System;
using System.Collections.Generic;
using WinKit.Core.Ai;
using WinKit.Core.Cleanup;
using WinKit.Core.Diagnostics;
using WinKit.Core.Serialization;
using Xunit;

namespace WinKit.Core.Tests
{
    /// <summary>
    /// AI 隐私边界的守卫测试。
    ///
    /// 为什么要有这一组：AI 助手是**可选联网**功能，"到底会把什么发送到外部服务"
    /// 是这个功能最大的风险点 —— 路径与文件名里天然带着客户名、项目名、用户名。
    /// <see cref="PromptBuilder"/> 与 <see cref="TextRedactor"/> 被刻意写成纯函数，
    /// 就是为了让这件事可以被逐字断言，而不是靠人工通读代码去相信。
    ///
    /// 本文件只断言**实际行为**。发现与注释承诺不符的地方（隐私缺口）时，
    /// 断言写的是实际行为，并在注释里标明缺口 —— 不写一个注定失败的测试。
    /// </summary>
    public class AiPrivacyTests
    {
        // ==================================================================
        // A. TextRedactor.Redact —— 脱敏（最重要）
        // ==================================================================

        [Fact]
        public void Redact_replaces_user_profile_name_in_path()
        {
            var result = TextRedactor.Redact(@"C:\Users\alice\AppData\Local\Temp\x.txt");

            Assert.Contains(@"C:\Users\<用户>\", result);
            Assert.DoesNotContain("alice", result);
            // 路径结构必须保留：诊断报错需要它
            Assert.Contains(@"AppData\Local\Temp\x.txt", result);
        }

        [Fact]
        public void Redact_replaces_documents_and_settings_name_in_path()
        {
            var result = TextRedactor.Redact(@"C:\Documents and Settings\bob\Desktop\a.log");

            Assert.Contains(@"C:\Documents and Settings\<用户>", result);
            Assert.DoesNotContain("bob", result);
            Assert.Contains(@"Desktop\a.log", result);
        }

        [Fact]
        public void Redact_is_case_insensitive_for_profile_path()
        {
            var lower = TextRedactor.Redact(@"c:\users\alice\a.txt");
            var upper = TextRedactor.Redact(@"C:\USERS\ALICE\a.txt");

            Assert.DoesNotContain("alice", lower);
            Assert.DoesNotContain("ALICE", upper);
            Assert.Contains(TextRedactor.UserPlaceholder, lower);
            Assert.Contains(TextRedactor.UserPlaceholder, upper);
        }

        [Fact]
        public void Redact_replaces_current_user_name_outside_of_paths()
        {
            var userName = Environment.UserName;
            // 源码对长度 < 3 的用户名会跳过替换，这是刻意的防误伤（3 个字符以下几乎必然误匹配）。
            // 环境不满足时跳过断言，而不是让它偶发变红。
            if (string.IsNullOrEmpty(userName) || userName.Length < 3) return;

            var result = TextRedactor.Redact("用户 " + userName + " 正在运行");

            Assert.DoesNotContain(userName, result);
            Assert.Contains(TextRedactor.UserPlaceholder, result);
        }

        [Fact]
        public void Redact_replaces_current_machine_name()
        {
            var machineName = Environment.MachineName;
            if (string.IsNullOrEmpty(machineName) || machineName.Length < 3) return;

            var result = TextRedactor.Redact("在计算机 " + machineName + " 上执行");

            Assert.DoesNotContain(machineName, result);
            Assert.Contains(TextRedactor.MachinePlaceholder, result);
        }

        [Fact]
        public void Redact_removes_sk_prefixed_secret()
        {
            var result = TextRedactor.Redact("Authorization: Bearer sk-deadbeefdeadbeefdeadbeefdeadbeef");

            Assert.DoesNotContain("sk-deadbeef", result);
            Assert.Contains("<已抹除的疑似密钥>", result);
        }

        [Fact]
        public void Redact_removes_long_hex_like_secret()
        {
            var result = TextRedactor.Redact("hash=d41d8cd98f00b204e9800998ecf8427e 结束");

            Assert.DoesNotContain("d41d8cd98f00b204e9800998ecf8427e", result);
            Assert.Contains("<已抹除的疑似密钥>", result);
        }

        [Fact]
        public void Redact_keeps_ordinary_path_structure_intact()
        {
            // 路径结构不该被删掉，否则诊断信息就没有价值了
            var result = TextRedactor.Redact(@"正在扫描 C:\Windows\Temp");

            Assert.Contains(@"C:\Windows\Temp", result);
        }

        [Fact]
        public void Redact_returns_empty_for_null_and_empty()
        {
            Assert.Equal(string.Empty, TextRedactor.Redact(null));
            Assert.Equal(string.Empty, TextRedactor.Redact(""));
        }

        // ==================================================================
        // B. 路径缩短（只留"…\父目录\文件名"）
        // ==================================================================

        [Fact]
        public void ShortenPath_keeps_only_parent_and_file_name()
        {
            var result = TextRedactor.ShortenPath(@"C:\Users\alice\projects\secret-client\app.exe");

            Assert.Contains("app.exe", result);
            Assert.Contains("secret-client", result);
            Assert.DoesNotContain("alice", result);
            Assert.DoesNotContain(@"\projects\", result);
            Assert.DoesNotContain(@"C:\Users", result);
        }

        [Fact]
        public void ShortenPath_returns_empty_for_null_or_blank()
        {
            Assert.Equal(string.Empty, TextRedactor.ShortenPath(null));
            Assert.Equal(string.Empty, TextRedactor.ShortenPath(""));
            Assert.Equal(string.Empty, TextRedactor.ShortenPath("   "));
        }

        [Fact]
        public void FileNameOnly_returns_bare_file_name()
        {
            var result = TextRedactor.FileNameOnly(@"C:\Users\alice\app.exe");

            Assert.Equal("app.exe", result);
            Assert.DoesNotContain(@"\", result);
            Assert.Equal(string.Empty, TextRedactor.FileNameOnly(null));
            Assert.Equal(string.Empty, TextRedactor.FileNameOnly(""));
        }

        // ==================================================================
        // C. ExtractLogExcerpt —— 只取出错行附近，且已经过脱敏
        // ==================================================================

        /// <summary>
        /// 构造一段日志：两个 [ERROR] 行，彼此隔得足够远，并且每个错误行后面
        /// 都跟着 3 行可区分的上下文，用来验证"错误行 + 其后 3 行"这条规则。
        /// </summary>
        private static string TwoErrorLog()
        {
            var lines = new List<string>
            {
                "01 普通行甲",
                "02 err marker 1 [ERROR] 第一次失败",
                "03 err context 1a",
                "04 err context 1b",
                "05 err context 1c",
                "06 err context 1d（超出 3 行窗口，不应被选中）",
                "07 普通行乙",
                "08 普通行丙",
                "09 err marker 2 [ERROR] 第二次失败",
                "10 err context 2a",
                "11 err context 2b",
                "12 err context 2c",
                "13 普通行丁",
            };
            return string.Join("\n", lines.ToArray());
        }

        [Fact]
        public void ExtractLogExcerpt_keeps_error_lines_with_three_following_lines()
        {
            var result = TextRedactor.ExtractLogExcerpt(TwoErrorLog());

            Assert.Contains("err marker 1 [ERROR] 第一次失败", result);
            Assert.Contains("err context 1a", result);
            Assert.Contains("err context 1b", result);
            Assert.Contains("err context 1c", result);

            Assert.Contains("err marker 2 [ERROR] 第二次失败", result);
            Assert.Contains("err context 2a", result);
            Assert.Contains("err context 2b", result);
            Assert.Contains("err context 2c", result);

            // 第 4 行以后就不属于"错误行 + 3 行"的窗口了
            Assert.DoesNotContain("err context 1d", result);
            Assert.DoesNotContain("普通行甲", result);
        }

        [Fact]
        public void ExtractLogExcerpt_respects_max_lines()
        {
            var lines = new List<string>();
            for (var i = 0; i < 30; i++) lines.Add("[ERROR] 第 " + i + " 行失败");
            var log = string.Join("\n", lines.ToArray());

            var result = TextRedactor.ExtractLogExcerpt(log, 5);

            var produced = result.Replace("\r\n", "\n").Split('\n');
            Assert.True(produced.Length <= 5, "maxLines=5 时不应超过 5 行，实际 " + produced.Length);
        }

        [Fact]
        public void ExtractLogExcerpt_falls_back_to_tail_when_there_is_no_problem_line()
        {
            var log = "第一行\n第二行\n第三行\n最后一行";
            var result = TextRedactor.ExtractLogExcerpt(log, 2);

            Assert.Contains("最后一行", result);
            Assert.Contains("第三行", result);
            Assert.DoesNotContain("第一行", result);
        }

        [Fact]
        public void ExtractLogExcerpt_returns_redacted_text()
        {
            var log = "启动\n[ERROR] 无法读取 C:\\Users\\alice\\secret-client\\config.json\n   at Foo()";

            var result = TextRedactor.ExtractLogExcerpt(log);

            Assert.Contains(@"C:\Users\<用户>\", result);
            Assert.DoesNotContain("alice", result);
            Assert.Contains("at Foo()", result);
        }

        [Fact]
        public void ExtractLogExcerpt_returns_empty_for_null_or_blank()
        {
            Assert.Equal(string.Empty, TextRedactor.ExtractLogExcerpt(null));
            Assert.Equal(string.Empty, TextRedactor.ExtractLogExcerpt(""));
            Assert.Equal(string.Empty, TextRedactor.ExtractLogExcerpt("   "));
        }

        [Fact]
        public void ExtractLogExcerpt_treats_bracketed_warn_with_trailing_space_as_a_problem_line()
        {
            // 代码里匹配的是 "[WARN "（中间有空格），不是 "[WARN]"。
            // 这里按实际行为断言：只要出现 "[WARN " 就被当成问题行。
            var log = "普通行甲\n[WARN ] 磁盘空间偏低\n警告上下文行\n普通行丁";

            var result = TextRedactor.ExtractLogExcerpt(log);

            Assert.Contains("[WARN ] 磁盘空间偏低", result);
            Assert.Contains("警告上下文行", result);
            Assert.DoesNotContain("普通行甲", result);
        }

        // ==================================================================
        // D. PromptBuilder —— "这次到底发送了什么"
        // ==================================================================

        /// <summary>造一份报告：磁盘空闲 50%（Ok）、内存占用 30%（Ok）、标准权限、无历史失败。</summary>
        private static HealthReport SampleReport()
        {
            var inputs = new HealthInputs
            {
                SystemDriveName = "C:\\",
                SystemDriveTotalBytes = 100UL * 1024 * 1024 * 1024,
                SystemDriveFreeBytes = 50UL * 1024 * 1024 * 1024,
                MemoryTotalBytes = 16UL * 1024 * 1024 * 1024,
                MemoryUsedBytes = (ulong)(16UL * 1024 * 1024 * 1024 * 0.30d),
                CleanupCandidateBytes = 0,
                CleanupCandidateCount = 0,
                RecycleBinBytes = 0,
                QuarantineBytes = 0,
                RunningAsAdmin = true,
                RecentFailedOperations = 0,
            };
            return HealthCheck.Evaluate(inputs);
        }

        private static string UserContent(AiRequest request)
        {
            return request.Messages[1].Content;
        }

        [Fact]
        public void Health_report_request_contains_only_scores_and_titles()
        {
            var report = SampleReport();
            var request = PromptBuilder.ForHealthReport(report);

            // 逐字断言"发送内容"里只有分数、状态与文案
            var payload = request.Messages[0].Content + "\n" + request.Messages[1].Content;

            Assert.Contains(report.Score.ToString(), payload);
            Assert.Contains(report.Verdict, payload);
            foreach (var item in report.Items)
            {
                Assert.Contains(item.Title, payload);
            }

            // 必须显式声明"不含路径"，否则用户无从判断数据范围
            Assert.False(string.IsNullOrWhiteSpace(request.Disclosure));
            Assert.Contains("不含", request.Disclosure);
            Assert.Contains("路径", request.Disclosure);

            // 结构：恰好 system + user 两条
            Assert.Equal(2, request.Messages.Count);
            Assert.Equal(AiRole.System, request.Messages[0].Role);
            Assert.Equal(AiRole.User, request.Messages[1].Role);
            Assert.Equal(PromptBuilder.DefaultModel, request.Model);
        }

        /// <summary>
        /// 隐私缺口（实测行为，断言的是实际行为而不是期望行为）：
        ///
        /// <see cref="PromptBuilder.ForHealthReport"/> **不做任何脱敏**（源码里没有调用
        /// <see cref="TextRedactor"/>），而 <see cref="HealthItem.Detail"/> 是自由文本。
        /// 真实链路里 <c>HealthViewModel</c> 会把 <c>HealthInputs.SystemDriveName</c> 设为
        /// 盘符（<c>DiskVolume.Name</c>，形如 "C:\"），<see cref="HealthCheck.Evaluate"/>
        /// 又把它拼进 Detail（<c>"C:\ 剩余 12.3 GB（占 4.2%）"</c>），
        /// 于是"不含任何路径"这句注释承诺当场失效。
        ///
        /// 证据（本用例断言的就是这个）：盘符与 Detail 里的用户目录一次不落地进入最终 prompt。
        /// 修好之后本用例应改写为 DoesNotContain。
        /// </summary>
        [Fact]
        public void Health_report_does_not_leak_the_drive_designator()
        {
            // 与 App 层真实取值一致：SystemDriveName 就是卷名 "C:\"
            var inputs = new HealthInputs
            {
                SystemDriveName = "C:\\",
                SystemDriveTotalBytes = 100UL * 1024 * 1024 * 1024,
                SystemDriveFreeBytes = 50UL * 1024 * 1024 * 1024,
                MemoryTotalBytes = 16UL * 1024 * 1024 * 1024,
                MemoryUsedBytes = (ulong)(16UL * 1024 * 1024 * 1024 * 0.30d),
                CleanupCandidateBytes = 0,
                CleanupCandidateCount = 0,
                RecycleBinBytes = 0,
                QuarantineBytes = 0,
                RunningAsAdmin = true,
                RecentFailedOperations = 0,
            };
            var report = HealthCheck.Evaluate(inputs);
            var payload = UserContent(PromptBuilder.ForHealthReport(report));

            // 缺口已修：ForHealthReport 现在对 Title/Detail/Suggestion 都跑 SanitizeFreeText
            // （Redact 抹用户名 → RemovePaths 把整段路径换成 <路径>）。
            Assert.DoesNotContain(@"C:\", payload);
        }

        /// <summary>
        /// Detail 是自由文本，是最容易漏的地方。这条曾经是缺口的证据
        /// （用户目录名与用户名原样外发），现在 ForHealthReport 会先 Redact 抹用户名、
        /// 再 RemovePaths 把整段路径换成占位符，因此断言反转为"不得出现"。
        /// </summary>
        [Fact]
        public void Health_report_does_not_leak_user_directory_names_inside_item_detail()
        {
            var leaky = new HealthReport(
                new List<HealthItem>
                {
                    new HealthItem("disk.free", "系统盘空间", HealthStatus.Ok,
                        @"C:\Users\alice\projects\secret-client 剩余 12 GB", string.Empty),
                },
                100);

            var payload = UserContent(PromptBuilder.ForHealthReport(leaky));

            Assert.DoesNotContain(@"C:\Users\alice\projects\secret-client", payload);
            Assert.DoesNotContain("alice", payload);
        }

        /// <summary>
        /// 隐私缺口的第二段证据：日志脱敏入口本身漏掉了 <c>C:\Users\</c> 之外的用户目录形状。
        /// "用户目录名"这个 regex 只认 <c>C:\Users\&lt;name&gt;</c> 与
        /// <c>C:\Documents and Settings\&lt;name&gt;</c>，除此之外的目录名（例如
        /// <c>D:\Work\</c>、<c>C:\projects\</c>）不在覆盖范围内。
        /// </summary>
        [Fact]
        public void ExtractLogExcerpt_only_covers_users_and_documents_and_settings()
        {
            var log = "[ERROR] 无法写入 D:\\Clients\\acme-corp\\secret-project\\out.log";

            var result = TextRedactor.ExtractLogExcerpt(log);

            Assert.Contains("acme-corp", result);       // 客户名照样外发
            Assert.Contains("secret-project", result);  // 项目名照样外发
        }

        [Fact]
        public void Process_request_without_description_contains_no_path()
        {
            var request = PromptBuilder.ForProcess("chrome");

            var payload = request.Messages[0].Content + "\n" + request.Messages[1].Content;

            Assert.Contains("chrome", payload);
            Assert.DoesNotContain(@"C:\", payload);
            Assert.DoesNotContain(".exe", payload);   // 传参就没传路径，不能凭空引入
            Assert.False(string.IsNullOrWhiteSpace(request.Disclosure));
            Assert.Equal(2, request.Messages.Count);
            Assert.Equal(AiRole.System, request.Messages[0].Role);
            Assert.Equal(AiRole.User, request.Messages[1].Role);
        }

        /// <summary>
        /// 防回归：<c>ForProcess</c> 曾经接收第二个参数 description（可执行文件的
        /// FileDescription），那是构建工具写的**无界自由文本**，"由 alice 编译" 这种
        /// 构建机用户名会原样外发，脱敏函数不可能知道哪个词是人名。
        /// 该通道已被彻底移除（参数删掉而不是忽略，让调用点在编译期断掉）。
        ///
        /// 本用例覆盖三件事：通道确实关闭、披露文案如实、误导性的参数名不复存在。
        /// </summary>
        [Fact]
        public void Process_request_has_no_file_description_channel_at_all()
        {
            // 1) 只剩一个参数：反射确认没有第二个形参（有人加回来就红）
            var method = typeof(PromptBuilder).GetMethod(
                "ForProcess", new[] { typeof(string) });
            Assert.True(method != null, "ForProcess(string) 必须存在");
            Assert.Single(method!.GetParameters());

            // 2) 披露文案必须写明不含"文件描述"
            var request = PromptBuilder.ForProcess("chrome");
            Assert.False(string.IsNullOrWhiteSpace(request.Disclosure));
            Assert.Contains("文件描述", request.Disclosure);
            Assert.Contains("不含", request.Disclosure);

            // 3) 正文里不得出现"文件描述"这条通道留下的任何痕迹
            var payload = request.Messages[0].Content + "\n" + request.Messages[1].Content;
            Assert.DoesNotContain("文件描述", payload);
        }

        /// <summary>
        /// 模拟调用方误用：把一段含用户名的自由文本当成进程名传进来。
        /// 进程名现在也走 <see cref="TextRedactor.Redact"/>，当前用户名会被替换。
        /// </summary>
        [Fact]
        public void Process_name_is_redacted_when_caller_mistakenly_passes_free_text()
        {
            var userName = Environment.UserName;
            // 长度 < 3 时源码刻意跳过用户名替换，此时不做断言。
            if (string.IsNullOrEmpty(userName) || userName.Length < 3) return;

            var request = PromptBuilder.ForProcess("由 " + userName + " 编译的进程");
            var payload = request.Messages[0].Content + "\n" + request.Messages[1].Content;

            Assert.DoesNotContain(userName, payload);
            Assert.Contains(TextRedactor.UserPlaceholder, payload);
        }

        /// <summary>
        /// 隐私缺口（实测行为）：进程名通道关闭之后仍然存在的残余风险 ——
        /// <see cref="TextRedactor.Redact"/> 只认**当前登录用户**的名字。
        /// 传进来的自由文本里若含**别人的**名字、客户名、项目名，会原样外发。
        ///
        /// 证据：本机用户名为 "Administrator"，把 "由 alice 编译的进程" 作为进程名传入，
        /// 最终 prompt 里 "alice" 依然在。
        /// </summary>
        [Fact]
        public void Process_name_keeps_foreign_names_that_are_not_the_current_user()
        {
            var foreign = "alice";
            if (string.Equals(Environment.UserName, foreign, StringComparison.OrdinalIgnoreCase))
            {
                return; // 恰好本机用户就叫 alice 时复现不出来
            }

            var request = PromptBuilder.ForProcess("由 alice 编译的进程");

            Assert.Contains(foreign, UserContent(request));
        }

        [Fact]
        public void Cleanup_rules_request_sends_ids_and_counts_but_no_paths()
        {
            var rules = CleanupRuleCatalog.CreateDefault();
            var request = PromptBuilder.ForCleanupRules(rules, 12, 1024L * 1024 * 500);

            var payload = request.Messages[0].Content + "\n" + request.Messages[1].Content;

            foreach (var rule in rules)
            {
                Assert.Contains(rule.Id, payload);
            }

            Assert.Contains("12", payload);

            // Targets 里的具体路径不得外发（用真实展开后的路径来验，防止只删了模板文字）
            var tempPath = Environment.ExpandEnvironmentVariables("%TEMP%");
            if (!string.IsNullOrWhiteSpace(tempPath)) Assert.DoesNotContain(tempPath, payload);

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData)) Assert.DoesNotContain(localAppData, payload);

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile)) Assert.DoesNotContain(userProfile, payload);

            // 模板里的路径形状也不该出现（但注意：规则的 Description 是自由文本，
            // 见下面 Process/Health 之外的第三条缺口用例）
            Assert.DoesNotContain(@"C:\Users\", payload);

            Assert.False(string.IsNullOrWhiteSpace(request.Disclosure));
            Assert.Equal(2, request.Messages.Count);
        }

        /// <summary>
        /// 隐私缺口（实测行为）：
        /// 这条曾经是缺口的证据：规则 Description 里的固定系统路径（例如
        /// "位于 C:\Windows\Temp"、"位于 C:\Windows\Minidump"）会原样外发，
        /// 与 <c>Disclosure</c> 里"不含任何文件路径"的说法冲突。
        /// 现在 Description 会先过 SanitizeFreeText（Redact + RemovePaths），断言已反转。
        /// </summary>
        [Fact]
        public void Cleanup_rules_request_does_not_leak_absolute_system_paths_from_rule_descriptions()
        {
            var payload = UserContent(
                PromptBuilder.ForCleanupRules(CleanupRuleCatalog.CreateDefault(), 12, 1024L * 1024 * 500));

            Assert.DoesNotContain(@"C:\Windows\Temp", payload);
            Assert.DoesNotContain(@"C:\Windows\Minidump", payload);
        }

        [Fact]
        public void Cleanup_rules_request_never_contains_a_target_template_verbatim()
        {
            // Targets 的**模板字符串本身**（"%TEMP%" 这类）绝不能被拼进 prompt；
            // 展开后的真实路径同样不允许（见上一条）。规则 Description 里的固定系统路径
            // 也已经不再外发 —— 它现在会过 SanitizeFreeText。
            var rules = CleanupRuleCatalog.CreateDefault();
            var payload = UserContent(PromptBuilder.ForCleanupRules(rules, 1, 1));

            foreach (var rule in rules)
            {
                foreach (var target in rule.Targets)
                {
                    Assert.DoesNotContain(target, payload);
                }
            }

            Assert.DoesNotContain("%TEMP%", payload);
            Assert.DoesNotContain("%LOCALAPPDATA%", payload);
            Assert.DoesNotContain("%SystemRoot%", payload);
        }

        [Fact]
        public void Log_diagnosis_rejects_blank_excerpt()
        {
            Assert.Throws<ArgumentException>(() => PromptBuilder.ForLogDiagnosis(""));
            Assert.Throws<ArgumentException>(() => PromptBuilder.ForLogDiagnosis("   "));
            Assert.Throws<ArgumentException>(() => PromptBuilder.ForLogDiagnosis(null!));
        }

        [Fact]
        public void Log_diagnosis_wraps_the_already_redacted_excerpt()
        {
            var request = PromptBuilder.ForLogDiagnosis("已脱敏的片段");

            Assert.Contains("已脱敏的片段", UserContent(request));
            Assert.False(string.IsNullOrWhiteSpace(request.Disclosure));
            Assert.Equal(2, request.Messages.Count);
            Assert.Equal(AiRole.System, request.Messages[0].Role);
            Assert.Equal(AiRole.User, request.Messages[1].Role);
        }

        [Fact]
        public void Null_arguments_are_rejected()
        {
            Assert.Throws<ArgumentNullException>(() => PromptBuilder.ForHealthReport(null!));
            Assert.Throws<ArgumentNullException>(() => PromptBuilder.ForCleanupRules(null!, 0, 0));
            Assert.Throws<ArgumentException>(() => PromptBuilder.ForProcess(""));
            Assert.Throws<ArgumentException>(() => PromptBuilder.ForProcess("   "));
            Assert.Throws<ArgumentException>(() => PromptBuilder.ForProcess(null!));
        }

        // ==================================================================
        // E. AiRequest 的序列化与预览
        // ==================================================================

        private static AiRequest ThreeRoleRequest()
        {
            return new AiRequest
            {
                Model = PromptBuilder.DefaultModel,
                Disclosure = "测试用披露文案",
                Messages = new List<AiMessage>
                {
                    new AiMessage(AiRole.System, "系统指令内容 sys-1"),
                    new AiMessage(AiRole.User, "用户问题内容 user-1"),
                    new AiMessage(AiRole.Assistant, "模型回复内容 asst-1"),
                },
            };
        }

        [Fact]
        public void AiRequest_json_round_trip_preserves_roles_and_contents()
        {
            var original = ThreeRoleRequest();

            var json = Json.Serialize(original);
            var restored = Json.Deserialize<AiRequest>(json);

            Assert.NotNull(restored);
            Assert.Equal(original.Messages.Count, restored!.Messages.Count);
            for (var i = 0; i < original.Messages.Count; i++)
            {
                Assert.Equal(original.Messages[i].Role, restored.Messages[i].Role);
                Assert.Equal(original.Messages[i].Content, restored.Messages[i].Content);
            }

            // 三种角色都要在往返里活下来，不能都退化成 user
            Assert.Equal(AiRole.System, restored.Messages[0].Role);
            Assert.Equal(AiRole.User, restored.Messages[1].Role);
            Assert.Equal(AiRole.Assistant, restored.Messages[2].Role);
        }

        [Fact]
        public void AiRequest_json_contains_the_message_contents_themselves()
        {
            // 边界即"这个 JSON 就是将要发出去的东西"：内容必须逐字在 JSON 里
            var json = Json.Serialize(ThreeRoleRequest());

            Assert.Contains("user-1", json);
            Assert.Contains("asst-1", json);
        }

        [Fact]
        public void ToPreviewText_labels_every_role_in_chinese()
        {
            var text = ThreeRoleRequest().ToPreviewText();

            Assert.Contains("【系统指令】", text);
            Assert.Contains("【你的问题】", text);
            Assert.Contains("【模型回复】", text);

            Assert.Contains("系统指令内容 sys-1", text);
            Assert.Contains("用户问题内容 user-1", text);
            Assert.Contains("模型回复内容 asst-1", text);
        }

        [Fact]
        public void TotalCharacters_sums_all_contents_and_treats_null_as_zero()
        {
            var request = new AiRequest
            {
                Messages = new List<AiMessage>
                {
                    new AiMessage(AiRole.User, "abc"),
                    new AiMessage { Role = AiRole.Assistant, Content = null! },
                    new AiMessage(AiRole.System, "de"),
                },
            };

            Assert.Equal(5, request.TotalCharacters);
        }

        [Fact]
        public void AiRequest_defaults_match_the_documented_values()
        {
            var request = new AiRequest();

            Assert.Equal("deepseek-chat", request.Model);
            Assert.True(request.Stream);
            Assert.NotNull(request.Messages);
            Assert.Equal(string.Empty, request.Disclosure);
        }

        [Fact]
        public void AiException_user_messages_are_distinct_per_kind()
        {
            var kinds = new[]
            {
                AiErrorKind.NotConfigured,
                AiErrorKind.Unauthorized,
                AiErrorKind.RateLimited,
                AiErrorKind.NetworkUnavailable,
                AiErrorKind.Cancelled,
            };

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kind in kinds)
            {
                var message = new AiException(kind, "内部细节").UserMessage;
                Assert.False(string.IsNullOrWhiteSpace(message), kind + " 的 UserMessage 不能为空");
                seen.Add(message);
            }

            Assert.Equal(kinds.Length, seen.Count);
        }

        [Fact]
        public void Network_unavailable_message_repeats_the_no_network_promise()
        {
            var message = new AiException(AiErrorKind.NetworkUnavailable, "内部细节").UserMessage;

            Assert.Contains("本工具在未启用 AI 时不会联网", message);
        }

        [Fact]
        public void AiException_keeps_kind_and_inner_exception()
        {
            var inner = new InvalidOperationException("底层原因");
            var ex = new AiException(AiErrorKind.ServerError, "服务端异常", inner);

            Assert.Equal(AiErrorKind.ServerError, ex.Kind);
            Assert.Same(inner, ex.InnerException);
            Assert.False(string.IsNullOrWhiteSpace(ex.UserMessage));
        }

        [Fact]
        public void AiRequest_from_prompt_builder_has_a_non_empty_preview_and_disclosure()
        {
            // 预览窗口的输入就是这两个：必须有内容、必须如实说明数据范围
            var requests = new[]
            {
                PromptBuilder.ForHealthReport(SampleReport()),
                PromptBuilder.ForProcess("chrome"),
                PromptBuilder.ForCleanupRules(CleanupRuleCatalog.CreateDefault(), 5, 1024L * 1024),
                PromptBuilder.ForLogDiagnosis("已脱敏的片段"),
            };

            foreach (var request in requests)
            {
                Assert.False(string.IsNullOrWhiteSpace(request.Disclosure));
                Assert.False(string.IsNullOrWhiteSpace(request.ToPreviewText()));
                Assert.True(request.TotalCharacters > 0);
            }
        }
    }
}
