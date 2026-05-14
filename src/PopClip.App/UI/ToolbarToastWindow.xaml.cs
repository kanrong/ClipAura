using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PopClip.Core.Logging;
using PopClip.Hooks.Window;

namespace PopClip.App.UI;

/// <summary>独立于 FloatingToolbar 的轻量提示窗，复制 / 动作完成等结果信息走这里。
///
/// 拆分动机：
/// - 浮窗 SizeToContent=Manual 模式下，把 Toast 内嵌进 FloatingToolbar 会被外层 Clip 裁掉。
/// - DismissOnActionInvoked 触发"复制完 700ms 关浮窗"时，Toast 与浮窗共生死，用户看不到。
/// - 两窗体分离后浮窗只管按钮、Toast 自管定位 + 计时关闭，互不干扰。
///
/// 单实例复用：每次只重写文字 / 重新定位 / 重置计时，避免反复 Window 创建销毁。</summary>
internal partial class ToolbarToastWindow : Window
{
    private readonly ILog _log;
    private CancellationTokenSource? _hideCts;
    private string? _copyText;

    public ToolbarToastWindow(ILog log)
    {
        _log = log;
        InitializeComponent();
        // 与 FloatingToolbar 同款：附加 WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW，
        // 防止 Show() 在某些场景下抢焦点 + 不在 Alt+Tab 列表里出现
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != 0) WindowStyleHelper.ApplyNoActivateToolWindow(hwnd);
    }

    /// <summary>显示 toast 并定位到 anchorCenterX 水平中心、anchorTopY 顶部。
    /// 调用方负责传入"目标位置"（一般是浮窗下沿中心），本窗自己根据实际 width 居中 anchorCenterX</summary>
    public void Show(string text, string? copyText, bool isError, int durationMs, double anchorCenterX, double anchorTopY)
    {
        try
        {
            _hideCts?.Cancel();
            _hideCts = new CancellationTokenSource();
            _copyText = copyText;
            ToastText.Text = text;
            ToastCopyButton.Visibility = string.IsNullOrEmpty(copyText) ? Visibility.Collapsed : Visibility.Visible;

            if (isError)
            {
                // 错误态固定红底，不跟 toolbar 主题；红色更醒目，避免主题色与错误语义冲突
                var errorBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x1E, 0x1E));
                ToastBorder.Background = errorBrush;
            }
            else
            {
                ToastBorder.ClearValue(System.Windows.Controls.Border.BackgroundProperty);
            }

            // 先 measure 出 toast 实际宽度，再用 anchorCenterX 把它水平居中。
            // 首次 Show 时先把窗口贴在屏幕外（-32000）让 SizeToContent 完成测量，再瞬间挪到目标位置，
            // 避免用户看到 toast 从 (0,0) 跳到目标处的 1 帧闪动；
            // ShowActivated=false 在 XAML 已设，base.Show() 不会抢焦点
            SizeToContent = SizeToContent.WidthAndHeight;
            if (!IsVisible)
            {
                Left = -32000;
                Top = -32000;
                base.Show();
            }
            UpdateLayout();

            var w = ActualWidth > 0 ? ActualWidth : 200;
            Left = anchorCenterX - w / 2;
            Top = anchorTopY;

            var cts = _hideCts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(durationMs, cts.Token).ConfigureAwait(false);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (cts.IsCancellationRequested) return;
                        Hide();
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _log.Warn("toast hide schedule failed", ("err", ex.Message));
                }
            });
        }
        catch (Exception ex)
        {
            _log.Warn("toast show failed", ("err", ex.Message));
        }
    }

    /// <summary>外部强制隐藏（如 toolbar 释放、用户主动关闭等），同步取消计时器</summary>
    public void HideNow()
    {
        _hideCts?.Cancel();
        _copyText = null;
        if (IsVisible) Hide();
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_copyText)) return;
        try
        {
            Clipboard.SetText(_copyText);
            ToastText.Text = "错误信息已复制";
            ToastCopyButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _log.Warn("toast copy failed", ("err", ex.Message));
        }
    }
}
