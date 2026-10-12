using System;
using System.Collections.Generic;
using System.IO;

namespace WinKit.Platform.Tests
{
    /// <summary>测试用临时目录，Dispose 时递归删除。</summary>
    internal sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "winkit-platform-test-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>
    /// 同步收集进度的 IProgress 实现。xunit 下 SynchronizationContext 可能为 null，
    /// 用 Progress&lt;T&gt; 会把回调投递到线程池，导致断言时进度还没到齐（潜在的偶发失败），
    /// 因此这里直接在调用线程上记录。
    /// </summary>
    internal sealed class RecordingProgress : IProgress<double>
    {
        private readonly System.Collections.Generic.List<double> _values =
            new System.Collections.Generic.List<double>();

        public IReadOnlyList<double> Values { get { return _values; } }

        public void Report(double value) { _values.Add(value); }
    }
}
