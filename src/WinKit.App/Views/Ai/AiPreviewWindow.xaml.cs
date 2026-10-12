using System.ComponentModel;
using System.Windows;
using WinKit.App.ViewModels;

namespace WinKit.App.Views.Ai
{
    /// <summary>
    /// 发送前预览窗口。**这里刻意不放任何业务逻辑**：
    /// 状态、联网判定、写回请求全部在 <see cref="AiAssistantViewModel"/> 里，
    /// 这样"用户看到的内容 = 将要发送的内容"这件事只在一处成立，易于审阅与测试。
    ///
    /// 唯一的交互胶水是关窗：先取消进行中的请求并标记窗口已关闭，再关。
    /// 走 OnClosing 而不是只挂按钮，是为了把标题栏 X 与 Alt+F4 一起覆盖掉。
    /// </summary>
    public partial class AiPreviewWindow : Window
    {
        private readonly AiAssistantViewModel _viewModel;

        public AiPreviewWindow(AiAssistantViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = viewModel;
            // 标题由这里显式赋值：窗口标题是 CLR/DP 上的一次性赋值，
            // 在 XAML 里写 Title="{Binding Title}" 反而会因为 DataContext 在 InitializeComponent
            // 之后才设置而产生一次绑定错误噪音，不如直接给值干净。
            Title = viewModel.Title;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // 取消令牌 + 置关闭标志，都是非阻塞操作；关窗不该被网络请求拖住
            if (_viewModel != null) _viewModel.CancelIfStreaming();
            base.OnClosing(e);
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            // 提前响应：点"关闭"立刻取消，不用等 Closing 阶段
            if (_viewModel != null) _viewModel.CancelIfStreaming();
        }
    }
}
