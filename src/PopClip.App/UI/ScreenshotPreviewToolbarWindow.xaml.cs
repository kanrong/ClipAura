using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace PopClip.App.UI;

/// <summary>截图预览的"独立工具条窗口"：与主预览窗解耦，本身就是一个透明无边框 Window，
/// 可通过 DragMove 拖到屏幕任意位置（包括跨屏 / 主窗外）。
///
/// 为什么独立窗口而不是嵌在主窗 Grid 内：
/// - 内嵌时窄高截图（如侧栏 / 长条 OCR 区域）会让工具条宽度反推 Grid 列宽，
///   把 Frame Border 撑得比截图大，视觉错位；
/// - 内嵌的 Overlay 模式遮挡截图原内容；外接 Top/Bottom 又会被截图边界裁切。
/// 独立 Window 一次性解决撑图 / 跨屏 / 拖动诉求，与 OcrResultToolbarWindow 走同一套路径。
///
/// 状态文案（DimensionText）由 host 推送过来；按钮回调全部委托给 host（CommandCopy/Save/Ocr/Close）。
/// 工具条只是显示层，所有数据 / 副作用都在主窗内</summary>
internal partial class ScreenshotPreviewToolbarWindow : Window
{
    private readonly ScreenshotPreviewWindow _host;
    private DispatcherTimer? _localToastTimer;

    public ScreenshotPreviewToolbarWindow(ScreenshotPreviewWindow host, string dimensionText)
    {
        _host = host;
        InitializeComponent();
        Owner = host;
        DimensionText.Text = dimensionText;

        // 整个 ToolbarBorder 都是拖动响应区：按钮自己消费 MouseLeftButtonDown，
        // 不会冒泡到 Border，所以点按钮不会误触发拖动。
        // 不用 Window.DragMove() —— 它走 SC_MOVE，受 Aero Snap 影响把工具条弹回工作区
        ToolbarBorder.MouseLeftButtonDown += OnBorderMouseDown;
    }

    private void OnBorderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        WindowDragHelper.BeginDrag(this);
    }

    /// <summary>工具条自己的轻量 toast：用于复制 / 保存等动作的反馈，
    /// 让消息出现在按钮正下方而不是飘到主窗中央。1.8 秒后自动隐藏</summary>
    public void ShowLocalToast(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        LocalToastText.Text = text;
        LocalToastBorder.Visibility = Visibility.Visible;
        if (_localToastTimer is null)
        {
            _localToastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            _localToastTimer.Tick += (_, _) =>
            {
                _localToastTimer!.Stop();
                LocalToastBorder.Visibility = Visibility.Collapsed;
            };
        }
        _localToastTimer.Stop();
        _localToastTimer.Start();
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e) => _host.CommandCopy(ShowLocalToast);
    private void OnSaveClicked(object sender, RoutedEventArgs e) => _host.CommandSave(ShowLocalToast);
    private void OnOcrClicked(object sender, RoutedEventArgs e) => _host.CommandOcr();
    private void OnCloseClicked(object sender, RoutedEventArgs e) => _host.CommandClose();
}
