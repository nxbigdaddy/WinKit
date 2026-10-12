using System.Windows;

namespace WinKit.App.Services
{
    /// <summary>
    /// 一个功能页就是一个插件。主窗口导航完全由这些插件驱动，
    /// 新增功能不需要改主窗口代码 —— 这也是能多 agent 并行开发同一工程的前提。
    /// </summary>
    public interface IToolModule
    {
        /// <summary>唯一标识，例如 "dashboard"、"process"。同时用于 headless 调用（--task）。</summary>
        string Id { get; }

        /// <summary>导航显示名。</summary>
        string Title { get; }

        /// <summary>导航分组名，决定左侧导航的分组标题。</summary>
        string Group { get; }

        /// <summary>Segoe MDL2 Assets 图标字形，例如 "\uE9D9"。</summary>
        string Icon { get; }

        /// <summary>同组内排序，越小越靠前。</summary>
        int Order { get; }

        /// <summary>是否需要管理员权限（导航项上显示盾牌图标）。</summary>
        bool RequiresAdmin { get; }

        /// <summary>命令面板搜索关键字，空格分隔。</summary>
        string Keywords { get; }

        /// <summary>创建页面视图。每次调用应返回新实例。</summary>
        FrameworkElement CreateView();
    }
}
