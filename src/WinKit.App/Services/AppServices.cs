using System;
using System.IO;
using WinKit.Core.Configuration;
using WinKit.Core.Diagnostics;
using WinKit.Core.Journal;
using WinKit.Core.Rules;
using WinKit.Platform.Ai;
using WinKit.Platform.Cleanup;
using WinKit.Platform.Metrics;
using WinKit.Platform.Security;
using WinKit.Platform.Undo;
using WinKit.Platform.Wmi;

namespace WinKit.App.Services
{
    /// <summary>
    /// 极简服务定位器。M0 刻意不引 DI 容器：依赖关系一共就这几个，
    /// 引容器带来的复杂度大于收益，而且会引入第三方 DLL。
    /// </summary>
    public static class AppServices
    {
        public static DataLocation Data { get; private set; } = null!;
        public static ConfigStore Config { get; private set; } = null!;
        public static JournalStore Journal { get; private set; } = null!;
        public static UndoEngine Undo { get; private set; } = null!;
        public static PathGuard Guard { get; private set; } = null!;
        public static SystemInfoProvider SystemInfo { get; private set; } = null!;

        /// <summary>
        /// 隔离区。清理掉的项先搬到这里，因此撤销就是一次文件移动 ——
        /// 这也是"可撤销"能做成 100% 可靠的原因。撤销复用 MoveFileUndoHandler，
        /// 不需要额外的撤销处理器。
        /// </summary>
        public static QuarantineStore Quarantine { get; private set; } = null!;

        /// <summary>
        /// 磁盘吞吐采样器。全应用共用一个实例：性能计数器的首次创建要 10~50ms 且持有系统句柄，
        /// 不该每个页面各建一份。
        /// </summary>
        public static DiskIoSampler DiskIo { get; private set; } = null!;

        /// <summary>DeepSeek API Key 的本机加密存储（DPAPI，CurrentUser）。</summary>
        public static ApiKeyStore AiKeys { get; private set; } = null!;

        /// <summary>
        /// AI 是否已启用且可用。**这是唯一允许发起网络请求的判定入口** ——
        /// 界面上的 AI 按钮必须先问这一句，任何后台流程都不许碰它。
        /// </summary>
        public static bool IsAiReady
        {
            get
            {
                if (Config == null) return false;
                if (!Config.Current.AiEnabled) return false;
                return AiKeys != null && AiKeys.HasKey;
            }
        }

        /// <summary>AI 不可用时给用户的原因（直接可显示）。</summary>
        public static string AiUnavailableReason
        {
            get
            {
                if (Config == null) return "配置尚未初始化";
                if (!Config.Current.AiEnabled) return "AI 助手未启用（默认关闭，可在「设置 → AI 助手」打开）";
                if (AiKeys == null || !AiKeys.HasKey) return "尚未填写 DeepSeek API Key";
                return string.Empty;
            }
        }

        public static bool IsElevated => Elevation.IsElevated;

        public static string PermissionText => IsElevated ? "管理员" : "标准权限";

        public static bool IsInitialized { get; private set; }

        public static void Initialize(string programDirectory)
        {
            if (IsInitialized) return;

            var fallback = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Data = PortablePaths.Resolve(programDirectory, fallback);
            Data.EnsureCreated();

            Log.Initialize(Data.LogDirectory, LogLevel.Info);
            Log.Info("WinKit 启动：" + (IsElevated ? "管理员权限" : "标准权限")
                + "，数据目录 " + Data.Directory + (Data.IsPortable ? "（便携模式）" : "（回退模式）"));

            Config = new ConfigStore(Data.ConfigFile);
            Config.Load();

            Journal = new JournalStore(Data.JournalFile);
            Undo = new UndoEngine(Journal);
            Undo.Register(new MoveFileUndoHandler());

            Guard = PathGuard.CreateDefault();
            foreach (var path in Config.Current.ProtectedPaths)
            {
                Guard.Protect(path, "用户自定义保护目录");
            }

            SystemInfo = new SystemInfoProvider();

            Quarantine = new QuarantineStore(Path.Combine(Data.Directory, "quarantine"));
            Quarantine.EnsureCreated();

            DiskIo = new DiskIoSampler();

            AiKeys = new ApiKeyStore(Path.Combine(Data.Directory, "secrets"));

            IsInitialized = true;
        }

        /// <summary>只读模式下禁止一切写操作。所有写操作入口都必须先问这一句。</summary>
        public static bool IsReadOnly => Config != null && Config.Current.ReadOnlyMode;

        public static string DataDirectoryText => Data == null ? "—" : Data.Directory;

        public static string ModeText =>
            Data == null ? "—" : (Data.IsPortable ? "便携模式" : "回退模式（%LOCALAPPDATA%）");
    }
}
