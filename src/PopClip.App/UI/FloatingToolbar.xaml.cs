using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using PopClip.App.Config;
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
public partial class FloatingToolbar : Window, INotifyPropertyChanged
{
    public ObservableCollection<ToolbarItem> Items { get; } = new();

    private nint _hwnd;
    private readonly ILog _log;
    private DateTime _lastShownAtUtc;
    private bool _prewarmed;
    private bool _isShown;
    private bool _isIconVisible = true;
    private bool _isTextVisible = true;
    private const int ShadowPaddingDip = 9;

    public event Action? Dismissed;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>是否显示按钮的图标部分；与 IsTextVisible 联动构成三种显示模式</summary>
    public bool IsIconVisible
    {
        get => _isIconVisible;
        private set
        {
            if (_isIconVisible == value) return;
            _isIconVisible = value;
            OnPropertyChanged();
        }
    }

    /// <summary>是否显示按钮的文字部分</summary>
    public bool IsTextVisible
    {
        get => _isTextVisible;
        private set
        {
            if (_isTextVisible == value) return;
            _isTextVisible = value;
            OnPropertyChanged();
        }
    }

    public FloatingToolbar(ILog log)
    {
        _log = log;
        InitializeComponent();
        DataContext = this;
        SourceInitialized += OnSourceInitialized;
        MouseLeave += OnMouseLeave;
    }

    /// <summary>由设置层调用，切换三种显示模式。
    /// 兜底：两者都关闭时回退为图标+文字，避免按钮空空如也无法点击</summary>
    public void ApplyDisplayMode(ToolbarDisplayMode mode)
    {
        Dispatcher.Invoke(() =>
        {
            switch (mode)
            {
                case ToolbarDisplayMode.IconOnly:
                    IsIconVisible = true;
                    IsTextVisible = false;
                    break;
                case ToolbarDisplayMode.TextOnly:
                    IsIconVisible = false;
                    IsTextVisible = true;
                    break;
                default:
                    IsIconVisible = true;
                    IsTextVisible = true;
                    break;
            }
        });
    }

    /// <summary>由设置层调用，切换浮窗浅色/深色主题</summary>
    public void ApplyThemeMode(ToolbarThemeMode mode)
    {
        Dispatcher.Invoke(() =>
        {
            var prefix = mode == ToolbarThemeMode.Dark ? "ToolbarDark" : "ToolbarLight";
            SetToolbarResource("ToolbarBackground", $"{prefix}Background");
            SetToolbarResource("ToolbarShadow", $"{prefix}Shadow");
            SetToolbarResource("ToolbarForeground", $"{prefix}Foreground");
            SetToolbarResource("ToolbarHover", $"{prefix}Hover");
        });
    }

    private void SetToolbarResource(string targetKey, string sourceKey)
    {
        var value = TryFindResource(sourceKey) ?? System.Windows.Application.Current?.TryFindResource(sourceKey);
        if (value is null)
        {
            _log.Warn("toolbar theme resource missing", ("key", sourceKey));
            return;
        }
        Resources[targetKey] = value;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>当前是否处于显示状态。供外层在监听全局点击时判断要不要主动 Dismiss</summary>
    public bool IsShown => _isShown;

    /// <summary>判断屏幕物理像素坐标是否落在浮窗矩形内。
    /// 用 Win32 GetWindowRect 取真实物理像素，避免与低级钩子坐标系混用 DIP</summary>
    public bool ContainsScreenPoint(int x, int y)
    {
        if (!_isShown || _hwnd == 0) return false;
        if (!NativeMethods.GetWindowRect(_hwnd, out var rect)) return false;
        return x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
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

        var shadowPaddingPxX = (int)Math.Ceiling(ShadowPaddingDip * monitor.DpiX / 96.0);
        var shadowPaddingPxY = (int)Math.Ceiling(ShadowPaddingDip * monitor.DpiY / 96.0);
        var (x, y) = ComputePositionPx(anchorRect, monitor, widthPx, heightPx, shadowPaddingPxX, shadowPaddingPxY);
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

    /// <summary>把工具栏摆到 anchor 下方，左边缘贴近鼠标垂线；触底则上翻并按工作区约束</summary>
    private static (int X, int Y) ComputePositionPx(
        SelectionRect anchor,
        MonitorMetrics monitor,
        int widthPx,
        int heightPx,
        int shadowPaddingPxX,
        int shadowPaddingPxY)
    {
        const int HorizontalOffset = 0;
        const int VerticalGap = 20;
        var x = anchor.Left + HorizontalOffset - shadowPaddingPxX;
        var y = anchor.Bottom + VerticalGap - shadowPaddingPxY;

        if (y + heightPx > monitor.WorkBottom - 4)
        {
            // 下方放不下，回退到 anchor 上方
            y = anchor.Top - heightPx - VerticalGap + shadowPaddingPxY;
        }

        if (x < monitor.WorkLeft + 4) x = monitor.WorkLeft + 4;
        if (x + widthPx > monitor.WorkRight - 4) x = monitor.WorkRight - widthPx - 4;
        if (y < monitor.WorkTop + 4) y = monitor.WorkTop + 4;
        if (y + heightPx > monitor.WorkBottom - 4) y = monitor.WorkBottom - heightPx - 4;
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
