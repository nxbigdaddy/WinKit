using System.Windows;
using WinKit.App.Services;
using WinKit.App.Views.Pages;

namespace WinKit.App.Tasks
{
    /// <summary>
    /// 一键体检模块。Id 同时用于 headless 调用与导航定位。
    ///
    /// RequiresAdmin 固定为 false：体检全程只读，且只使用不需要提权的清理规则 ——
    /// "看一眼系统状态"这件事绝不该弹 UAC。
    /// </summary>
    public sealed class HealthModule : IToolModule
    {
        public string Id => "health";

        public string Title => "一键体检";

        public string Group => "监控";

        /// <summary>Segoe MDL2 Assets：CheckMark（对勾）。</summary>
        public string Icon => "\uE73E";

        public int Order => 5;

        public bool RequiresAdmin => false;

        public string Keywords => "health check 体检 状态 评分 诊断 打分";

        public FrameworkElement CreateView() => new HealthPage();
    }
}
