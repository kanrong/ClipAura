using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using PopClip.Core.Logging;
using PopClip.Core.Model;
using PopClip.Hooks.Interop;
using PopClip.Hooks.Window;

namespace PopClip.App.UI;

/// <summary>不抢焦点的浮动工具栏。
/// 关键技术点：
/// - 扩展样式叠加 WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
/// - 重写 WndProc 处理 WM_MOUSEACTIVATE 返回 MA_NOACTIVATE
/// - 用 SetWindowPos(SWP_NOACTIVATE) + ShowWindow(SW_SHOWNOACTIVATE) 显示，不走 Window.Show()
/// - 定位使用 GetDpiForWindow 处理多显示器异构 DPI</summary>
public partial class FloatingToolbar : Window
{
    public ObservableCollection<ToolbarItem> Items { get; } = new();

    private nint _hwnd;
    private readonly ILog _log;
    private DateTime _lastShownAtUtc;

    public event Action? Dismissed;

    public FloatingToolbar(ILog log)
    {
        _log = log;
        InitializeComponent();
        DataContext = this;
        SourceInitialized += OnSourceInitialized;
        MouseLeave += OnMouseLeave;
        Deactivated += (_, _) => { /* 此窗口不抢焦点，不会有 Deactivated */ };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        _hwnd = helper.Handle;
        WindowStyleHelper.ApplyNoActivateToolWindow(_hwnd);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return NativeMethods.MA_NOACTIVATE;
        }
        return 0;
    }

    /// <summary>在选区附近以"不抢焦点"的方式显示工具栏。
    /// anchorRect 由 UIA (TextPatternRange.GetBoundingRectangles) 或鼠标坐标产生，
    /// 单位均为物理像素 (device pixel)。本方法把 toolbar 的 DIP 尺寸换算到 anchor 所在 monitor 的
    /// 物理像素，再用物理像素做边界约束 + SetWindowPos，避免 DIP/PX 混用偏移</summary>
    public void ShowAt(SelectionRect anchorRect, ForegroundWindowInfo foreground)
    {
        EnsureHwndCreated();

        UpdateLayout();
        var widthDip = ActualWidth;
        var heightDip = ActualHeight;
        if (widthDip <= 0 || heightDip <= 0)
        {
            return;
        }

        var monitor = MonitorQuery.FromPoint(anchorRect.Right, anchorRect.Bottom);
        var widthPx = (int)Math.Ceiling(widthDip * monitor.DpiX / 96.0);
        var heightPx = (int)Math.Ceiling(heightDip * monitor.DpiY / 96.0);

        var (x, y) = ComputePositionPx(anchorRect, monitor, widthPx, heightPx);
        WindowStyleHelper.ShowNoActivate(_hwnd, x, y);
        _lastShownAtUtc = DateTime.UtcNow;
    }

    /// <summary>用 EnsureHandle 提前创建 HWND，避免 base.Show() 走激活路径</summary>
    private void EnsureHwndCreated()
    {
        if (_hwnd != 0) return;
        var helper = new WindowInteropHelper(this);
        _hwnd = helper.EnsureHandle();
    }

    private static (int X, int Y) ComputePositionPx(SelectionRect anchor, MonitorMetrics monitor, int widthPx, int heightPx)
    {
        const int Gap = 8;
        var x = anchor.Right - widthPx / 2;
        var y = anchor.Bottom + Gap;

        if (x < monitor.WorkLeft + 4) x = monitor.WorkLeft + 4;
        if (x + widthPx > monitor.WorkRight - 4) x = monitor.WorkRight - widthPx - 4;
        if (y + heightPx > monitor.WorkBottom - 4)
        {
            y = anchor.Top - heightPx - Gap;
        }
        if (y < monitor.WorkTop + 4) y = monitor.WorkTop + 4;
        return (x, y);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        // 鼠标离开后延迟自动消失，避免用户在边缘抖动时误关
        var leftAt = DateTime.UtcNow;
        Task.Delay(800).ContinueWith(_ =>
        {
            Dispatcher.Invoke(() =>
            {
                if (IsMouseOver) return;
                if (DateTime.UtcNow - _lastShownAtUtc < TimeSpan.FromMilliseconds(700)) return;
                HideToolbar("mouse-leave");
            });
        });
    }

    public void HideToolbar(string reason)
    {
        if (_hwnd == 0) return;
        WindowStyleHelper.Hide(_hwnd);
        Dismissed?.Invoke();
        _log.Debug("toolbar dismissed", ("reason", reason));
    }

    /// <summary>外部代码触发关闭（前台窗口变化、Esc、新选区等）</summary>
    public void DismissExternal(string reason) => Dispatcher.Invoke(() => HideToolbar(reason));
}
