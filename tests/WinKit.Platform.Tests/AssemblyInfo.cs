using Xunit;

// Platform 层大量依赖进程级全局状态（性能计数器基准、WMI 连接、日志文件句柄、注册表缓存），
// 并行执行会让采样差值互相干扰并放大 WMI 负载，因此整个测试程序集关闭并行。
// 另外 WinKit.Core.Diagnostics.Log 也是进程级静态状态。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
