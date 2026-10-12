using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace WinKit.Platform.Metrics
{
    public sealed class ProcessSample
    {
        public int Pid { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Path { get; set; }
        public long WorkingSet { get; set; }
        public long PrivateMemory { get; set; }

        /// <summary>占整机 CPU 的百分比（和任务管理器口径一致）。首次采样为 0。</summary>
        public double CpuPercent { get; set; }

        public int Threads { get; set; }
        public int Handles { get; set; }
        public DateTime? StartTime { get; set; }
        public bool AccessDenied { get; set; }

        public string DisplayName => string.IsNullOrEmpty(Description) ? Name : Description!;

        public string StartTimeText => StartTime.HasValue ? StartTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "—";

        public string PathText => string.IsNullOrEmpty(Path) ? "—" : Path!;
    }

    /// <summary>
    /// 进程采样器。CPU 占用需要两次采样的差值，因此第一次调用所有进程的 CPU 都是 0。
    /// 全部走 Process API，不碰 WMI —— 命令行等冷数据用 WmiQuery 按需取。
    /// </summary>
    public sealed class ProcessSampler
    {
        private Dictionary<int, TimeSpan> _previousCpu = new Dictionary<int, TimeSpan>();
        private long _previousTimestamp;

        public IReadOnlyList<ProcessSample> Sample()
        {
            var now = Stopwatch.GetTimestamp();
            var elapsedSeconds = _previousTimestamp == 0
                ? 0d
                : (now - _previousTimestamp) / (double)Stopwatch.Frequency;

            var cores = Environment.ProcessorCount;
            var next = new Dictionary<int, TimeSpan>(_previousCpu.Count + 64);
            var result = new List<ProcessSample>(_previousCpu.Count + 64);

            Process[] processes;
            try
            {
                processes = Process.GetProcesses();
            }
            catch (Exception)
            {
                return result;
            }

            foreach (var process in processes)
            {
                try
                {
                    var pid = process.Id;
                    var sample = new ProcessSample { Pid = pid };

                    try { sample.Name = process.ProcessName; }
                    catch (Exception) { sample.Name = "pid:" + pid; }

                    try { sample.WorkingSet = process.WorkingSet64; } catch (Exception) { sample.AccessDenied = true; }
                    try { sample.PrivateMemory = process.PrivateMemorySize64; } catch (Exception) { sample.AccessDenied = true; }
                    try { sample.Threads = process.Threads.Count; } catch (Exception) { }
                    try { sample.Handles = process.HandleCount; } catch (Exception) { }
                    try { sample.StartTime = process.StartTime; } catch (Exception) { }

                    try
                    {
                        var module = process.MainModule;
                        if (module != null)
                        {
                            sample.Path = module.FileName;
                            try { sample.Description = module.FileVersionInfo?.FileDescription; } catch (Exception) { }
                        }
                    }
                    catch (Exception)
                    {
                        // 系统进程 / 其他用户的进程读不到模块信息，属于预期情况
                        sample.AccessDenied = true;
                    }

                    TimeSpan cpu = TimeSpan.Zero;
                    var cpuReadable = false;
                    try
                    {
                        cpu = process.TotalProcessorTime;
                        cpuReadable = true;
                    }
                    catch (Exception) { }

                    if (cpuReadable)
                    {
                        TimeSpan previous;
                        if (elapsedSeconds > 0 && cores > 0 && _previousCpu.TryGetValue(pid, out previous))
                        {
                            var delta = (cpu - previous).TotalSeconds;
                            if (delta < 0) delta = 0;
                            sample.CpuPercent = Math.Round(
                                Math.Min(100d, delta / elapsedSeconds / cores * 100d), 1);
                        }
                        next[pid] = cpu;
                    }

                    result.Add(sample);
                }
                catch (Exception)
                {
                    // 进程可能在枚举过程中退出，忽略
                }
                finally
                {
                    try { process.Dispose(); } catch (Exception) { }
                }
            }

            _previousCpu = next;
            _previousTimestamp = now;
            return result;
        }

        public void Reset()
        {
            _previousCpu = new Dictionary<int, TimeSpan>();
            _previousTimestamp = 0;
        }
    }
}
