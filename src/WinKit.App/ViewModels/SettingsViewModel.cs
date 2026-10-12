using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using WinKit.App.Mvvm;
using WinKit.App.Services;
using WinKit.Core.Ai;
using WinKit.Core.Configuration;
using WinKit.Core.Diagnostics;
using WinKit.Platform.Ai;

namespace WinKit.App.ViewModels
{
    /// <summary>
    /// 设置页。所有可写项都是"改完立即落盘"（ConfigStore 内部是原子写入），
    /// 因此不存在"忘记保存"的状态；SaveCommand 只是给用户一个显式的确认动作。
    /// 持久化位置就是 AppServices.Config.FilePath（便携模式在程序目录，否则在 %LOCALAPPDATA%）。
    /// </summary>
    public sealed class SettingsViewModel : ObservableObject
    {
        private const int MinSampleIntervalMs = 200;
        private const int MaxSampleIntervalMs = 10000;

        private string _selectedTheme = ThemeMode.System;
        private bool _readOnlyMode;
        private bool _confirmDangerousActions = true;
        private int _sampleIntervalMs = 1000;
        private string _statusMessage = string.Empty;

        private string _dataDirectoryText = "—";
        private string _modeText = "—";
        private string _permissionText = "—";
        private string _configFilePath = "—";
        private string _logFilePath = "—";
        private string _versionText = "—";
        private int _protectedPathCount;

        // ---------------- AI 助手段 ----------------
        private const string DefaultAiBaseUrl = "https://api.deepseek.com";
        private const int MinApiKeyLength = 20;

        private bool _aiEnabled;
        private string _aiKeyInput = string.Empty;
        private string _aiKeyMaskText = "（未配置）";
        private bool _hasAiKey;
        private string _aiStatusText = "尚未测试。测试连接会向 DeepSeek 发送一句固定的测试文本，不含你的任何数据。";

        /// <summary>
        /// 防止 setter 递归：确认框被取消时要回写 <c>IsChecked</c> 为 false，
        /// 那会再次进入 setter。没有这个闸门就会无限弹窗。
        /// </summary>
        private bool _suppressAiToggleConfirm;

        public SettingsViewModel()
        {
            _versionText = ResolveVersionText();

            // 这里**不能**调 SafeApplyTheme：本页打开时主题已经由 App 启动流程按配置应用好了。
            // 而 _selectedTheme 的字段初值是 System，此时 ReloadFromConfig 还没跑，
            // 提前应用会把用户保存的主题覆盖成"跟随系统"（表现为：一打开设置页主题就变了）。
            OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
            OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
            CopyConfigPathCommand = new RelayCommand(CopyConfigPath);
            SaveCommand = new RelayCommand(SaveNow);
            SaveAiKeyCommand = new RelayCommand(SaveAiKey);
            DeleteAiKeyCommand = new RelayCommand(DeleteAiKey);
            TestAiConnectionCommand = new RelayCommand(TestAiConnection);

            ReloadFromConfig();
            RefreshReadonlyInfo();
            RefreshAiState();
        }

        /// <summary>界面标题用的程序版本号，来自 AssemblyInfo（Directory.Build.props 的 Version）。</summary>
        public string VersionText => _versionText;

        public string SelectedTheme
        {
            get => _selectedTheme;
            set
            {
                var next = Normalize(value);
                if (!SetProperty(ref _selectedTheme, next)) return;

                OnPropertyChanged(nameof(IsDarkTheme));
                SafeApplyTheme(next);

                var config = AppServices.Config;
                if (config != null)
                {
                    config.Current.Theme = next;
                    config.Save();
                }
                StatusMessage = "主题已切换为「" + DescribeTheme(next) + "」";
            }
        }

        /// <summary>只读便捷属性（不含"跟随系统"语义），供 ToggleButton 之类的控件使用。</summary>
        public bool IsDarkTheme
        {
            get
            {
                var manager = ThemeManager.Current;
                return manager != null && manager.AppliedTheme != ThemeMode.Light;
            }
        }

