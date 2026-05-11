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
    private bool _prewarmed;
    private bool _isShown;

    public event Action? Dismissed;

    public FloatingToolbar(ILog log)
    {
        _log = log;
        InitializeComponent();
        DataContext = this;
        SourceInitialized += OnSourceInitialized;
        MouseLeave += OnMouseLeave;
    }

    /// <summary>预热：触发 HWND 创建并 attach WndProc hook。
    /// 不在此预算 size，因为预热时 Items 为空，size 是 padding-only 的 10x10，毫无意义</summary>
    public void PrewarmLayout()
    {
        if (_prewarmed) return;
        var helper = new WindowInteropHelper(this);
        _hwnd = helper.EnsureHandle();
        _prewarmed = true;
        _log.Info("toolbar hwnd ready", ("hwnd", _hwnd));
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
        PrewarmLayout();

        // 关键：必须 base.Show() 一次让 SizeToContent="WidthAndHeight" 真正生效。
        // Hidden 状态下的 UpdateLayout 不会更新 Window 的 ActualWidth/Height。
        // Left/Top 先预定位到屏幕外（-32000）让用户感知不到，立刻 SetWindowPos 移到正确位置。
        // ShowActivated=False 保证不抢焦点
        Left = -32000;
        Top = -32000;
        base.Show();
        UpdateLayout();

        var widthDip = ActualWidth;
        var heightDip = ActualHeight;
        if (widthDip <= 10 || heightDip <= 10)
        {
            _log.Warn("toolbar size suspiciously small",
                ("w", widthDip), ("h", heightDip),
                ("items", Items.Count));
            // 即便很小也尝试显示，至少能看到痕迹便于排错
        }

        var monitor = MonitorQuery.FromPoint(anchorRect.Right, anchorRect.Bottom);
        var widthPx = (int)Math.Ceiling(widthDip * monitor.DpiX / 96.0);
        var heightPx = (int)Math.Ceiling(heightDip * monitor.DpiY / 96.0);

        var (x, y) = ComputePositionPx(anchorRect, monitor, widthPx, heightPx);
        _log.Info("toolbar show",
            ("anchorRight", anchorRect.Right), ("anchorBottom", anchorRect.Bottom),
            ("widthPx", widthPx), ("heightPx", heightPx),
            ("x", x), ("y", y),
            ("dpiX", monitor.DpiX));

        // SWP_NOSIZE 保留 SizeToContent 算出的大小
        WindowStyleHelper.ShowNoActivate(_hwnd, x, y);
        _isShown = true;
        _lastShownAtUtc = DateTime.UtcNow;
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

    private void OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
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
        if (!_isShown) return;
        if (_hwnd == 0) return;
        // 用 WPF Hide() 而非 Win32 ShowWindow(SW_HIDE)，保证 Visibility 属性同步翻成 Hidden。
        // 否则下次 base.Show() 会因为 Visibility 仍是 Visible 而 no-op，SizeToContent 不再触发
        base.Hide();
        _isShown = false;
        Dismissed?.Invoke();
        _log.Debug("toolbar dismissed", ("reason", reason));
    }

    /// <summary>外部代码触发关闭（前台窗口变化、Esc、新选区等）</summary>
    public void DismissExternal(string reason) => Dispatcher.Invoke(() => HideToolbar(reason));
}
