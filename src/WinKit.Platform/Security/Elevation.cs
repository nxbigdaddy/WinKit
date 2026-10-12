using System;
using System.IO;
using System.Runtime.Serialization;
using System.Security.Principal;
using System.Text;
using WinKit.Core.Diagnostics;
using WinKit.Core.Serialization;

namespace WinKit.Platform.Security
{
    /// <summary>
    /// 提权支持。设计要点（见《技术选型》第 5 节）：
    /// 默认以标准权限运行，只有明确的"管理员动作"才提权，且提权侧只接受白名单动作。
    ///
    /// 已知限制：通过 ShellExecute + runas 启动的提权进程**无法回传 stdout/stderr**（不继承管道），
    /// 因此结果必须走临时文件，并且带随机 token 校验，防止结果被伪造。
    /// </summary>
    /// <summary>
    /// 提权任务的结果。
    ///
    /// 刻意放在**命名空间层级**而不是嵌在 <see cref="Elevation"/> 里：
    /// 调用方写 `ElevatedOutcome` 就能用，不必记住它是嵌套类型。
    /// 这类"文档里是顶层类型、实现里是嵌套类型"的不一致，会让按文档写代码的人编译不过 ——
    /// 契约怎么写，代码就该是什么样。
    /// </summary>
    public sealed class ElevatedOutcome
    {
        public bool Success { get; set; }
        public bool UserCancelled { get; set; }
        public string? Output { get; set; }
        public string? Error { get; set; }
    }

    public static class Elevation
    {
        [DataContract]
        private sealed class ElevatedResultFile
        {
            [DataMember(Name = "token", Order = 0)]
            public string Token { get; set; } = string.Empty;

            [DataMember(Name = "success", Order = 1)]
            public bool Success { get; set; }

            [DataMember(Name = "output", Order = 2)]
            public string? Output { get; set; }

            [DataMember(Name = "error", Order = 3)]
            public string? Error { get; set; }
        }

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static bool IsElevated
        {
            get
            {
                try
                {
                    using (var identity = WindowsIdentity.GetCurrent())
                    {
                        if (identity == null) return false;
                        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                    }
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public static string CurrentUser
        {
            get
            {
                try
                {
                    using (var identity = WindowsIdentity.GetCurrent())
                    {
                        return identity?.Name ?? Environment.UserName;
                    }
                }
                catch (Exception)
                {
                    return Environment.UserName;
                }
            }
        }

        /// <summary>当前进程的可执行文件路径（打包后就是 WinKit.exe 自己）。</summary>
        public static string ExecutablePath
        {
            get
            {
                try
                {
                    var path = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                    if (!string.IsNullOrEmpty(path)) return path!;
                }
                catch (Exception) { }

                // net48 没有 Environment.ProcessPath，只能退回主模块文件名
                try
                {
                    using (var process = System.Diagnostics.Process.GetCurrentProcess())
                    {
                        var module = process.MainModule;
                        if (module != null && !string.IsNullOrEmpty(module.FileName)) return module.FileName;
                    }
                }
                catch (Exception) { }

                return string.Empty;
            }
        }

        /// <summary>
        /// 以管理员身份运行同一个 exe 的无界面任务，并等待结果。
        /// 用户拒绝 UAC 时返回 UserCancelled，而不是抛异常。
        /// </summary>
        public static ElevatedOutcome RunElevatedTask(string taskId, string payloadJson, TimeSpan timeout)
        {
            if (string.IsNullOrWhiteSpace(taskId)) throw new ArgumentException("任务标识不能为空", nameof(taskId));

            var exe = ExecutablePath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                return new ElevatedOutcome { Success = false, Error = "无法定位可执行文件，提权任务不可用" };
            }

            var token = Guid.NewGuid().ToString("N");
            var resultFile = Path.Combine(Path.GetTempPath(), "winkit-elev-" + token + ".json");
            var payloadBase64 = Convert.ToBase64String(Utf8NoBom.GetBytes(payloadJson ?? "{}"));

            var arguments = "--task " + taskId
                + " --payload " + payloadBase64
                + " --result \"" + resultFile + "\""
                + " --token " + token;

            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = arguments,
                    UseShellExecute = true,          // runas 必须有外壳执行
                    Verb = "runas",
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
                };

                using (var process = System.Diagnostics.Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return new ElevatedOutcome { Success = false, Error = "无法启动提权进程" };
                    }
                    if (!process.WaitForExit((int)Math.Max(1000, timeout.TotalMilliseconds)))
                    {
                        TryKill(process);
                        return new ElevatedOutcome { Success = false, Error = "提权任务超时未返回" };
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // 用户在 UAC 对话框点了"否"，错误码 1223
                if (ex.NativeErrorCode == 1223)
                {
                    return new ElevatedOutcome { Success = false, UserCancelled = true, Error = "用户取消了管理员权限请求" };
                }
                Log.Warn("提权失败：" + ex.Message);
                return new ElevatedOutcome { Success = false, Error = "提权失败：" + ex.Message };
            }
            catch (Exception ex)
            {
                Log.Error("提权任务异常：" + ex.Message, ex);
                return new ElevatedOutcome { Success = false, Error = ex.Message };
            }

            return ReadResult(resultFile, token);
        }

        private static ElevatedOutcome ReadResult(string resultFile, string expectedToken)
        {
            try
            {
                if (!File.Exists(resultFile))
                {
                    return new ElevatedOutcome { Success = false, Error = "提权任务未回传结果文件" };
                }

                var text = File.ReadAllText(resultFile, Utf8NoBom);
                var payload = Json.Deserialize<ElevatedResultFile>(text);

                if (payload == null)
                {
                    return new ElevatedOutcome { Success = false, Error = "提权任务回传的结果无法解析" };
                }
                if (!string.Equals(payload.Token, expectedToken, StringComparison.Ordinal))
                {
                    return new ElevatedOutcome { Success = false, Error = "提权任务回传结果校验失败（token 不匹配）" };
                }

                return new ElevatedOutcome
                {
                    Success = payload.Success,
                    Output = payload.Output,
                    Error = payload.Error,
                };
            }
            catch (Exception ex)
            {
                return new ElevatedOutcome { Success = false, Error = "读取提权结果失败：" + ex.Message };
            }
            finally
            {
                try { if (File.Exists(resultFile)) File.Delete(resultFile); } catch (Exception) { }
            }
        }

        /// <summary>提权侧调用：把结果写回文件供父进程读取。</summary>
        public static void WriteResult(string resultFile, string token, bool success, string? output, string? error)
        {
            var payload = new ElevatedResultFile
            {
                Token = token ?? string.Empty,
                Success = success,
                Output = output,
                Error = error,
            };
            var dir = Path.GetDirectoryName(resultFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(resultFile, Json.Serialize(payload), Utf8NoBom);
        }

        private static void TryKill(System.Diagnostics.Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (Exception) { }
        }
    }
}
