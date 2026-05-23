using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PopClip.Hooks.Interop;
using PopClip.Hooks.Window;

namespace PopClip.App.UI;

/// <summary>延时截图倒计时浮层。
///
/// 关键诉求：
/// - 不抢焦点：WS_EX_NOACTIVATE + ShowActivated=False，倒计时期间用户原本聚焦的应用
///   （比如开始菜单 / Office 菜单 / 浏览器 hover tooltip）保持原状，
///   不会因为浮层抢焦点而把目标 UI 状态打回去。
/// - Topmost：始终在所有窗口之上，让用户随时看到剩余秒数。
/// - 屏幕中央定位：用 Cursor 所在 monitor 的 work area 计算中心点，
///   多屏环境下浮层出现在用户当前关注的屏幕上。
/// - ESC 取消：通过 WM_HOTKEY 注册临时 ESC 全局热键并不优雅，因为浮层 Focus=False，
///   所以 ESC 取消改由 LowLevel 键盘钩子或 RegisterHotKey 处理。这里采用最简方案：
///   宿主调用方在外部用 RegisterHotKey 临时挂 ESC，倒计时窗暴露 Cancel() 方法被外部调用。
///
/// 触发关系：本窗口本身不直接触发 OcrSelectionWindow，由 OcrCaptureCoordinator 在
/// _onElapsed 回调里启动新的截图选区蒙层 —— 让"延时机制"与"截图机制"完全解耦</summary>
internal partial class ScreenshotCountdownWindow : Window
{
    private readonly DispatcherTimer _timer;
    private int _remaining;
    private readonly Action _onElapsed;
    private readonly Action _onCancelled;
    private bool _stopped;

    public ScreenshotCountdownWindow(int totalSeconds, Action onElapsed, Action onCancelled)
    {
        InitializeComponent();
        _remaining = Math.Max(1, totalSeconds);
        _onElapsed = onElapsed;
        _onCancelled = onCancelled;

        // 倒计时 1 秒一跳；从 _remaining 开始向下计数到 0 时触发 elapsed 回调
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
        Closed += OnClosed;

        UpdateText();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW：不抢焦点 + 不出现在 Alt+Tab
        WindowStyleHelper.ApplyNoActivateToolWindow(hwnd);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceAtScreenCenter();
        _timer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
    }

    private void PlaceAtScreenCenter()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;

        // 用 Cursor 当前位置选所在 monitor，确保倒计时浮层出现在用户正在看的那块屏幕上
        MonitorMetrics monitor;
        if (NativeMethods.GetCursorPos(out var pt))
        {
            monitor = MonitorQuery.FromPoint(pt.X, pt.Y);
        }
        else
        {
            monitor = MonitorQuery.FromRect(0, 0, 1, 1);
        }

        // 窗口固定 DIP 尺寸 140×140，转 anchor monitor 物理像素
        double dipScaleX = monitor.DpiX / 96.0;
        double dipScaleY = monitor.DpiY / 96.0;
        int wPx = (int)Math.Round(140 * dipScaleX);
        int hPx = (int)Math.Round(140 * dipScaleY);

        int leftPx = monitor.WorkLeft + (monitor.WorkWidth - wPx) / 2;
        int topPx = monitor.WorkTop + (monitor.WorkHeight - hPx) / 2;

        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            leftPx, topPx, wPx, hPx,
            NativeMethods.SWP_NOACTIVATE);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _remaining--;
        if (_remaining <= 0)
        {
            FireElapsed();
            return;
        }
        UpdateText();
    }

    private void UpdateText() => CountdownText.Text = _remaining.ToString();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Focusable=True + ShowActivated=False 的组合让窗口本身能接收键盘事件吗？
        // 实际上不能：WS_EX_NOACTIVATE 的窗口不会获得 keyboard focus。
        // ESC 取消的真正路径走 RegisterHotKey 由 OcrCaptureCoordinator 处理；
        // 这里保留 KeyDown 仅作 fallback：若用户用某种方式让窗口拿到了焦点，按 ESC 也能取消
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelByUser();
        }
    }

    /// <summary>外部调用：取消倒计时（用户按 ESC / 主动调用）。
    /// 多次调用幂等。已经触发 elapsed 后再调取消会被忽略</summary>
    public void CancelByUser()
    {
        if (_stopped) return;
        _stopped = true;
        _timer.Stop();
        try { _onCancelled(); } catch { }
        try { Close(); } catch { }
    }

    private void FireElapsed()
    {
        if (_stopped) return;
        _stopped = true;
        _timer.Stop();
        try { _onElapsed(); } catch { }
        try { Close(); } catch { }
    }
}
