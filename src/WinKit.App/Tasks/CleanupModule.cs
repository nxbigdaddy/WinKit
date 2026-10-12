using System.Windows;
using WinKit.App.Services;

namespace WinKit.App.Tasks
{
    /// <summary>
    /// 垃圾清理模块的注册入口。
    ///
    /// 这一页是整个产品的卖点所在：**危险操作必须先预览、可回滚**。
    /// 所以这里的元数据刻意保持"零门槛"（Order 最靠前、不需要管理员就能看），
    /// 但真正的安全约束不在这里，而在 CleanupViewModel 的执行流程里：
    /// 扫描 → 逐项预览 → 明确说明处置方式 → 确认（彻底删除还要二次确认）→ 执行 → 可撤销。
    /// </summary>
    public sealed class CleanupModule : IToolModule
    {
        public string Id => "cleanup";

        public string Title => "垃圾清理";

        public string Group => "清理";

        /// <summary>Segoe MDL2 Assets：Delete（垃圾桶）字形。</summary>
        public string Icon => "\uE74D";

        public int Order => 0;

        /// <summary>
        /// 扫描与"移入隔离区/回收站"都不需要提权。极少数规则（系统临时文件、系统转储）
        /// 确实需要管理员，但它们默认关闭且会在规则行上单独标注"需要管理员"，
        /// 用 RequiresAdmin 把整页锁掉反而会让用户连预览都看不到 —— 那正是本页最想避免的事。
        /// </summary>
        public bool RequiresAdmin => false;

        public string Keywords => "cleanup 清理 垃圾 临时文件 缓存 回收站 磁盘空间 释放";

        public FrameworkElement CreateView() => new Views.Pages.CleanupPage();
    }
}