        public bool ReadOnlyMode
        {
            get => _readOnlyMode;
            set
            {
                if (!SetProperty(ref _readOnlyMode, value)) return;
                SaveSetting("ReadOnlyMode", value);
                StatusMessage = value
                    ? "已开启只读审计模式：所有写操作被禁用"
                    : "已关闭只读审计模式：写操作恢复可用";
            }
        }

        public bool ConfirmDangerousActions
        {
            get => _confirmDangerousActions;
            set
            {
                if (!SetProperty(ref _confirmDangerousActions, value)) return;
                SaveSetting("ConfirmDangerousActions", value);
                StatusMessage = value ? "危险操作前会要求确认" : "已关闭危险操作确认（不建议）";
            }
        }

        public int SampleIntervalMs
        {
            get => _sampleIntervalMs;
            set
            {
                var clamped = Clamp(value);
                if (!SetProperty(ref _sampleIntervalMs, clamped)) return;

                OnPropertyChanged(nameof(SampleIntervalText));
                SaveSetting("SampleIntervalMs", clamped);
                StatusMessage = "采样间隔已设为 " + clamped + " 毫秒（最小 " + MinSampleIntervalMs + "，最大 " + MaxSampleIntervalMs + "）";
            }
        }

        public string SampleIntervalText => _sampleIntervalMs + " 毫秒";

        public ObservableCollection<string> ProtectedPaths { get; } = new ObservableCollection<string>();

        public int ProtectedPathCount
        {
            get => _protectedPathCount;
            private set
            {
                if (!SetProperty(ref _protectedPathCount, value)) return;
                OnPropertyChanged(nameof(HasCustomProtectedPaths));
                OnPropertyChanged(nameof(ProtectedPathsHint));
            }
        }

        public bool HasCustomProtectedPaths => _protectedPathCount > 0;

        public string ProtectedPathsHint => _protectedPathCount > 0
            ? "共 " + _protectedPathCount + " 个自定义保护目录，写操作一律拒绝"
            : "未添加自定义保护目录";

        public string DataDirectoryText
        {
            get => _dataDirectoryText;
            private set => SetProperty(ref _dataDirectoryText, value);
        }

        public string ModeText
        {
            get => _modeText;
            private set => SetProperty(ref _modeText, value);
        }

        public string PermissionText
        {
            get => _permissionText;
            private set => SetProperty(ref _permissionText, value);
        }

        public string ConfigFilePath
        {
            get => _configFilePath;
            private set => SetProperty(ref _configFilePath, value);
        }

