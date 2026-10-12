using System;
using System.Threading;
using WinKit.Core.Diagnostics;

namespace WinKit.Core.Tasks
{
    /// <summary>任务进度。Fraction 为 -1 表示不确定进度（转圈）。</summary>
    public readonly struct TaskProgress
    {
        public TaskProgress(double fraction, string? message = null)
        {
            Fraction = fraction;
            Message = message;
        }

        /// <summary>0.0 ~ 1.0；-1 表示不确定。</summary>
        public double Fraction { get; }

        public string? Message { get; }

        public bool IsIndeterminate => Fraction < 0;

        public static TaskProgress Indeterminate(string? message = null) => new TaskProgress(-1d, message);

        public static TaskProgress Completed(string? message = null) => new TaskProgress(1d, message);
    }

    /// <summary>
    /// 传给耗时任务的上下文：协作式取消 + 进度上报 + 日志。
    /// 所有扫描/清理类任务都通过它检查取消，保证点"取消"后 200ms 内停下来。
    /// </summary>
    public sealed class TaskContext
    {
        private readonly CancellationToken _token;
        private readonly IProgress<TaskProgress>? _progress;

        public TaskContext(string taskId, CancellationToken token, IProgress<TaskProgress>? progress = null)
        {
            TaskId = taskId ?? throw new ArgumentNullException(nameof(taskId));
            _token = token;
            _progress = progress;
        }

        public string TaskId { get; }

        public bool IsCancellationRequested => _token.IsCancellationRequested;

        public CancellationToken Token => _token;

        public void ThrowIfCancelled() => _token.ThrowIfCancellationRequested();

        public void Report(double fraction, string? message = null) => Report(new TaskProgress(fraction, message));

        public void Report(TaskProgress progress)
        {
            var p = _progress;
            if (p == null) return;
            try
            {
                p.Report(progress);
            }
            catch (Exception)
            {
                // 进度上报失败不应中断任务
            }
        }

        public void Log(LogLevel level, string message)
        {
            switch (level)
            {
                case LogLevel.Debug: Diagnostics.Log.Debug("[" + TaskId + "] " + message); break;
                case LogLevel.Warn: Diagnostics.Log.Warn("[" + TaskId + "] " + message); break;
                case LogLevel.Error: Diagnostics.Log.Error("[" + TaskId + "] " + message); break;
                default: Diagnostics.Log.Info("[" + TaskId + "] " + message); break;
            }
        }

        public void Info(string message) => Log(LogLevel.Info, message);
        public void Warn(string message) => Log(LogLevel.Warn, message);
        public void Error(string message) => Log(LogLevel.Error, message);
    }
}
