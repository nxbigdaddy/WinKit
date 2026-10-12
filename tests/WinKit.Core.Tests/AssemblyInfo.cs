using Xunit;

// Log / PortablePaths 等存在进程级静态状态（日志文件句柄、环境变量），
// 并行执行会导致用例互相干扰，因此整个测试程序集关闭并行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
