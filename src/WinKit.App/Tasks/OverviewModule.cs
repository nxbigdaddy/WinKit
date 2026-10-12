using System.Windows;
using WinKit.App.Services;

namespace WinKit.App.Tasks
{
    /// <summary>
    /// 总览（仪表盘）模块。只做注册与视图创建，具体逻辑在 DashboardViewModel 里。
    /// 不引用任何其他模块，避免和并行开发的页面产生编译耦合。
    /// </summary>
    public sealed class OverviewModule : IToolModule
    {
        public string Id => "dashboard";

        public string Title => "总览";

        public string Group => "监控";

        /// <summary>Segoe MDL2 Assets：仪表盘。</summary>
        public string Icon => "\uE9D9";

        public int Order => 0;

        public bool RequiresAdmin => false;

        public string Keywords => "dashboard 总览 监控 cpu 内存 磁盘 网络 系统信息";

        public FrameworkElement CreateView() => new Views.Pages.DashboardPage();
    }
}
