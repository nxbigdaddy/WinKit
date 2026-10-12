using System.Collections.Generic;
using System.IO;
using System.Text;
using WinKit.Core.Configuration;
using Xunit;

namespace WinKit.Core.Tests
{
    public class ConfigStoreTests
    {
        [Fact]
        public void Load_creates_default_config_when_file_missing()
        {
            using var tmp = new TempDir();
            var store = new ConfigStore(tmp.File("config.json"));

            var config = store.Load();

            Assert.Equal(AppConfig.CurrentSchemaVersion, config.SchemaVersion);
            Assert.Equal(ThemeMode.Dark, config.Theme);
            Assert.True(File.Exists(store.FilePath));
            Assert.Contains("默认配置", store.LastLoadNote);
        }

        [Fact]
        public void Save_then_Load_roundtrips_all_fields()
        {
            using var tmp = new TempDir();
            var path = tmp.File("config.json");

            var store = new ConfigStore(path);
            var config = store.Load();
            config.Theme = ThemeMode.Light;
            config.SampleIntervalMs = 500;
            config.ReadOnlyMode = true;
            config.ProtectedPaths = new List<string> { @"D:\重要资料", @"E:\备份" };
            store.Save(config);

            var reloaded = new ConfigStore(path).Load();

            Assert.Equal(ThemeMode.Light, reloaded.Theme);
            Assert.Equal(500, reloaded.SampleIntervalMs);
            Assert.True(reloaded.ReadOnlyMode);
            Assert.Equal(2, reloaded.ProtectedPaths.Count);
            Assert.Equal(@"D:\重要资料", reloaded.ProtectedPaths[0]);
        }

        [Fact]
        public void Load_backs_up_corrupted_file_and_falls_back_to_defaults()
        {
            using var tmp = new TempDir();
            var path = tmp.File("config.json");
            File.WriteAllText(path, "{ 这不是合法的 JSON ", new UTF8Encoding(false));

            var store = new ConfigStore(path);
            var config = store.Load();

            Assert.Equal(ThemeMode.Dark, config.Theme);
            Assert.Contains("损坏", store.LastLoadNote);
            Assert.NotEmpty(Directory.GetFiles(tmp.Path, "config.json.broken-*"));
        }

        [Fact]
        public void Save_leaves_no_temporary_file_behind()
        {
            using var tmp = new TempDir();
            var store = new ConfigStore(tmp.File("config.json"));
            store.Save(store.Load());

            Assert.Empty(Directory.GetFiles(tmp.Path, "*.tmp"));
        }

        [Fact]
        public void Load_clamps_absurd_sample_interval()
        {
            using var tmp = new TempDir();
            var path = tmp.File("config.json");
            File.WriteAllText(path, "{\"schemaVersion\":1,\"theme\":\"Dark\",\"sampleIntervalMs\":5}", new UTF8Encoding(false));

            var config = new ConfigStore(path).Load();

            Assert.Equal(200, config.SampleIntervalMs);
        }

        [Fact]
        public void Save_raises_changed_event()
        {
            using var tmp = new TempDir();
            var store = new ConfigStore(tmp.File("config.json"));
            AppConfig? observed = null;
            store.Changed += c => observed = c;

            store.Save(new AppConfig { Theme = ThemeMode.Light });

            Assert.NotNull(observed);
            Assert.Equal(ThemeMode.Light, observed!.Theme);
        }
    }
}
