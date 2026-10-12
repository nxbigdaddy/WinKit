using System;
using System.IO;
using System.Text;
using WinKit.Core.Diagnostics;
using WinKit.Core.Serialization;

namespace WinKit.Core.Configuration
{
    /// <summary>
    /// 配置读写。两个硬要求：
    /// 1) 原子写入（先写临时文件再替换），避免掉电/崩溃把配置写坏；
    /// 2) 损坏时自动备份现场并回退默认值，绝不因为配置坏了导致程序起不来。
    /// </summary>
    public sealed class ConfigStore
    {
        private readonly string _path;
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public ConfigStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("配置路径不能为空", nameof(path));
            _path = path;
            Current = new AppConfig();
        }

        public string FilePath => _path;

        public AppConfig Current { get; private set; }

        /// <summary>上次加载时的异常说明（如"配置损坏，已备份"），供 UI 提示。</summary>
        public string? LastLoadNote { get; private set; }

        public event Action<AppConfig>? Changed;

        public AppConfig Load()
        {
            LastLoadNote = null;

            if (!File.Exists(_path))
            {
                Current = new AppConfig();
                TrySave(Current);
                LastLoadNote = "未找到配置文件，已创建默认配置";
                return Current;
            }

            string raw;
            try
            {
                raw = File.ReadAllText(_path, Utf8NoBom);
            }
            catch (Exception ex)
            {
                Log.Warn("读取配置失败，使用默认配置：" + ex.Message);
                Current = new AppConfig();
                LastLoadNote = "配置文件无法读取，已使用默认配置：" + ex.Message;
                return Current;
            }

            var loaded = Json.TryDeserialize<AppConfig>(raw);
            if (loaded == null)
            {
                var backup = BackupBrokenFile();
                Log.Warn("配置文件损坏，已备份到 " + backup);
                Current = new AppConfig();
                TrySave(Current);
                LastLoadNote = "配置文件损坏，已备份为 " + Path.GetFileName(backup) + " 并重建默认配置";
                return Current;
            }

            loaded = Migrate(loaded);
            loaded.ProtectedPaths ??= new System.Collections.Generic.List<string>();
            if (loaded.SampleIntervalMs < 200) loaded.SampleIntervalMs = 200;
            Current = loaded;
            return Current;
        }

        public void Save()
        {
            Save(Current);
        }

        public void Save(AppConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            config.SchemaVersion = AppConfig.CurrentSchemaVersion;
            Current = config;
            if (!TrySave(config))
            {
                LastLoadNote = "配置保存失败，请检查数据目录是否可写";
            }
            var handler = Changed;
            if (handler != null)
            {
                try { handler(config); } catch (Exception) { /* 订阅者异常不影响保存 */ }
            }
        }

        /// <summary>schema 迁移入口。当前只有 v1，保留结构以便将来平滑升级。</summary>
        private static AppConfig Migrate(AppConfig config)
        {
            if (config.SchemaVersion <= 0) config.SchemaVersion = 1;
            // 将来：if (config.SchemaVersion < 2) { ...; config.SchemaVersion = 2; }
            return config;
        }

        private bool TrySave(AppConfig config)
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(config), Utf8NoBom);

                if (File.Exists(_path))
                {
                    var backup = _path + ".bak";
                    File.Replace(tmp, _path, backup, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tmp, _path);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("保存配置失败：" + ex.Message, ex);
                return false;
            }
        }

        private string BackupBrokenFile()
        {
            var target = _path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try
            {
                File.Copy(_path, target, overwrite: true);
            }
            catch (Exception)
            {
                // 备份失败不影响主流程
            }
            return target;
        }
    }
}
