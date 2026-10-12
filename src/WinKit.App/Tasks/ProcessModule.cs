using System.Windows;
using WinKit.App.Services;

namespace WinKit.App.Tasks
{
    /// <summary>
    /// 进程管理模块的注册入口。只负责描述"这是什么、怎么打开"，
    /// 具体逻辑全在 ProcessPage / ProcessViewModel 里，主窗口不需要知道任何进程相关的东西。
    /// </summary>
    public sealed class ProcessModule : IToolModule
    {
        public string Id => "process";

        public string Title => "进程管理";

        public string Group => "监控";

        /// <summary>Segoe MDL2 Assets：TaskView / 应用列表字形。</summary>
        public string Icon => "\uE71D";

        public int Order => 10;

        /// <summary>查看进程不需要管理员；只有结束系统级进程才需要提权，届时由失败提示引导用户。</summary>
        public bool RequiresAdmin => false;

        public string Keywords => "process 进程 任务管理器 结束 kill cpu 内存 pid";

        public FrameworkElement CreateView() => new Views.Pages.ProcessPage();
    }
}
