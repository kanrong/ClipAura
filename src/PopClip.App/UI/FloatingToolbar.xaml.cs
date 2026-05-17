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
    /// <summary>权威集合：按用户在动作页配置的顺序排，键盘导航 / Tab / 数字快捷键都基于它做 index 查找。
    /// 即使布局模式把按钮拆到多行，Items 顺序仍是单一真理源</summary>
    public ObservableCollection<ToolbarItem> Items { get; } = new();
    /// <summary>渲染用集合：每个 Row 是一行按钮。
    /// 单行模式下 Rows.Count == 1（row.Items 等于 Items）；
    /// 多行模式下 Rows 按布局策略拆分 Items 到 ≥2 个 row</summary>
    public ObservableCollection<ToolbarItemRow> Rows { get; } = new();

    private nint _hwnd;
    private readonly ILog _log;
    private DateTime _lastShownAtUtc;
    private bool _prewarmed;
    private bool _isShown;
    private bool _isIconVisible = true;
    private bool _isTextVisible = true;
    private int _selectedIndex = -1;
    private int _maxActionsPerRow = 6;
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
    private CancellationTokenSource? _dismissTimeoutCts;
    private ToolbarToastWindow? _toastWindow;
    /// <summary>窗口外缘留给阴影扩散的 DIP 数。
    /// 与 FloatingToolbar.xaml 中 Grid.Margin 严格一致（同步改），
    /// 决定阴影最大可见 BlurRadius+ShadowDepth；过小阴影边缘会被透明窗口裁切，过大会增加屏占用</summary>
    private const int ShadowPaddingDip = 12;

    /// <summary>窗口尺寸 = ContentClipHost.DesiredSize + 这个值。
    /// 必须 = ShadowPaddingDip * 2（左右 / 上下两侧 Margin 之和），
    /// 否则 SizeToContent 算出的 window 比真实内容小，右侧按钮会被透明边裁切</summary>
    private const int OuterMarginDip = ShadowPaddingDip * 2;
    /// <summary>Standard 密度下按钮的左右内边距。
    /// Compact / Comfortable 由 PaddingForDensity 派生</summary>
    private const double ToolbarButtonPaddingX = 12;

    /// <summary>Standard 密度下按钮的上下内边距</summary>
    private const double ToolbarButtonPaddingY = 9;
    // 外圆角半径，与阴影底板/描边层的 CornerRadius 完全一致；
    // ContentClipHost 用它作 Clip 圆角，按钮 hover 颜色直接铺满到外圆角弧线，
    // 描边层（最上层）再把外圆角描边盖住 hover 边缘的抗锯齿过渡像素
    private double _outerCornerRadius;

    public event Action? Dismissed;
    /// <summary>系统主题/强调色变化时触发，由 AppHost 转给 ThemeManager 重新应用全局主题。
    /// 浮窗 WndProc 是系统消息最容易接到的入口，因此用它做信号源，而不在内部直接动主题资源</summary>
    public event Action? SystemThemeChanged;
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

    /// <summary>用 items 替换 Items，并按 layoutMode 重建 Rows。
    /// 替代外部直接 Items.Clear / Items.Add 的旧模式：
    /// - 保证 Items（键盘导航源）与 Rows（渲染源）同步更新
    /// - 按"≥2 组可见才换行"的规则自动降级，避免单一类别浮窗变成"高瘦"形状</summary>
    public void ApplyItems(IEnumerable<ToolbarItem> items, ToolbarLayoutMode layoutMode = ToolbarLayoutMode.Single)
    {
        Items.Clear();
        foreach (var it in items) Items.Add(it);
        RebuildRows(layoutMode);
    }

    private void RebuildRows(ToolbarLayoutMode mode)
    {
        Rows.Clear();
        var basic = Items.Where(i => i.Group == ToolbarItemGroup.Basic).ToList();
        var smart = Items.Where(i => i.Group == ToolbarItemGroup.Smart).ToList();
        var ai = Items.Where(i => i.Group == ToolbarItemGroup.Ai).ToList();
        var visibleGroupCount = (basic.Count > 0 ? 1 : 0) + (smart.Count > 0 ? 1 : 0) + (ai.Count > 0 ? 1 : 0);

        switch (mode)
        {
            case ToolbarLayoutMode.SmartOnSeparateRow when smart.Count > 0 && (basic.Count + ai.Count) > 0:
                // 第一行：Basic + AI（按 Items 原序，保留用户配置顺序的可读性）
                AddRow(Items.Where(i => i.Group != ToolbarItemGroup.Smart));
                AddRow(smart);
                break;
            case ToolbarLayoutMode.GroupRows when visibleGroupCount >= 2:
                if (basic.Count > 0) AddRow(basic);
                if (smart.Count > 0) AddRow(smart);
                if (ai.Count > 0) AddRow(ai);
                break;
            default:
                // 单行模式 或 多行模式下"只有一类可见"：仍按原序紧凑单行
                AddRow(Items);
                break;
        }
    }

    private void AddRow(IEnumerable<ToolbarItem> items)
    {
        var row = new ToolbarItemRow();
        foreach (var i in items) row.Items.Add(i);
        Rows.Add(row);
    }

    public void ApplyAppearance(AppSettings settings)
    {
        ApplyDisplayMode(settings.ToolbarDisplay);
        // 主题与字体由 ThemeManager 统一写到 Application.Resources，浮窗的 DynamicResource 自动跟随；
        // 这里不重复写本地资源字典，避免覆盖全局主题
        ApplySurfaceStyle(settings.ToolbarSurface);
        var (padX, padY) = PaddingForDensity(settings.ToolbarDensity);
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
            Resources["ToolbarButtonPadding"] = new Thickness(padX, padY, padX, padY);
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

    // 阴影参数策略：加深 Opacity 让"浮起"立体感更明显；
    // 总预算 BlurRadius + ShadowDepth 不超过 ShadowPaddingDip(=12) 避免被透明窗口边界裁切
    private void ApplySurfaceStyle(ToolbarSurfaceStyle style)
    {
        Dispatcher.Invoke(() =>
        {
            switch (style)
            {
                case ToolbarSurfaceStyle.Shadow:
                    // 纯阴影：放宽 BlurRadius + 提高 Opacity，叠出明显"浮起"立体感
                    Resources["ToolbarBorderThickness"] = new Thickness(0);
                    Resources["ToolbarShadowBlurRadius"] = 10d;
                    Resources["ToolbarShadowDepth"] = 3d;
                    Resources["ToolbarShadowOpacity"] = 0.55d;
                    break;
                case ToolbarSurfaceStyle.Border:
                    // 纯细边框：去掉阴影完全避免裁切；边框颜色由 ToolbarBorder 主题切换
                    Resources["ToolbarBorderThickness"] = new Thickness(1);
                    Resources["ToolbarShadowBlurRadius"] = 0d;
                    Resources["ToolbarShadowDepth"] = 0d;
                    Resources["ToolbarShadowOpacity"] = 0d;
                    break;
                default:
                    // ShadowAndBorder：阴影更深一档，细边框 + 较重阴影构成"漂浮卡片"感
                    Resources["ToolbarBorderThickness"] = new Thickness(1);
                    Resources["ToolbarShadowBlurRadius"] = 8d;
                    Resources["ToolbarShadowDepth"] = 2d;
                    Resources["ToolbarShadowOpacity"] = 0.42d;
                    break;
            }
        });
    }

    /// <summary>把密度档位映射成按钮的左右/上下内边距。
    /// Standard 与早期硬编码一致 (12,9)，向 Compact (8,5) / Comfortable (16,13) 两侧线性放缩</summary>
    private static (double X, double Y) PaddingForDensity(ToolbarDensity density) => density switch
    {
        ToolbarDensity.Compact => (8, 5),
        ToolbarDensity.Comfortable => (16, 13),
        _ => (ToolbarButtonPaddingX, ToolbarButtonPaddingY),
    };

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
            SystemThemeChanged?.Invoke();
        }
        return 0;
    }

    /// <summary>在选区附近以"不抢焦点"的方式显示工具栏。
    /// anchorRect 由 UIA (TextPatternRange.GetBoundingRectangles) 或鼠标坐标产生，
    /// 单位均为物理像素 (device pixel)。本方法把 toolbar 的 DIP 尺寸换算到 anchor 所在 monitor 的
    /// 物理像素，再用物理像素做边界约束 + SetWindowPos，避免 DIP/PX 混用偏移</summary>
    public void ShowAt(SelectionRect anchorRect, ForegroundWindowInfo foreground)
    {
        // 新选区出现先关掉上一次残留的 toast：旧 toast 位置是基于上次浮窗算的，
        // 新浮窗位置不同的话会让旧 toast 飘在屏幕奇怪位置或盖在新浮窗上
        HideToast();
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
        InvalidateToolbarMeasure();
        SizeToContent = SizeToContent.WidthAndHeight;
        base.Show();
        UpdateLayout();

        var (widthDip, heightDip) = GetPreferredWindowSizeDip();
        if (widthDip <= 10 || heightDip <= 10)
        {
            _log.Warn("toolbar size suspiciously small",
                ("w", widthDip), ("h", heightDip),
                ("items", Items.Count));
            // 即便很小也尝试显示，至少能看到痕迹便于排错
        }

        // 先让 WPF 用 SizeToContent 完成一次"测量"，再切到手动尺寸锁住最终窗口宽高。
        // 首次显示时否则会出现 native SetWindowPos 已传入 94px，但 WPF 又把旧的 136px 自动尺寸写回 HWND。
        SizeToContent = SizeToContent.Manual;
        Width = widthDip;
        Height = heightDip;
        UpdateLayout();

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

        // 显式写回当前测量尺寸，避免首次显示沿用 HWND 创建/预热阶段的旧尺寸
        WindowStyleHelper.ShowNoActivate(_hwnd, x, y, widthPx, heightPx);
        _isShown = true;
        _lastShownAtUtc = DateTime.UtcNow;
        ScheduleTimeoutDismiss();
    }

    private (double WidthDip, double HeightDip) GetPreferredWindowSizeDip()
    {
        // 首次显示时 Window/Root Grid 偶尔会把阴影层的 effect 外扩算进 Desired/Actual，
        // 导致宽度像多出一个按钮位。定位尺寸改为直接取内容层期望尺寸 + 外层固定 margin，
        // 让窗口大小只由真实按钮/Toast 内容决定。
        var contentWidth = ContentClipHost.DesiredSize.Width > 0
            ? ContentClipHost.DesiredSize.Width
            : ContentClipHost.ActualWidth;
        var contentHeight = ContentClipHost.DesiredSize.Height > 0
            ? ContentClipHost.DesiredSize.Height
            : ContentClipHost.ActualHeight;

        return (contentWidth + OuterMarginDip, contentHeight + OuterMarginDip);
    }

    private void InvalidateToolbarMeasure()
    {
        ClearValue(WidthProperty);
        ClearValue(HeightProperty);
        RowsHost.ClearValue(WidthProperty);
        RowsHost.InvalidateMeasure();
        RowsHost.InvalidateArrange();
        ContentClipHost.InvalidateMeasure();
        ContentClipHost.InvalidateArrange();
        InvalidateMeasure();
        InvalidateArrange();
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

        // 键盘选择（导航 + 触发）由 _tabNavigationEnabled 统一控制：
        // Tab / 方向键 / Enter / 空格 共同构成"键盘选中并触发"的交互；用户关掉这一开关时浮窗完全不响应键盘选择
        var navEnabled = _keyboardShortcutsEnabled && _tabNavigationEnabled && Items.Count > 0;
        if (navEnabled && key.VirtualKey is NativeMethods.VK_LEFT or NativeMethods.VK_UP)
        {
            MoveSelection(-1);
            return true;
        }
        if (navEnabled && key.VirtualKey is NativeMethods.VK_RIGHT or NativeMethods.VK_DOWN)
        {
            MoveSelection(1);
            return true;
        }
        if (navEnabled && key.VirtualKey == NativeMethods.VK_TAB)
        {
            MoveSelection(key.Shift ? -1 : 1);
            return true;
        }
        if (navEnabled && key.VirtualKey is NativeMethods.VK_RETURN or NativeMethods.VK_SPACE)
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

    /// <summary>是否在显示浮窗时自动用键盘高亮第一项。
    /// 仅当用户开启了"键盘选择"（_tabNavigationEnabled）时才有意义；关闭时浮窗以纯鼠标方式呈现，
    /// 没有键盘焦点高亮也避免误导用户按方向键无效</summary>
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
        // 单行模式下用 Items.Count；多行模式下取每行的最大按钮数，
        // 整体宽度限制要让"最长的那一行"也别超 _maxActionsPerRow
        var maxItemsInARow = 0;
        foreach (var row in Rows)
        {
            if (row.Items.Count > maxItemsInARow) maxItemsInARow = row.Items.Count;
        }
        if (maxItemsInARow == 0) maxItemsInARow = Items.Count;
        if (maxItemsInARow <= _maxActionsPerRow)
        {
            RowsHost.ClearValue(MaxWidthProperty);
            return;
        }

        // 强制 measure 让外层 RowsHost 的 container 生成；下钻视觉树找第一个真实的按钮 DesiredSize
        RowsHost.ApplyTemplate();
        RowsHost.UpdateLayout();

        double maxItemDesiredWidth = MeasureMaxButtonWidth();
        // 兜底（首帧 container 未生成时）；旧硬编码 112 在纵向布局下偏大，统一缩到 96
        if (maxItemDesiredWidth < 1) maxItemDesiredWidth = 96;
        // +1 抵消舍入；按"最宽 button * N"作为 MaxWidth，短按钮排得下时一行 ≥ N，
        // 真正超出 _maxActionsPerRow 的情形 WrapPanel 自动换到下一行
        RowsHost.MaxWidth = maxItemDesiredWidth * _maxActionsPerRow + 1;
    }

    /// <summary>下钻 RowsHost 视觉树找所有 Button.DesiredSize.Width 的最大值。
    /// 两层 ItemsControl 架构（外层 Rows 容器 → 内层按钮容器）让 ItemContainerGenerator 不再直接给到按钮，
    /// 此处用 VisualTreeHelper 递归查找</summary>
    private double MeasureMaxButtonWidth()
    {
        double max = 0;
        FindButtons(RowsHost);
        return max;

        void FindButtons(DependencyObject parent)
        {
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is System.Windows.Controls.Button btn)
                {
                    if (btn.DesiredSize.Width > max) max = btn.DesiredSize.Width;
                }
                FindButtons(child);
            }
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
        // 故意不在这里 HideToast：DismissOnActionInvoked=true 时浮窗会在动作完成后 700ms 自关，
        // 但用户期望"复制完看到提示"——让 toast 自己计时 1.8s 后再关，体验更自然。
        // 旧 toast 在下一次 ShowAt 入口处会被强制清掉（避免新浮窗位置变化时旧 toast 残留）
        Dismissed?.Invoke();
        _log.Debug("toolbar dismissed", ("reason", reason));
    }

    /// <summary>窗口彻底关闭（程序退出等）时连同 toast 一起释放，避免 Owner 已销毁后 toast 还驻留</summary>
    protected override void OnClosed(EventArgs e)
    {
        _toastWindow?.HideNow();
        _toastWindow?.Close();
        _toastWindow = null;
        base.OnClosed(e);
    }

    /// <summary>外部代码触发关闭（前台窗口变化、Esc、新选区等）</summary>
    public void DismissExternal(string reason) => Dispatcher.Invoke(() => HideToolbar(reason));

    public void Notify(string text) => ShowInlineToast(text);

    /// <summary>把提示交给独立 ToolbarToastWindow 显示，避免内嵌 Toast 与浮窗 SizeToContent 冲突，
    /// 也允许浮窗 700ms 关闭后提示自己续命到 durationMs 结束</summary>
    public void ShowInlineToast(string text, bool isError = false, string? copyText = null, int durationMs = 1800)
    {
        Dispatcher.Invoke(() =>
        {
            LastToastAtUtc = DateTime.UtcNow;
            EnsureToastWindow();
            // anchor 取浮窗水平中心、内容下沿（Window.Top + Height - ShadowPaddingDip）；
            // 浮窗外缘有 ShadowPaddingDip 的透明阴影留白，扣掉之后 toast 才贴近"用户能看到的浮窗底"
            var anchorCenterX = Left + Width / 2;
            var anchorTopY = Top + Height - ShadowPaddingDip;
            _toastWindow!.Show(text, copyText, isError, durationMs, anchorCenterX, anchorTopY);
        });
    }

    /// <summary>独立锚点 toast：当浮窗本身不会显示（OCR 识别失败 / 加载中）时使用，
    /// 直接给屏幕物理像素坐标做锚点。anchor 是 toast 的水平中心 + 顶部位置（DIP）。
    /// 调用方负责物理像素 → DIP 的换算。</summary>
    public void ShowToastAt(string text, double anchorCenterDip, double anchorTopDip,
        bool isError = false, int durationMs = 3000)
    {
        Dispatcher.Invoke(() =>
        {
            LastToastAtUtc = DateTime.UtcNow;
            EnsureToastWindow();
            _toastWindow!.Show(text, copyText: null, isError, durationMs, anchorCenterDip, anchorTopDip);
        });
    }

    private void EnsureToastWindow()
    {
        if (_toastWindow is not null) return;
        _toastWindow = new ToolbarToastWindow(_log);
        // 浮窗 Owner 关闭时 toast 也跟着销毁；但 toast 自己的 durationMs 计时由它自己管，浮窗 Hide 不会强关 toast
        _toastWindow.Owner = this;
    }

    private void HideToast() => _toastWindow?.HideNow();

    /// <summary>计算 AI 内联气泡的锚点：浮窗下沿水平居中点，单位 DIP。
    /// 同时返回该浮窗所在 monitor 的工作区上下沿 DIP 值，让气泡能在屏幕底部空间不足时翻到上方。
    /// 浮窗尚未显示时返回 null —— 此时 AiTextService 应回退到现有 toast/notify 路径</summary>
    public BubbleAnchor? GetCurrentBubbleAnchor()
    {
        if (!_isShown || _hwnd == 0) return null;
        var centerXDip = Left + Width / 2;
        // ShadowPaddingDip 是浮窗外缘留给阴影的透明 padding；扣掉它才能贴近"用户能看到的浮窗底"
        var topYDip = Top + Height - ShadowPaddingDip;

        if (NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            var monitor = MonitorQuery.FromRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
            // 物理像素 → DIP：除以该 monitor 的 DPI 缩放比
            var monitorBottomDip = monitor.WorkBottom * 96.0 / Math.Max(monitor.DpiY, 96);
            var monitorTopDip = monitor.WorkTop * 96.0 / Math.Max(monitor.DpiY, 96);
            return new BubbleAnchor(centerXDip, topYDip, monitorBottomDip, monitorTopDip);
        }
        // 取不到屏幕边界时仍给坐标，让气泡显示出来即可（极端情况，不阻断主流程）
        return new BubbleAnchor(centerXDip, topYDip, double.PositiveInfinity, 0);
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
            // 轮询风格：到期时若鼠标仍停留在浮窗上，进入下一轮等待，不关闭浮窗；
            // 直到一次到期时鼠标已离开浮窗（Win32 物理坐标判定），才真正 HideToolbar。
            // 这样语义稳定：只要鼠标在浮窗上，超时永远不触发；不依赖 WPF MouseEnter/MouseLeave 路由事件，
            // 规避 NoActivate + Focusable=False 浮窗在某些路径下事件丢失导致的"鼠标在浮窗上仍被超时关闭"问题。
            //
            // 取消语义：不把 cts.Token 传给 Task.Delay —— 那样每次 ScheduleTimeoutDismiss 替换旧 timer 都会
            // 让正在 await 的 Task.Delay 抛 TaskCanceledException，造成 IDE 输出窗口的 first-chance exception 干扰。
            // 改成"每轮 delay 后查一次 IsCancellationRequested"：cancel 后最多多等一个 delayMs 才真正退出，
            // 但此时 _isShown / IsCancellationRequested 都拦得住 HideToolbar，没有任何用户可见副作用
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                if (cts.IsCancellationRequested) return;

                var shouldHide = false;
                Dispatcher.Invoke(() =>
                {
                    if (cts.IsCancellationRequested || !_isShown) return;
                    shouldHide = !IsMouseOverWindowReal();
                });
                if (!shouldHide)
                {
                    continue;
                }
                Dispatcher.Invoke(() =>
                {
                    if (cts.IsCancellationRequested || !_isShown) return;
                    HideToolbar("timeout");
                });
                return;
            }
        });
    }

}
