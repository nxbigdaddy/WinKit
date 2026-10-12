using System;
using System.IO;

namespace WinKit.Core.Tests
{
    /// <summary>测试用临时目录，Dispose 时递归删除。</summary>
    internal sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "winkit-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public string Dir(string name)
        {
            var d = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(d);
            return d;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch (Exception)
            {
                // 临时目录清理失败不影响测试结论
            }
        }
    }
}
