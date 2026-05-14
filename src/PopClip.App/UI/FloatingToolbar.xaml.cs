using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using PopClip.App.Config;
using PopClip.Core.Actions;
using PopClip.Core.Logging;
using PopClip.Core.Model;
using PopClip.Core.Session;
using PopClip.Hooks.Interop;
using PopClip.Hooks.Window;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using RectangleGeometry = System.Windows.Media.RectangleGeometry;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace PopClip.App.UI;

/// <summary>不抢焦点的浮动工具栏。
/// 关键技术点：
/// - 扩展样式叠加 WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
/// - 重写 WndProc 处理 WM_MOUSEACTIVATE 返回 MA_NOACTIVATE
/// - 用 SetWindowPos(SWP_NOACTIVATE) + ShowWindow(SW_SHOWNOACTIVATE) 显示，不走 Window.Show()
/// - 定位使用 GetDpiForWindow 处理多显示器异构 DPI</summary>
public partial class FloatingToolbar : Window, INotifyPropertyChanged, INotificationSink
{
    public ObservableCollection<ToolbarItem> Items { get; } = new();

    private nint _hwnd;
    private readonly ILog _log;
    private DateTime _lastShownAtUtc;
    private bool _prewarmed;
    private bool _isShown;
    private bool _isIconVisible = true;
    private bool _isTextVisible = true;
    private int _selectedIndex = -1;
    private int _maxActionsPerRow = 6;
    private ToolbarThemeMode _themeMode = ToolbarThemeMode.Auto;
    private bool _followAccentColor = true;
    private bool _dismissOnMouseLeave = true;
    private int _dismissMouseLeaveDelayMs = 800;
    private bool _dismissOnTimeout;
    private int _dismissTimeoutMs = 5000;
    // 鼠标未悬停时的目标透明度；鼠标进入浮窗后窗口 Opacity 立即恢复为 1.0
    private double _idleOpacity = 1.0;
    private bool _dismissOnEscape = true;
    private bool _keyboardShortcutsEnabled = true;
    private bool _tabNavigationEnabled = true;
    private bool _numberShortcutsEnabled = true;
    private CancellationTokenSource? _toastCts;
    private CancellationTokenSource? _dismissTimeoutCts;
    private string? _toastCopyText;
    private const int ShadowPaddingDip = 9;
    private const double ToolbarButtonPaddingX = 12;
    private const double ToolbarButtonPaddingY = 9;
    // 外圆角半径，与阴影底板/描边层的 CornerRadius 完全一致；
    // ContentClipHost 用它作 Clip 圆角，按钮 hover 颜色直接铺满到外圆角弧线，
    // 描边层（最上层）再把外圆角描边盖住 hover 边缘的抗锯齿过渡像素
    private double _outerCornerRadius;

    public event Action? Dismissed;
    public event PropertyChangedEventHandler? PropertyChanged;
    public DateTime LastToastAtUtc { get; private set; } = DateTime.MinValue;

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
        MouseEnter += OnMouseEnterRestoreOpacity;
        MouseLeave += OnMouseLeave;
        Items.CollectionChanged += OnItemsChanged;
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
        => ApplyThemeMode(mode, _followAccentColor);

