using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using WinKit.Core.Diagnostics;

namespace WinKit.Platform.Ai
{
    /// <summary>
    /// API Key 的本机加密存储。
    ///
    /// 三条硬约束：
    /// 1. **绝不明文落盘** —— 明文写进便携模式的 config.json 等于把密钥放在 exe 旁边，
    ///    拷走 U 盘就泄露。这里用 Windows DPAPI（CurrentUser 作用域）加密，
    ///    换用户或换机器都解不开。
    /// 2. **绝不进日志** —— 本类只在失败时记录"文件路径"级别的信息，从不记录密钥内容。
    /// 3. **不放进 config.json** —— 配置会被导出/迁移，密钥不该跟着走；它是独立的 .bin 文件。
    /// </summary>
    public sealed class ApiKeyStore
    {
        private const string FileName = "deepseek.key";

        // 附加熵：让密文与"随便一个 DPAPI 加密的文件"不可互换
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WinKit.DeepSeek.ApiKey.v1");

        private readonly string _path;

        public ApiKeyStore(string secretsDirectory)
        {
            if (string.IsNullOrWhiteSpace(secretsDirectory))
            {
                throw new ArgumentException("密钥目录不能为空", nameof(secretsDirectory));
            }
            Directory.CreateDirectory(secretsDirectory);
            _path = Path.Combine(secretsDirectory, FileName);
        }

        public string FilePath => _path;

        public bool HasKey
        {
            get
            {
                try { return File.Exists(_path) && new FileInfo(_path).Length > 0; }
                catch (Exception) { return false; }
            }
        }

        public void Save(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API Key 不能为空", nameof(apiKey));

            var trimmed = apiKey.Trim();
            var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(trimmed), Entropy, DataProtectionScope.CurrentUser);

            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // 先写临时文件再替换：避免写一半断电留下半截密文
            var temporary = _path + ".tmp";
            File.WriteAllBytes(temporary, blob);
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(temporary, _path);

            Log.Info("已保存 DeepSeek API Key（DPAPI 加密，长度 " + trimmed.Length + " 字符）");
        }

        /// <summary>读取明文密钥。解密失败（换了用户/机器、文件损坏）返回 null 并记日志。</summary>
        public string? Load()
        {
            if (!HasKey) return null;

            try
            {
                var blob = File.ReadAllBytes(_path);
                var plain = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
                var key = Encoding.UTF8.GetString(plain);
                return string.IsNullOrWhiteSpace(key) ? null : key;
            }
            catch (CryptographicException ex)
            {
                // 不打印密钥内容，只说清原因
                Log.Warn("无法解密已保存的 API Key（可能换了 Windows 用户或换了机器），需要重新填写：" + ex.Message);
                return null;
            }
            catch (Exception ex)
            {
                Log.Warn("读取 API Key 失败：" + ex.Message);
                return null;
            }
        }

        public void Delete()
        {
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
                Log.Info("已删除本机保存的 DeepSeek API Key");
            }
            catch (Exception ex)
            {
                Log.Warn("删除 API Key 失败：" + ex.Message);
            }
        }

        /// <summary>给界面用的掩码串，例如 <c>sk-511d••••••••f3ad</c>。绝不返回完整密钥。</summary>
        public static string Mask(string? apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return "（未配置）";

            var key = apiKey!.Trim();
            if (key.Length <= 12) return new string('•', key.Length);

            return key.Substring(0, 7) + new string('•', 8) + key.Substring(key.Length - 4);
        }
    }
}
