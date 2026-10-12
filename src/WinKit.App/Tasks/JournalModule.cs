using System.Windows;
using WinKit.App.Services;
using WinKit.App.Views.Pages;

namespace WinKit.App.Tasks
{
    /// <summary>
    /// 撤销中心模块。Id 同时用于 headless 调用（--task journal）与快捷键 Ctrl+Z 跳转。
    /// </summary>
    public sealed class JournalModule : IToolModule
    {
        public string Id => "journal";

        public string Title => "撤销中心";

        public string Group => "系统";

        /// <summary>Segoe MDL2 Assets：Undo。</summary>
        public string Icon => "\uE7A7";

        public int Order => 10;

        public bool RequiresAdmin => false;

        public string Keywords => "undo journal 撤销 回滚 操作日志 历史";

        public FrameworkElement CreateView() => new JournalPage();
    }
}