        public string LogFilePath
        {
            get => _logFilePath;
            private set => SetProperty(ref _logFilePath, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        public RelayCommand OpenDataFolderCommand { get; }

        public RelayCommand OpenLogFolderCommand { get; }

        public RelayCommand CopyConfigPathCommand { get; }

        public RelayCommand SaveCommand { get; }

        /// <summary>页面每次显示时调用：刷新数据目录、权限、日志路径等只读信息。</summary>
        public void RefreshReadonlyInfo()
        {
            var data = AppServices.Data;
            var config = AppServices.Config;

            DataDirectoryText = data == null ? "—" : data.Directory;
            ModeText = string.IsNullOrEmpty(AppServices.ModeText) ? "—" : AppServices.ModeText;
            PermissionText = string.IsNullOrEmpty(AppServices.PermissionText) ? "—" : AppServices.PermissionText;
            ConfigFilePath = config == null ? "—" : config.FilePath;

            var logFile = Log.CurrentFile;
            LogFilePath = string.IsNullOrEmpty(logFile)
                ? (data == null ? "—" : System.IO.Path.Combine(data.LogDirectory, "winkit-*.log"))
                : logFile!;

            ProtectedPaths.Clear();
            if (config != null)
            {
                var paths = config.Current.ProtectedPaths;
                if (paths != null)
                {
                    foreach (var path in paths)
                    {
                        if (!string.IsNullOrWhiteSpace(path)) ProtectedPaths.Add(path);
                    }
                }
            }
            ProtectedPathCount = ProtectedPaths.Count;
        }

        private void ReloadFromConfig()
        {
            var config = AppServices.Config;
            if (config == null) return;

            var current = config.Current;
            _selectedTheme = Normalize(current.Theme);
            _readOnlyMode = current.ReadOnlyMode;
            _confirmDangerousActions = current.ConfirmDangerousActions;
            _sampleIntervalMs = Clamp(current.SampleIntervalMs);
            _aiEnabled = current.AiEnabled;

            OnPropertyChanged(nameof(SelectedTheme));
            OnPropertyChanged(nameof(IsDarkTheme));
            OnPropertyChanged(nameof(ReadOnlyMode));
            OnPropertyChanged(nameof(ConfirmDangerousActions));
            OnPropertyChanged(nameof(SampleIntervalMs));
            OnPropertyChanged(nameof(SampleIntervalText));
            OnPropertyChanged(nameof(AiEnabled));

            var note = config.LastLoadNote;
            StatusMessage = string.IsNullOrEmpty(note) ? "设置已就绪，改动即时保存" : note!;
        }

        private void SaveSetting(string propertyName, object value)
        {
            var config = AppServices.Config;
            if (config == null)
            {
                StatusMessage = "配置不可用，改动未保存";
                return;
            }

            try
            {
                config.Save();
                Log.Info("设置已更新：" + propertyName + " = " + value);
            }
            catch (Exception ex)
            {
                StatusMessage = "保存失败：" + ex.Message;
                Log.Error("保存设置失败：" + propertyName, ex);
            }
        }

        private void SaveNow()
        {
            var config = AppServices.Config;
            if (config == null)
            {
                StatusMessage = "配置不可用，无法保存";
                return;
            }

            try
            {
                config.Save();
                StatusMessage = "已保存到 " + config.FilePath;
                Log.Info("用户手动保存设置：" + config.FilePath);
            }
            catch (Exception ex)
            {
                StatusMessage = "保存失败：" + ex.Message;
                Log.Error("手动保存设置失败：" + ex.Message, ex);
            }
        }

        private void OpenDataFolder()
        {
            var data = AppServices.Data;
            OpenInExplorer(data == null ? null : data.Directory);
        }

        private void OpenLogFolder()
        {
            var data = AppServices.Data;
            OpenInExplorer(data == null ? null : data.LogDirectory);
        }

        private void OpenInExplorer(string? directory)
        {
            if (string.IsNullOrEmpty(directory))
            {
                StatusMessage = "路径不可用";
                return;
            }

            try
            {
                Process.Start("explorer.exe", "\"" + directory + "\"");
                StatusMessage = "已打开：" + directory;
            }
            catch (Exception ex)
            {
                StatusMessage = "无法打开：" + ex.Message;
                Log.Warn("打开目录失败：" + directory + " → " + ex.Message);
            }
        }

        private void CopyConfigPath()
        {
            var config = AppServices.Config;
            if (config == null) return;

            try
            {
                Clipboard.SetDataObject(config.FilePath, true);
                StatusMessage = "已复制配置路径：" + config.FilePath;
            }
            catch (Exception ex)
            {
                StatusMessage = "复制失败：" + ex.Message;
                Log.Warn("复制配置路径失败：" + ex.Message);
            }
        }

        // ==================== AI 助手段 ====================
        //
        // 本段的全部职责是守住"默认不联网、可选联网"这条承诺的用户可见部分：
        //   * 默认关闭（AppConfig.AiEnabled 初值为 false）；
        //   * 首次开启时用确认框把"什么情况下才会联网"讲清楚，默认按钮是"取消"；
        //   * key 用 DPAPI 加密存在独立文件里，界面只显示掩码，绝不回显完整 key；
        //   * "测试连接"虽然不经过预览窗口（它不含任何用户数据），但界面上写清楚它发什么。

        /// <summary>
        /// AI 助手总开关，**默认 false**。
        /// 从 false 翻到 true 的那一刻要弹一次确认：这是用户第一次把"可能联网"这件事授权出去。
        /// </summary>
        public bool AiEnabled
        {
            get => _aiEnabled;
            set
            {
                if (_aiEnabled == value) return;

                if (value && !_suppressAiToggleConfirm)
                {
                    var confirmed = MessageBox.Show(
                        "启用后，只有你主动点击『用 AI 解释』并确认发送时才会联网；发送前会显示将要发送的确切内容。"
                        + "WinKit 不会在后台联网，也不会上传任何文件内容。确定启用吗？",
                        "启用 AI 助手",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Warning,
                        MessageBoxResult.Cancel) == MessageBoxResult.OK;

                    if (!confirmed)
                    {
                        // 用户拒绝：保持关闭。这里必须走抑制闸门，
                        // 否则回写 OnPropertyChanged 触发的绑定刷新会再次进入本 setter。
                        _suppressAiToggleConfirm = true;
                        try
                        {
                            _aiEnabled = false;
                            OnPropertyChanged(nameof(AiEnabled));
                        }
                        finally
                        {
                            _suppressAiToggleConfirm = false;
                        }
                        StatusMessage = "未启用 AI 助手，程序保持完全离线。";
                        return;
                    }
                }

                _aiEnabled = value;

                var config = AppServices.Config;
                if (config != null)
                {
                    config.Current.AiEnabled = value;
                    try
                    {
                        config.Save();
                    }
                    catch (Exception ex)
                    {
                        StatusMessage = "保存失败：" + ex.Message;
                        Log.Error("保存 AI 开关失败", ex);
                    }
                }

                OnPropertyChanged(nameof(AiEnabled));
                RefreshAiState();
                StatusMessage = value
                    ? "已启用 AI 助手：只有你点『用 AI 解释』并确认发送时才会联网。"
                    : "已关闭 AI 助手：程序不会发起任何网络请求。";
            }
        }

        /// <summary>用户正在输入的 key。保存成功后清空，界面上不保留明文。</summary>
        public string AiKeyInput
        {
            get => _aiKeyInput;
            set => SetProperty(ref _aiKeyInput, value);
        }

        /// <summary>已保存 key 的掩码展示（绝不显示完整 key）。</summary>
        public string AiKeyMaskText
        {
            get => _aiKeyMaskText;
            private set => SetProperty(ref _aiKeyMaskText, value);
        }

        public bool HasAiKey
        {
            get => _hasAiKey;
            private set => SetProperty(ref _hasAiKey, value);
        }

        /// <summary>DeepSeek 服务地址。留空即使用默认地址（本版本为只读展示）。</summary>
        public string AiBaseUrl => DefaultAiBaseUrl;

        public string AiStatusText
        {
            get => _aiStatusText;
            private set => SetProperty(ref _aiStatusText, value);
        }

        /// <summary>加密密钥文件所在路径，展示用（用户要备份/删除时知道去哪找）。</summary>
        public string AiKeyStoragePath => AppServices.AiKeys == null ? "—" : AppServices.AiKeys.FilePath;

        public RelayCommand SaveAiKeyCommand { get; }

        public RelayCommand DeleteAiKeyCommand { get; }

        public RelayCommand TestAiConnectionCommand { get; }

        /// <summary>刷新掩码、路径与可用状态。构造函数、保存/删除 key 之后调用。</summary>
        private void RefreshAiState()
        {
            var store = AppServices.AiKeys;

            if (store == null)
            {
                HasAiKey = false;
                AiKeyMaskText = "（密钥存储不可用）";
            }
            else if (!store.HasKey)
            {
                HasAiKey = false;
                AiKeyMaskText = "（未配置）";
            }
            else
            {
                // 解密一次拿到掩码素材。解不开（换用户/换机器）也要如实说出来，
                // 否则界面显示"已保存"、点发送却必然失败，用户会以为程序有 bug。
                var key = store.Load();
                if (string.IsNullOrWhiteSpace(key))
                {
                    HasAiKey = false;
                    AiKeyMaskText = "（已保存但无法解密：可能换了 Windows 用户或文件损坏，请重新填写）";
                }
                else
                {
                    HasAiKey = true;
                    AiKeyMaskText = ApiKeyStore.Mask(key) + "（已加密保存在本机）";
                }
            }

            OnPropertyChanged(nameof(AiKeyStoragePath));
            OnPropertyChanged(nameof(AiBaseUrl));
        }

        private void SaveAiKey()
        {
            var input = AiKeyInput == null ? string.Empty : AiKeyInput.Trim();

            if (input.Length == 0)
            {
                AiStatusText = "请先粘贴 DeepSeek API Key。";
                return;
            }

            if (AppServices.IsReadOnly)
            {
                AiStatusText = "当前处于只读审计模式，禁止写入密钥。";
                return;
            }

            var store = AppServices.AiKeys;
            if (store == null)
            {
                AiStatusText = "密钥存储不可用，无法保存。";
                return;
            }

            // 形如提示：不阻止保存（用户可能用的是代理服务商的自定义 key），但要说清楚。
            // **绝不把输入内容本身写进状态或日志。**
            var warning = string.Empty;
            if (input.Length < MinApiKeyLength)
            {
                warning = "注意：这段内容只有 " + input.Length + " 个字符，通常 API Key 会更长。";
            }
            else if (!input.StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
            {
                warning = "注意：DeepSeek 的 Key 一般以 sk- 开头，这段内容可能不是 API Key。";
            }

            try
            {
                store.Save(input);
                AiKeyInput = string.Empty;
                RefreshAiState();
                AiStatusText = warning.Length > 0
                    ? warning + "已按你的输入保存（DPAPI 加密，仅当前 Windows 用户可解密）。"
                    : "已保存（DPAPI 加密，仅当前 Windows 用户可解密）。日志只记录长度，不记录内容。";
                StatusMessage = "DeepSeek API Key 已保存到本机。";
            }
            catch (Exception ex)
            {
                AiStatusText = "保存失败：" + ex.Message;
                Log.Error("保存 API Key 失败", ex);
            }
        }

        private void DeleteAiKey()
        {
            if (!HasAiKey && AppServices.AiKeys != null && !AppServices.AiKeys.HasKey)
            {
                AiStatusText = "本机没有已保存的 API Key。";
                return;
            }

            var confirmed = MessageBox.Show(
                "确定删除本机保存的 DeepSeek API Key 吗？删除后 AI 功能将不可用，直到你重新填写。",
                "删除 API Key",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel) == MessageBoxResult.OK;

            if (!confirmed) return;

            if (AppServices.IsReadOnly)
            {
                AiStatusText = "当前处于只读审计模式，禁止删除密钥。";
                return;
            }

            var store = AppServices.AiKeys;
            if (store == null)
            {
                AiStatusText = "密钥存储不可用。";
                return;
            }

            store.Delete();
            RefreshAiState();
            AiStatusText = "已删除本机保存的 API Key。";
            StatusMessage = "已删除本机保存的 DeepSeek API Key。";
        }

        /// <summary>
        /// 测试连接。**不经过预览窗口** —— 它不包含任何用户数据（一句写死的测试文本）。
        /// 但界面上必须写明这一点，否则"测试"就成了绕过预览窗口的后门。
        /// </summary>
        private void TestAiConnection()
        {
            if (!AppServices.IsAiReady)
            {
                AiStatusText = AppServices.AiUnavailableReason;
                return;
            }

            var store = AppServices.AiKeys;
            if (store == null)
            {
                AiStatusText = "密钥存储不可用。";
                return;
            }

            var key = store.Load();
            if (string.IsNullOrWhiteSpace(key))
            {
                AiStatusText = "无法读取已保存的 API Key，请重新填写。";
                return;
            }

            AiStatusText = "正在测试连接（发送一句固定测试文本）…";

            var model = AppServices.Config == null ? PromptBuilder.DefaultModel : AppServices.Config.Current.AiModel;
            if (string.IsNullOrWhiteSpace(model)) model = PromptBuilder.DefaultModel;

            var request = new AiRequest
            {
                Model = model,
                Temperature = 0,
                MaxTokens = 16,
                Stream = false,
                Disclosure = "测试连接：只发送一句固定的测试文本（\"测试\"），不含你的任何数据。",
                Messages = new System.Collections.Generic.List<AiMessage>
                {
                    new AiMessage(AiRole.System, "你是测试助手，只回复四个字：连接正常"),
                    new AiMessage(AiRole.User, "测试"),
                },
            };

            // 后台线程发请求：网络等待绝不能占用 UI 线程
            Task.Run(() =>
            {
                DeepSeekClient? client = null;
                string message;
                try
                {
                    client = new DeepSeekClient(key == null ? string.Empty : key, null);
                    var reply = client.CompleteAsync(request, System.Threading.CancellationToken.None, TimeSpan.FromSeconds(20))
                        .GetAwaiter().GetResult();

                    var trimmed = reply == null ? string.Empty : reply.Trim();
                    if (trimmed.Length > 40) trimmed = trimmed.Substring(0, 40) + "…";
                    message = trimmed.Length == 0
                        ? "连接正常（服务返回了空内容）"
                        : "连接正常，模型返回：" + trimmed;
                }
                catch (AiException ex)
                {
                    message = ex.UserMessage;
                    Log.Warn("AI 测试连接失败：" + ex.Kind);
                }
                catch (Exception ex)
                {
                    message = "测试失败：" + ex.Message;
                    Log.Error("AI 测试连接异常", ex);
                }
                finally
                {
                    if (client != null) client.Dispose();
                }

                // 回到 UI 线程改绑定属性
                var dispatcher = Application.Current == null ? null : Application.Current.Dispatcher;
                if (dispatcher != null) dispatcher.BeginInvoke(new Action(() => AiStatusText = message));
                else AiStatusText = message;
            });
        }

        private static int Clamp(int value)
        {
            if (value < MinSampleIntervalMs) return MinSampleIntervalMs;
            if (value > MaxSampleIntervalMs) return MaxSampleIntervalMs;
            return value;
        }

        private static string Normalize(string? theme)
        {
            if (string.Equals(theme, ThemeMode.Light, StringComparison.OrdinalIgnoreCase)) return ThemeMode.Light;
            if (string.Equals(theme, ThemeMode.System, StringComparison.OrdinalIgnoreCase)) return ThemeMode.System;
            return ThemeMode.Dark;
        }

        private static string DescribeTheme(string theme)
        {
            if (theme == ThemeMode.Light) return "浅色";
            if (theme == ThemeMode.System) return "跟随系统";
            return "深色";
        }

        /// <summary>取程序集版本（由 Directory.Build.props 的 Version 决定），失败时退化为"未知"。</summary>
        private static string ResolveVersionText()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                if (info != null && !string.IsNullOrWhiteSpace(info.InformationalVersion))
                {
                    return info.InformationalVersion;
                }

                var version = assembly.GetName().Version;
                return version == null ? "未知" : version.ToString(3);
            }
            catch (Exception)
            {
                return "未知";
            }
        }

        private static void SafeApplyTheme(string theme)
        {
            var manager = ThemeManager.Current;
            if (manager == null) return;

            try
            {
                manager.Apply(theme);
            }
            catch (Exception ex)
            {
                Log.Warn("应用主题失败：" + ex.Message);
            }
        }
    }
}
