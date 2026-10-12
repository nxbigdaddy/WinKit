using System.Windows;
using WinKit.App.Services;
using WinKit.App.Views.Pages;

namespace WinKit.App.Tasks
{
    /// <summary>设置模块。分组与撤销中心一致，排在它后面。</summary>
    public sealed class SettingsModule : IToolModule
    {
        public string Id => "settings";

        public string Title => "设置";

        public string Group => "系统";

        /// <summary>Segoe MDL2 Assets：Settings（齿轮）。</summary>
        public string Icon => "\uE713";

        public int Order => 20;

        public bool RequiresAdmin => false;

        public string Keywords => "settings 设置 主题 深色 浅色 只读 审计 采样 关于";

        public FrameworkElement CreateView() => new SettingsPage();
    }
}