    public void ApplyAppearance(AppSettings settings)
    {
        ApplyDisplayMode(settings.ToolbarDisplay);
        ApplyThemeMode(settings.ToolbarTheme, settings.FollowAccentColor);
        ApplySurfaceStyle(settings.ToolbarSurface);
        _maxActionsPerRow = Math.Clamp(settings.ToolbarMaxActionsPerRow, 3, 12);
        _dismissOnMouseLeave = settings.DismissOnMouseLeave;
        _dismissMouseLeaveDelayMs = Math.Clamp(settings.DismissMouseLeaveDelayMs, 0, 5000);
        _dismissOnTimeout = settings.DismissOnTimeout;
        _dismissTimeoutMs = Math.Clamp(settings.DismissTimeoutMs, 100, 120000);
        _dismissOnEscape = settings.DismissOnEscapeKey;
        _keyboardShortcutsEnabled = settings.EnableToolbarKeyboardShortcuts;
        _tabNavigationEnabled = settings.EnableToolbarTabNavigation;
        _numberShortcutsEnabled = settings.EnableToolbarNumberShortcuts;
        _idleOpacity = Math.Clamp(settings.ToolbarIdleOpacity, 0.3, 1.0);
        Dispatcher.Invoke(() =>
        {
            // 未显示或鼠标不在浮窗上时立刻按 idle 透明度生效；
            // 已显示且鼠标当前悬停在浮窗内时保持完全不透明，等离开后再回落
            Opacity = IsMouseOverWindowReal() ? 1.0 : _idleOpacity;
        });
        Dispatcher.Invoke(() =>
        {
            var radius = Math.Clamp(settings.ToolbarCornerRadius, 0, 18);
            var spacing = Math.Clamp(settings.ToolbarButtonSpacing, 0, 10);
            var fontSize = Math.Clamp(settings.ToolbarFontSize, 10, 18);
            // 外壳圆角 = 用户设置 radius + 1（让圆角观感与设置数值匹配，且给描边留 1px 空间）
            // 阴影底板 / ContentClipHost.Clip / 描边层都用同一个外圆角，避免任何"双层圆角错位"
            Resources["ToolbarCornerRadius"] = new CornerRadius(radius + 1);
            // 按钮本体保持矩形：左右两端按钮的"圆角观感"完全交给 ContentClipHost 的 Clip 完成
            Resources["ToolbarButtonCornerRadius"] = new CornerRadius(0);
            _outerCornerRadius = radius + 1;
            // spacing 仅作用于按钮的左右 Margin；按钮 Padding 固定（仅决定按钮内容的内边距）。
            // 需要调整按钮高度时，改 ToolbarButtonPaddingY 即可。
            // 不再有 container padding：StackPanel 直接贴满 ContentClipHost，
            // ItemsPanel 通过负 Margin 抵消最外侧按钮的左右 Margin，最左/最右按钮的高亮区直接贴到外壳内边
            Resources["ToolbarButtonMargin"] = new Thickness(spacing, 0, spacing, 0);
            Resources["ToolbarButtonPadding"] = new Thickness(
                ToolbarButtonPaddingX,
                ToolbarButtonPaddingY,
                ToolbarButtonPaddingX,
                ToolbarButtonPaddingY);
            Resources["ToolbarItemsPanelMargin"] = new Thickness(-spacing, 0, -spacing, 0);
            Resources["ToolbarButtonFontSize"] = fontSize;
            // 图标比正文大 4 号 + SemiBold 字重，让图标在按钮里成为视觉重心
            Resources["ToolbarIconFontSize"] = fontSize + 2;
            // 圆角变化时 Clip 半径也要跟着改，否则按钮还会以旧半径被裁切
            UpdateContentClip();
        });
    }

