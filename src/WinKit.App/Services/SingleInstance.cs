using System;
using System.IO.Pipes;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WinKit.Core.Diagnostics;

namespace WinKit.App.Services
{
    /// <summary>
    /// 单实例控制。两件事：
    /// 1) 互斥体（Mutex）保证只有一个进程在跑；
    /// 2) 命名管道让第二个进程能把已有窗口叫到前台，而不是直接静默退出。
    ///
    /// 铁律：这里任何失败都只是"降级"（变成多实例 / 无法激活），绝不能影响启动。
    /// </summary>
    public static class SingleInstance
    {
        /// <summary>Local\ 前缀 = 每个登录会话一个实例；不同用户各自开各自的 WinKit。</summary>
        private const string MutexName = @"Local\WinKit.SingleInstance.6F3C1B0E4A2D47E8";

        private const string PipeName = "WinKit.Activate";

        private const int ActivateConnectTimeoutMs = 1000;

        private static readonly object Gate = new object();

        private static Mutex? _mutex;
        private static bool _ownsMutex;
        private static NamedPipeServerStream? _server;
        private static volatile bool _listening;

        /// <summary>抢单实例锁。返回 false 表示已有实例在运行（调用方应该通知它并退出）。</summary>
        public static bool TryAcquire()
        {
            try
            {
                var mutex = new Mutex(true, MutexName, out var createdNew);
                if (!createdNew)
                {
                    // 已有实例持有同名互斥体：本次是"第二个实例"
                    try { mutex.Dispose(); } catch (Exception) { }
                    return false;
                }

                lock (Gate)
                {
                    _mutex = mutex;
                    _ownsMutex = true;
                }
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                // 互斥体属于别的用户/权限不足：宁可多开，也不要因为锁失败打不开程序
                Log.Warn("单实例互斥体不可用，按多实例继续运行：" + ex.Message);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("创建单实例互斥体失败，按多实例继续运行：" + ex.Message);
                return true;
            }
        }

        /// <summary>后台线程监听命名管道；收到任意字节就把 onActivate 切回 UI 线程执行。</summary>
        public static void StartListener(Action onActivate)
        {
            if (onActivate == null) return;

            lock (Gate)
            {
                if (_listening) return;
                _listening = true;
            }

            var thread = new Thread(() => ListenLoop(onActivate))
            {
                IsBackground = true,
                Name = "WinKit.ActivateListener",
            };

            try
            {
                thread.Start();
            }
            catch (Exception ex)
            {
                Log.Warn("启动激活监听线程失败：" + ex.Message);
                lock (Gate) { _listening = false; }
            }
        }

        /// <summary>第二个实例调用：连一次管道并写一个字节。失败（含 1 秒超时）静默返回。</summary>
        public static void NotifyExistingInstance()
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.None))
                {
                    client.Connect(ActivateConnectTimeoutMs);
                    client.WriteByte(0x01);
                    client.Flush();
                }
                Log.Info("已请求已有实例显示窗口");
            }
            catch (Exception ex)
            {
                Log.Warn("通知已有实例失败（忽略，不影响本次退出）：" + ex.Message);
            }
        }

        /// <summary>释放管道与互斥体。必须在创建互斥体的线程（UI 线程）上调用。</summary>
        public static void Release()
        {
            _listening = false;

            NamedPipeServerStream? server;
            lock (Gate)
            {
                server = _server;
                _server = null;
            }
            if (server != null)
            {
                // Dispose 会让阻塞中的 WaitForConnection 抛异常，监听循环据此退出
                try { server.Dispose(); } catch (Exception) { }
            }

            Mutex? mutex;
            bool owned;
            lock (Gate)
            {
                mutex = _mutex;
                owned = _ownsMutex;
                _mutex = null;
                _ownsMutex = false;
            }

            if (mutex != null)
            {
                try
                {
                    if (owned) mutex.ReleaseMutex();
                }
                catch (Exception ex)
                {
                    Log.Warn("释放单实例互斥体失败：" + ex.Message);
                }
                try { mutex.Dispose(); } catch (Exception) { }
            }
        }

        private static void ListenLoop(Action onActivate)
        {
            while (_listening)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.None);

                    lock (Gate) { _server = server; }

                    server.WaitForConnection();

                    // 内容不重要：收到任意字节就视为"请把窗口显示出来"
                    var buffer = new byte[8];
                    server.Read(buffer, 0, buffer.Length);
                    Log.Info("收到来自其他实例的激活请求");
                    Dispatch(onActivate);
                }
                catch (Exception ex)
                {
                    if (_listening)
                    {
                        Log.Warn("激活管道监听异常：" + ex.Message);
                        // 避免异常导致空转烧 CPU
                        Thread.Sleep(200);
                    }
                }
                finally
                {
                    lock (Gate) { _server = null; }
                    if (server != null)
                    {
                        try { server.Dispose(); } catch (Exception) { }
                    }
                }
            }
        }

        private static void Dispatch(Action onActivate)
        {
            try
            {
                var application = Application.Current;
                if (application == null)
                {
                    onActivate();
                    return;
                }

                var dispatcher = application.Dispatcher;
                if (dispatcher == null || dispatcher.CheckAccess()) onActivate();
                else dispatcher.BeginInvoke(DispatcherPriority.Normal, onActivate);
            }
            catch (Exception ex)
            {
                Log.Warn("切回 UI 线程执行激活回调失败：" + ex.Message);
            }
        }
    }
}
