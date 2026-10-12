using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WinKit.Core.Diagnostics;
using WinKit.Core.Tasks;

namespace WinKit.App.Services
{
    /// <summary>
    /// 后台任务执行器。铁律：UI 线程只做渲染，任何文件遍历/注册表/WMI 都必须走这里。
    /// 进度与完成回调都会回到 UI 线程（Progress&lt;T&gt; 捕获创建时的同步上下文）。
    /// </summary>
    public static class TaskRunner
    {
        public static CancellationTokenSource Run(
            string taskId,
            Action<TaskContext> work,
            Action<TaskProgress>? onProgress = null,
            Action<Exception?>? onCompleted = null)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));

            var cts = new CancellationTokenSource();
            // 必须在 UI 线程构造，才能把回调封送到 UI 线程
            var progress = onProgress == null ? null : new Progress<TaskProgress>(onProgress);

            Task.Factory.StartNew(() =>
            {
                Exception? error = null;
                try
                {
                    work(new TaskContext(taskId, cts.Token, progress));
                }
                catch (OperationCanceledException)
                {
                    Log.Debug("任务已取消：" + taskId);
                }
                catch (Exception ex)
                {
                    error = ex;
                    Log.Error("任务执行失败：" + taskId + " → " + ex.Message, ex);
                }
                finally
                {
                    if (onCompleted != null)
                    {
                        var dispatcher = Application.Current != null ? Application.Current.Dispatcher : null;
                        if (dispatcher != null) dispatcher.BeginInvoke(onCompleted, error);
                        else onCompleted(error);
                    }
                }
            }, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            return cts;
        }

        /// <summary>把一个纯计算挪到线程池，await 后继续在 UI 线程。用于一次性取数。</summary>
        public static Task<T> RunAsync<T>(Func<T> work)
        {
            return Task.Run(work);
        }
    }
}