    /// <summary>用 RoundedRectangleGeometry 给 ContentClipHost 设置 Clip，
    /// 半径与外圆角一致：按钮 hover 颜色铺满到外圆角弧线，描边层再把外侧抗锯齿过渡盖住</summary>
    private void UpdateContentClip()
    {
        if (ContentClipHost is null) return;
        var w = ContentClipHost.ActualWidth;
        var h = ContentClipHost.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            // 尚未完成首次布局：清空旧 Clip，等 SizeChanged 触发时再更新
            ContentClipHost.Clip = null;
            return;
        }
        // 不超过容器一半，避免圆角值过大导致几何畸形
        var r = Math.Min(_outerCornerRadius, Math.Min(w, h) * 0.5);
        if (r <= 0)
        {
            ContentClipHost.Clip = null;
            return;
        }
        ContentClipHost.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
    }

    private void OnContentClipHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateContentClip();
    }

    private void ApplyThemeMode(ToolbarThemeMode mode, bool followAccentColor)
    {
        Dispatcher.Invoke(() =>
        {
            _themeMode = mode;
            _followAccentColor = followAccentColor;
            var resolved = mode == ToolbarThemeMode.Auto
                ? (SystemThemeHelper.IsSystemDark() ? ToolbarThemeMode.Dark : ToolbarThemeMode.Light)
                : mode;
            var prefix = resolved == ToolbarThemeMode.Dark ? "ToolbarDark" : "ToolbarLight";
            SetToolbarResource("ToolbarBackground", $"{prefix}Background");
            SetToolbarResource("ToolbarShadow", $"{prefix}Shadow");
            SetToolbarResource("ToolbarBorder", $"{prefix}Border");
            SetToolbarResource("ToolbarForeground", $"{prefix}Foreground");
            SetToolbarResource("ToolbarHover", $"{prefix}Hover");
            SetToolbarResource("ToolbarAccentSoft", $"{prefix}AccentSoft");
            SetToolbarResource("ToolbarToastBackground", $"{prefix}ToastBackground");
            if (followAccentColor)
            {
                Resources["ToolbarAccentSoft"] = new SolidColorBrush(BlendAccent(SystemThemeHelper.AccentColor(), resolved));
            }
        });
    }

    // 阴影参数策略：加深 Opacity（更明显）+ 缩小 BlurRadius（不晕散到边框外远处）；
    // 总预算 BlurRadius + ShadowDepth 不超过 ShadowPaddingDip(=9) 避免被透明窗口边界裁切
    private void ApplySurfaceStyle(ToolbarSurfaceStyle style)
    {
        Dispatcher.Invoke(() =>
        {
            switch (style)
            {
                case ToolbarSurfaceStyle.Shadow:
                    // 纯阴影：在没有描边的情况下用稍宽一点的范围让外形可读，但仍偏紧
                    Resources["ToolbarBorderThickness"] = new Thickness(0);
                    Resources["ToolbarShadowBlurRadius"] = 7d;
                    Resources["ToolbarShadowDepth"] = 2d;
                    Resources["ToolbarShadowOpacity"] = 0.42d;
                    break;
                case ToolbarSurfaceStyle.Border:
                    // 纯细边框：去掉阴影完全避免裁切；边框颜色由 ToolbarBorder 主题切换
                    Resources["ToolbarBorderThickness"] = new Thickness(1);
                    Resources["ToolbarShadowBlurRadius"] = 0d;
                    Resources["ToolbarShadowDepth"] = 0d;
                    Resources["ToolbarShadowOpacity"] = 0d;
                    break;
                default:
                    // ShadowAndBorder：阴影更深更紧贴，细边框 + 重阴影构成"漂浮卡片"感
                    Resources["ToolbarBorderThickness"] = new Thickness(1);
                    Resources["ToolbarShadowBlurRadius"] = 6d;
                    Resources["ToolbarShadowDepth"] = 2d;
                    Resources["ToolbarShadowOpacity"] = 0.32d;
                    break;
            }
        });
    }

    private static Color BlendAccent(Color accent, ToolbarThemeMode theme)
    {
        var factor = theme == ToolbarThemeMode.Dark ? 0.36 : 0.18;
        var baseColor = theme == ToolbarThemeMode.Dark ? Color.FromRgb(0x2B, 0x30, 0x37) : Color.FromRgb(0xFF, 0xFF, 0xFF);
        byte Blend(byte a, byte b) => (byte)Math.Round(a * factor + b * (1 - factor));
        return Color.FromRgb(Blend(accent.R, baseColor.R), Blend(accent.G, baseColor.G), Blend(accent.B, baseColor.B));
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

    /// <summary>判定鼠标"真实地"位于浮窗矩形内：
    /// 优先用 WPF 命中测试 IsMouseOver；浮窗为 NoActivate + Focusable=False，
    /// 在部分场景下 MouseEnter 可能不触发或被丢弃，因此再用 Win32 GetCursorPos+GetWindowRect 兜底，
    /// 保证 timeout 计时器决策不会因事件丢失而误关浮窗</summary>
    private bool IsMouseOverWindowReal()
    {
        if (IsMouseOver) return true;
        if (!NativeMethods.GetCursorPos(out var pt)) return false;
        return ContainsScreenPoint(pt.X, pt.Y);
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
        if (msg is NativeMethods.WM_SETTINGCHANGE
            or NativeMethods.WM_THEMECHANGED
            or NativeMethods.WM_DWMCOLORIZATIONCOLORCHANGED)
        {
            ApplyThemeMode(_themeMode, _followAccentColor);
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
        UpdateOverflowLayout();
        SelectIndex(ShouldAutoSelectFirstItem() ? 0 : -1);

        // 关键：必须 base.Show() 一次让 SizeToContent="WidthAndHeight" 真正生效。
        // Hidden 状态下的 UpdateLayout 不会更新 Window 的 ActualWidth/Height。
        // Left/Top 先预定位到屏幕外（-32000）让用户感知不到，立刻 SetWindowPos 移到正确位置。
        // ShowActivated=False 保证不抢焦点
        Left = -32000;
        Top = -32000;
        // 显示前先按 idle 透明度生效；鼠标进入浮窗后由 OnMouseEnterRestoreOpacity 切到 1.0
        Opacity = _idleOpacity;
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
        ScheduleTimeoutDismiss();
    }

    public bool TryHandleGlobalKey(KeyEvent key)
    {
        if (!_isShown || !key.IsDown) return false;
        try
        {
            return Dispatcher.Invoke(() => HandleGlobalKeyCore(key));
        }
        catch (Exception ex)
        {
            _log.Warn("toolbar key handling failed", ("err", ex.Message));
            return false;
        }
    }

    private bool HandleGlobalKeyCore(KeyEvent key)
    {
        if (!_isShown) return false;
        if (key.VirtualKey == NativeMethods.VK_ESCAPE)
        {
            if (!_dismissOnEscape) return false;
            HideToolbar("keyboard-esc");
            return true;
        }

        if (_keyboardShortcutsEnabled
            && _numberShortcutsEnabled
            && Items.Count > 0
            && key.VirtualKey is >= 0x31 and <= 0x39)
        {
            var index = key.VirtualKey - 0x31;
            if (index < Items.Count)
            {
                SelectIndex(index);
                Items[index].Invoke();
                return true;
            }
        }

        if (_keyboardShortcutsEnabled && Items.Count > 0 && key.VirtualKey is NativeMethods.VK_LEFT or NativeMethods.VK_UP)
        {
            MoveSelection(-1);
            return true;
        }
        if (_keyboardShortcutsEnabled && Items.Count > 0 && key.VirtualKey is NativeMethods.VK_RIGHT or NativeMethods.VK_DOWN)
        {
            MoveSelection(1);
            return true;
        }
        if (_keyboardShortcutsEnabled
            && _tabNavigationEnabled
            && Items.Count > 0
            && key.VirtualKey == NativeMethods.VK_TAB)
        {
            MoveSelection(key.Shift ? -1 : 1);
            return true;
        }
        if (_keyboardShortcutsEnabled && Items.Count > 0 && key.VirtualKey is NativeMethods.VK_RETURN or NativeMethods.VK_SPACE)
        {
            var index = _selectedIndex >= 0 ? _selectedIndex : 0;
            SelectIndex(index);
            Items[index].Invoke();
            return true;
        }

        if (!IsModifierOnlyKey(key.VirtualKey))
        {
            HideToolbar("keyboard-input");
        }

        return false;
    }

    private static bool IsModifierOnlyKey(int virtualKey)
    {
        const int VK_LMENU = 0xA4;
        const int VK_RMENU = 0xA5;
        const int VK_LWIN = 0x5B;
        const int VK_RWIN = 0x5C;

        return virtualKey is NativeMethods.VK_SHIFT
            or NativeMethods.VK_LSHIFT
            or NativeMethods.VK_RSHIFT
            or NativeMethods.VK_CONTROL
            or NativeMethods.VK_LCONTROL
            or NativeMethods.VK_RCONTROL
            or NativeMethods.VK_MENU
            or VK_LMENU
            or VK_RMENU
            or VK_LWIN
            or VK_RWIN;
    }

    private void MoveSelection(int delta)
    {
        if (Items.Count == 0) return;
        var current = _selectedIndex < 0 ? 0 : _selectedIndex;
        var next = (current + delta + Items.Count) % Items.Count;
        SelectIndex(next);
    }

    private void SelectIndex(int index)
    {
        _selectedIndex = index;
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].IsKeyboardSelected = i == index;
        }
    }

    private bool ShouldAutoSelectFirstItem()
        => _keyboardShortcutsEnabled && _tabNavigationEnabled && Items.Count > 0;

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateOverflowLayout();
            if (Items.Count == 0)
            {
                SelectIndex(-1);
            }
            else if (_selectedIndex >= 0)
            {
                SelectIndex(Math.Clamp(_selectedIndex, 0, Items.Count - 1));
            }
            else
            {
                SelectIndex(ShouldAutoSelectFirstItem() ? 0 : -1);
            }
        });
    }

    private void UpdateOverflowLayout()
    {
        if (Items.Count > _maxActionsPerRow)
        {
            ItemsHost.MaxWidth = _maxActionsPerRow * 112;
        }
        else
        {
            ItemsHost.ClearValue(MaxWidthProperty);
        }
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

    private void OnMouseEnterRestoreOpacity(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // 鼠标进入浮窗时永远以完全不透明显示，方便用户辨认与点击；离开后回落到 _idleOpacity
        Opacity = 1.0;
        // 鼠标停留在浮窗上视为用户在使用，暂停超时计时；离开后由 OnMouseLeave 重新调度
        _dismissTimeoutCts?.Cancel();
        _dismissTimeoutCts = null;
    }

    private void OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // 鼠标离开时立刻回落到 idle 透明度，让用户看清遮挡的背景内容
        Opacity = _idleOpacity;
        // 鼠标离开后重新开始超时计时（若用户开启了"浮窗超时后自动消失"）
        ScheduleTimeoutDismiss();

        // 鼠标离开后延迟自动消失，避免用户在边缘抖动时误关；用户也可在设置中关闭此触发
        if (!_dismissOnMouseLeave) return;
        var delay = _dismissMouseLeaveDelayMs > 0 ? _dismissMouseLeaveDelayMs : 1;
        Task.Delay(delay).ContinueWith(_ =>
        {
            Dispatcher.Invoke(() =>
            {
                // 同样用 Win32 物理坐标兜底，避免 MouseEnter 漏触发时鼠标仍在浮窗上却被关闭
                if (IsMouseOverWindowReal()) return;
                if (DateTime.UtcNow - _lastShownAtUtc < TimeSpan.FromMilliseconds(700)) return;
                HideToolbar("mouse-leave");
            });
        });
    }

    public void HideToolbar(string reason)
    {
        if (!_isShown) return;
        if (_hwnd == 0) return;
        _dismissTimeoutCts?.Cancel();
        _dismissTimeoutCts = null;
        // 用 WPF Hide() 而非 Win32 ShowWindow(SW_HIDE)，保证 Visibility 属性同步翻成 Hidden。
        // 否则下次 base.Show() 会因为 Visibility 仍是 Visible 而 no-op，SizeToContent 不再触发
        base.Hide();
        _isShown = false;
        HideToast();
        Dismissed?.Invoke();
        _log.Debug("toolbar dismissed", ("reason", reason));
    }

    /// <summary>外部代码触发关闭（前台窗口变化、Esc、新选区等）</summary>
    public void DismissExternal(string reason) => Dispatcher.Invoke(() => HideToolbar(reason));

    public void Notify(string text) => ShowInlineToast(text);

    public void ShowInlineToast(string text, bool isError = false, string? copyText = null, int durationMs = 1800)
    {
        Dispatcher.Invoke(() =>
        {
            LastToastAtUtc = DateTime.UtcNow;
            _toastCts?.Cancel();
            _toastCts = new CancellationTokenSource();
            _toastCopyText = copyText;
            ToastText.Text = text;
            ToastCopyButton.Visibility = string.IsNullOrEmpty(copyText) ? Visibility.Collapsed : Visibility.Visible;
            ToastHost.Visibility = Visibility.Visible;
            if (isError)
            {
                ToastHost.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5A, 0x1E, 0x1E));
            }
            else
            {
                if (TryFindResource("ToolbarToastBackground") is Brush brush) ToastHost.Background = brush;
            }

            var cts = _toastCts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(durationMs, cts.Token).ConfigureAwait(false);
                    Dispatcher.Invoke(() =>
                    {
                        if (cts.IsCancellationRequested) return;
                        ToastHost.Visibility = Visibility.Collapsed;
                    });
                }
                catch (OperationCanceledException) { }
            });
        });
    }

    private void HideToast()
    {
        _toastCts?.Cancel();
        ToastHost.Visibility = Visibility.Collapsed;
        _toastCopyText = null;
    }

    private void ScheduleTimeoutDismiss()
    {
        _dismissTimeoutCts?.Cancel();
        _dismissTimeoutCts = null;
        if (!_dismissOnTimeout) return;

        var delayMs = _dismissTimeoutMs > 0 ? _dismissTimeoutMs : 3000;
        var cts = new CancellationTokenSource();
        _dismissTimeoutCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                // 轮询风格：到期时若鼠标仍停留在浮窗上，进入下一轮等待，不关闭浮窗；
                // 直到一次到期时鼠标已离开浮窗（Win32 物理坐标判定），才真正 HideToolbar。
                // 这样语义稳定：只要鼠标在浮窗上，超时永远不触发；不依赖 WPF MouseEnter/MouseLeave 路由事件，
                // 规避 NoActivate + Focusable=False 浮窗在某些路径下事件丢失导致的"鼠标在浮窗上仍被超时关闭"问题
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(delayMs, cts.Token).ConfigureAwait(false);

                    var shouldHide = false;
                    Dispatcher.Invoke(() =>
                    {
                        if (cts.IsCancellationRequested || !_isShown) return;
                        shouldHide = !IsMouseOverWindowReal();
                    });
                    if (!shouldHide)
                    {
                        _log.Debug("toolbar timeout postponed: mouse still over");
                        continue;
                    }
                    Dispatcher.Invoke(() =>
                    {
                        if (cts.IsCancellationRequested || !_isShown) return;
                        HideToolbar("timeout");
                    });
                    return;
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private void OnCopyToastClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_toastCopyText)) return;
        try
        {
            System.Windows.Clipboard.SetText(_toastCopyText);
            ToastText.Text = "错误信息已复制";
            ToastCopyButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _log.Warn("copy toast failed", ("err", ex.Message));
        }
    }
}
