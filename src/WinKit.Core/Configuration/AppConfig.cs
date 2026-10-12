using System.Collections.Generic;
using System.Runtime.Serialization;

namespace WinKit.Core.Configuration
{
    public static class ThemeMode
    {
        public const string Dark = "Dark";
        public const string Light = "Light";
        public const string System = "System";
    }

    /// <summary>
    /// 应用配置。字段使用 DataMember 显式命名，保证 JSON 里的键名稳定（便于将来做 schema 迁移）。
    /// 未识别的字段在反序列化时被忽略；未知字段不会丢失，因为保存前会先与旧文件合并。
    /// </summary>
    [DataContract]
    public sealed class AppConfig
    {
        public const int CurrentSchemaVersion = 1;

        [DataMember(Name = "schemaVersion", Order = 0)]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [DataMember(Name = "theme", Order = 1)]
        public string Theme { get; set; } = ThemeMode.Dark;

        [DataMember(Name = "sampleIntervalMs", Order = 2)]
        public int SampleIntervalMs { get; set; } = 1000;

        [DataMember(Name = "confirmDangerousActions", Order = 3)]
        public bool ConfirmDangerousActions { get; set; } = true;

        /// <summary>只读审计模式：开启后禁用一切写操作，只允许扫描与导出。</summary>
        [DataMember(Name = "readOnlyMode", Order = 4)]
        public bool ReadOnlyMode { get; set; }

        [DataMember(Name = "protectedPaths", Order = 5)]
        public List<string> ProtectedPaths { get; set; } = new List<string>();

        [DataMember(Name = "windowWidth", Order = 6)]
        public double WindowWidth { get; set; } = 1180d;

        [DataMember(Name = "windowHeight", Order = 7)]
        public double WindowHeight { get; set; } = 720d;

        [DataMember(Name = "windowMaximized", Order = 8)]
        public bool WindowMaximized { get; set; }

        /// <summary>
        /// AI 助手总开关。**默认 false** —— 这是"默认不联网"承诺的开关本体：
        /// 只有用户显式打开它，程序才可能在用户点"发送"时发起网络请求。
        /// 任何后台流程（启动、扫描、体检）都不读这个开关去做网络调用。
        /// </summary>
        [DataMember(Name = "aiEnabled", Order = 9)]
        public bool AiEnabled { get; set; }

        [DataMember(Name = "aiModel", Order = 10)]
        public string AiModel { get; set; } = "deepseek-chat";
    }
}
