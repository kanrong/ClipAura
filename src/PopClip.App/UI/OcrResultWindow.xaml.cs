using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using PopClip.App.Config;
using PopClip.App.Ocr;
using PopClip.App.Services;
using PopClip.Core.Logging;
using PopClip.Hooks.Interop;
using PopClip.Hooks.Window;
using WpfPoint = System.Windows.Point;
using WinRectangle = System.Drawing.Rectangle;

namespace PopClip.App.UI;

/// <summary>iOS 风格 OCR 结果窗：原截图作背景 + 每段文字一个可点选的高亮多边形。
///
/// 交互定义（与 iOS 长按图片识字尽可能贴近，受 block-level 检测限制不到字符级）：
/// - 单击高亮 → 选中该 block，复制其文本到剪贴板
/// - Ctrl/Shift + 单击 → 加入 / 切换选中
/// - 空白处按下并拖动 → marquee 框选
/// - Ctrl+A 全选 / Ctrl+C 复制 / Ctrl+D 清除 / ESC 关闭
/// - 右键 → ContextMenu（复制 / 翻译 / 全选 / Quick / 关闭）
/// - 翻译：调 AiTextService.TranslateBatchAsync，inline 渲染到 polygon 上方
///
/// 工具条独立成 OcrResultToolbarWindow 子窗口（透明 Topmost，可拖到屏幕任意位置），
/// 主窗只持有结果数据与状态，工具条只做显示 + 按钮转发 → Command* 回调本窗。
///
/// 颜色 / 字体跟随 ToolbarThemeMode（所有 chrome 用 DynamicResource 绑定 Toolbar* 资源）。
/// 边框模式（settings.OcrResultWindowBordered=true）：WindowBorderFrame.BorderThickness=2，
/// 不引入标题栏 / 缩放角，纯粹给截图四周一圈视觉边界。</summary>
internal partial class OcrResultWindow : Window
{
    private readonly ILog _log;
    private readonly OcrResult _result;
    private readonly ClipboardWriter _clipboard;
    private readonly AppSettings _settings;
    private readonly AiTextService? _aiText;
    private readonly Action? _onCloseRequested;
    private readonly string _layoutFullText;

    /// <summary>用户在结果窗点"Quick 输出"时调用：交给 Coordinator 走一遍 Quick 模式渲染
    /// （剪贴板 + 浮窗气泡 + toast），不修改 settings.OcrResultMode。
    /// 用户的 OCR 模式偏好通过设置面板永久切换，本按钮只影响当次输出</summary>
    private readonly Action<string>? _quickFallback;

    /// <summary>"打开设置某一分页"的跨层回调（如 "AI" / "Ocr"）。
    /// 为空时跳转入口降级为 Toast 提示，避免空引用</summary>
    private readonly Action<string>? _openSettings;

    /// <summary>边框宽度（DIP）。bordered=true 时为 2.0，否则 0.0。
    /// 影响：1) WindowBorderFrame.BorderThickness；2) Window 总尺寸要扩出 2 * borderWidth
    /// 让里面的 Image / Grid 区域仍与原截图等大，保证 Stretch="Fill" 时 1:1 像素映射、文字不糊</summary>
    private readonly double _borderWidth;

    /// <summary>OCR 截图框的物理像素矩形（即 anchor monitor 上的真实像素坐标）。
    /// 用 Win32 SetWindowPos 直接按物理像素定位窗口，规避 WPF Window.Left/Top 在 PerMonitor V2 下
    /// 跨屏异构 DPI 时被错算到主屏的老问题（与 FloatingToolbar / ToolbarToastWindow 同一策略）</summary>
    private readonly WinRectangle _anchorPhysical;

    /// <summary>anchor 所在 monitor 的 effective DPI（用 MonitorQuery.FromRect 取得）。
    /// DIP 与物理像素的换算基准；分 X / Y 以兼容显示器横竖 DPI 不同的极少数情况</summary>
    private readonly uint _anchorDpiX;
    private readonly uint _anchorDpiY;

    private OcrResultToolbarWindow? _toolbarWindow;
    private OcrResultTextPanelWindow? _textPanelWindow;

    /// <summary>每个 block 对应一个 Polygon overlay。
    /// 顺序与 _result.Blocks 一致，下标可双向查询。</summary>
    private readonly List<Polygon> _polygons = new();

    /// <summary>每个 block 对应的"译文覆盖框"。null 表示该 block 还没翻译。
    /// 顺序与 _result.Blocks 一致。</summary>
    private readonly Border?[] _translationOverlays;

    /// <summary>当前 selected 状态：bitmap-like 集合，下标对应 _result.Blocks。
    /// 改成 HashSet&lt;int&gt; 也 OK，但 block 一般不超过 100 个，bool[] 更直接。</summary>
    private bool[] _selected;

    /// <summary>当前 hover 中的 polygon 下标，没有时 -1。
    /// 切换 hover 时需要先把旧的 polygon 还原成 default 颜色</summary>
    private int _hoverIndex = -1;

    /// <summary>Marquee 拖动状态：起点在 OverlayCanvas 坐标系（DIP），按下后即记录。
    /// 取消条件：松开鼠标 / 移出窗口 / Escape。</summary>
    private WpfPoint? _marqueeStart;
    private bool _isDragging;

    /// <summary>Toast 自动隐藏的定时器，每次 ShowToast 都重置；窗口关闭时一并 Stop</summary>
    private readonly DispatcherTimer _toastTimer;

    /// <summary>翻译期间禁用相关按钮 / 显示 loading 文案的标志位</summary>
    private bool _translating;

    public OcrResultWindow(
        ILog log,
        OcrResult result,
        byte[] screenshotPng,
        WinRectangle anchorPhysical,
        uint anchorDpiX,
        uint anchorDpiY,
        ClipboardWriter clipboard,
        AppSettings settings,
        AiTextService? aiText,
        string? layoutFullText = null,
        Action<string>? quickFallback = null,
        Action<string>? openSettings = null,
        Action? onCloseRequested = null)
    {
        _log = log;
        _result = result;
        _clipboard = clipboard;
        _settings = settings;
        _aiText = aiText;
        _layoutFullText = string.IsNullOrWhiteSpace(layoutFullText) ? "" : layoutFullText.Trim();
        _quickFallback = quickFallback;
        _openSettings = openSettings;
        _onCloseRequested = onCloseRequested;
        _anchorPhysical = anchorPhysical;
        _anchorDpiX = anchorDpiX == 0 ? 96 : anchorDpiX;
        _anchorDpiY = anchorDpiY == 0 ? 96 : anchorDpiY;
        _selected = new bool[result.Blocks.Count];
        _translationOverlays = new Border?[result.Blocks.Count];

        InitializeComponent();

        // 边框模式：四周加 2px 主题色边框；无边模式保持 0。
        // 关键：Window 总尺寸要扩出 borderWidth * 2，让里面的 Grid/Image 区域仍等于原截图大小，
        // 否则 Stretch="Fill" 把截图缩放到比截图稍小的 Image 区域，文字像素错位 → 模糊。
        // 同时窗口位置往左上偏 borderWidth，让 Image 区域的中心仍贴回原截图位置
        _borderWidth = _settings.OcrResultWindowBordered ? 2.0 : 0.0;
        WindowBorderFrame.BorderThickness = new Thickness(_borderWidth);

        // 初始把窗口放到屏幕外（主屏负坐标），先让 WPF 完成 layout 测量；
        // Loaded 阶段再用 Win32 SetWindowPos 把窗口精确定位到 anchor 物理像素位置。
        // 不直接用目标 DIP 设置 Left/Top —— 跨屏异构 DPI 时 WPF Window.Left/Top 会按主屏 DPI 解读，
        // 导致副屏框选的结果窗飘回主屏（与 FloatingToolbar / ToolbarToast 走 Win32 SetWindowPos 同因同治）
        double dipScaleX = _anchorDpiX / 96.0;
        double dipScaleY = _anchorDpiY / 96.0;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        Width = Math.Max(120, anchorPhysical.Width / dipScaleX + _borderWidth * 2);
        Height = Math.Max(80, anchorPhysical.Height / dipScaleY + _borderWidth * 2);

        // 加载截图作背景：用 BitmapImage 而不是 BitmapDecoder，让 WPF 自己处理像素格式
        ScreenshotImage.Source = LoadBitmap(screenshotPng);

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBorder.Visibility = Visibility.Collapsed;
        };

        Loaded += OnWindowLoaded;
        KeyDown += OnKeyDown;
        Closed += OnWindowClosed;

        // OverlayCanvas 接管所有 mouse 事件：polygon 命中走 Polygon 的 OnPolygonXxx，
        // 命中空白处走 OnOverlayMouseDown 启动 marquee。
        // 关键：必须用 MouseLeftButtonDown / MouseLeftButtonUp 而不是 MouseDown / MouseUp，
        // 因为 Polygon.MouseLeftButtonDown 里 e.Handled=true 只阻断同名事件冒泡，
        // 不会阻断父级的通用 MouseDown 监听，否则点 polygon 会同时启动 marquee 拖动
        OverlayCanvas.MouseLeftButtonDown += OnOverlayMouseDown;
        OverlayCanvas.MouseMove += OnOverlayMouseMove;
        OverlayCanvas.MouseLeftButtonUp += OnOverlayMouseUp;
        OverlayCanvas.MouseLeave += OnOverlayMouseLeave;
    }

    private static BitmapSource LoadBitmap(byte[] png)
    {
        using var ms = new MemoryStream(png);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;  // 立即加载，安全释放 MemoryStream
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // 第一步：用 Win32 SetWindowPos 把窗口精确定位到 anchor 物理像素位置。
        // WPF Window.Show 期间会按 Left=-32000 把窗口放到屏幕外，Loaded 时再次覆盖到 anchor monitor 上。
        // 紧接着 UpdateLayout 让 WPF 在新 DPI 下重算 ActualWidth/ActualHeight，
        // 后续 BuildPolygons 的缩放系数 = innerSize / SourceWidth 才是当前屏 DPI 下的真实 DIP 比例
        PlaceWindowAtAnchorPhysical();
        UpdateLayout();

        BuildPolygons();

        // 工具条窗口在主窗 Loaded 之后再 Show：此时主窗 Left/Top/ActualWidth/ActualHeight 都是稳定值，
        // 可以根据主窗位置算出工具条初始位置。
        // base.Show 前先把工具条挪到屏幕外（-32000），避免 Show 期间在 OS 默认位置短暂闪现
        var aiAvailable = _aiText is not null && _aiText.CanRun;
        _toolbarWindow = new OcrResultToolbarWindow(this, aiAvailable);
        _toolbarWindow.Left = -32000;
        _toolbarWindow.Top = -32000;
        // 工具条 SizeToContent=WidthAndHeight，必须先 Show 一次让它测出 ActualWidth/Height
        _toolbarWindow.Show();
        PositionToolbarInitially();

        // 文本面板默认开启：让用户立刻看到整段识别文本，弥补 Interactive 模式
        // "需要逐段点击 / 框选才能凑齐全文"的可读性短板
        OpenTextPanel(initial: true);

        UpdateStatusBar();
        // 让主窗拿到键盘焦点，否则 Ctrl+A / ESC 都收不到。
        // 工具条 ShowActivated=False 不会抢主窗焦点
        Focus();
        Keyboard.Focus(this);
        Activate();
    }

    /// <summary>用 Win32 SetWindowPos 把窗口物理像素绝对定位到 anchor 截图区域上方。
    /// 必须在 Loaded 之后调（WPF Show 内部会先按 Window.Left=-32000 摆放一次窗口）。
    /// SWP_NOACTIVATE 表示本次不抢焦点 —— 激活由后续 Activate() 处理，避免与 WPF Show 流程的激活竞争。
    ///
    /// 跨屏异构 DPI 下用此路径替代 WPF Window.Left/Top 的根本原因：
    /// PerMonitor V2 模式下 WPF 把 Window.Left/Top 解读为"基于当前所在 monitor 的 DPI"的 DIP，
    /// 但窗口在 Show 前并不知道自己会落在哪个 monitor —— 早期解读用主屏 DPI，导致副屏框选时
    /// 算出的 DIP 值再被解读时落到主屏物理像素位置，飘回主屏</summary>
    private void PlaceWindowAtAnchorPhysical()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;

        int borderPxX = (int)Math.Round(_borderWidth * _anchorDpiX / 96.0);
        int borderPxY = (int)Math.Round(_borderWidth * _anchorDpiY / 96.0);
        int x = _anchorPhysical.Left - borderPxX;
        int y = _anchorPhysical.Top - borderPxY;
        int w = _anchorPhysical.Width + borderPxX * 2;
        int h = _anchorPhysical.Height + borderPxY * 2;

        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            x, y, Math.Max(1, w), Math.Max(1, h),
            NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>把工具条窗口放到主窗下方居中。
    /// 如果主窗下方超出 anchor monitor 工作区（截图贴屏底）则改放到主窗内部底部居中，避免工具条飘出屏幕看不见。
    /// 用户拖动后自负其责，后续位置不再受约束（独立 Window 可跨屏任意拖）。
    ///
    /// 跨屏跑回主屏的根因：WPF Window.Left/Top setter 在 PerMonitor V2 下用"窗口当前所在屏 DPI"做 DIP→物理像素换算，
    /// 而工具条 base.Show() 时常驻主屏（96 DPI），主窗已在副屏（144 DPI），同一个 DIP 数值在两套 DPI 下指向不同物理像素，
    /// 工具条被推到主屏对应物理像素位置。修复路径：直接用 Win32 SetWindowPos 物理像素绝对定位工具条，
    /// 与 FloatingToolbar / OcrResultWindow 走同一条多屏正确路径</summary>
    private void PositionToolbarInitially()
    {
        if (_toolbarWindow is null) return;
        var tb = _toolbarWindow;

        var tbHwnd = new WindowInteropHelper(tb).Handle;
        if (tbHwnd == IntPtr.Zero)
        {
            // 极少数情况：工具条 hwnd 还没创建好。回退到 WPF DIP 定位（旧路径，跨屏可能偏，至少能看见）
            var fallbackTbW = tb.ActualWidth > 0 ? tb.ActualWidth : 400;
            var fallbackTbH = tb.ActualHeight > 0 ? tb.ActualHeight : 44;
            tb.Left = Math.Max(0, Left + (Width - fallbackTbW) / 2);
            tb.Top = Math.Max(0, Top + Height + 8);
            return;
        }

        // 工具条 ActualWidth/Height 是 DIP（PerMonitor V2 下与所在屏 DPI 无关，content 大小恒定）。
        // 跨到 anchor monitor 后视觉一致 → 物理像素 = DIP × anchor DPI / 96
        var tbWDip = tb.ActualWidth > 0 ? tb.ActualWidth : 400;
        var tbHDip = tb.ActualHeight > 0 ? tb.ActualHeight : 44;
        int tbWPx = (int)Math.Ceiling(tbWDip * _anchorDpiX / 96.0);
        int tbHPx = (int)Math.Ceiling(tbHDip * _anchorDpiY / 96.0);

        // 主窗物理像素几何（含边框扩展，与 PlaceWindowAtAnchorPhysical 同算法）
        int borderPxX = (int)Math.Round(_borderWidth * _anchorDpiX / 96.0);
        int borderPxY = (int)Math.Round(_borderWidth * _anchorDpiY / 96.0);
        int mainLeftPx = _anchorPhysical.Left - borderPxX;
        int mainTopPx = _anchorPhysical.Top - borderPxY;
        int mainWidthPx = _anchorPhysical.Width + borderPxX * 2;
        int mainHeightPx = _anchorPhysical.Height + borderPxY * 2;
        int mainBottomPx = mainTopPx + mainHeightPx;

        // 水平居中；垂直下方 8 DIP / 主窗内部底部 12 DIP，都按 anchor monitor DPI 缩放到物理像素
        int gapBelowPx = (int)Math.Round(8 * _anchorDpiY / 96.0);
        int gapInsidePx = (int)Math.Round(12 * _anchorDpiY / 96.0);
        int leftPx = mainLeftPx + (mainWidthPx - tbWPx) / 2;
        int topBelowPx = mainBottomPx + gapBelowPx;
        int topInsidePx = mainBottomPx - tbHPx - gapInsidePx;

        // anchor monitor 工作区底部物理像素：直接拿 anchor 所在屏的 rcWork，与位置计算同口径（物理像素）
        var monitor = MonitorQuery.FromRect(
            _anchorPhysical.Left, _anchorPhysical.Top,
            _anchorPhysical.Right, _anchorPhysical.Bottom);
        int workBottomPx = monitor.WorkBottom;

        int topPx = (topBelowPx + tbHPx) <= workBottomPx ? topBelowPx : topInsidePx;

        // 边界保护：避免工具条横向被推出 anchor monitor 工作区外（主窗贴屏左/右时会发生）
        if (leftPx < monitor.WorkLeft) leftPx = monitor.WorkLeft;
        if (leftPx + tbWPx > monitor.WorkRight) leftPx = monitor.WorkRight - tbWPx;
        if (topPx < monitor.WorkTop) topPx = monitor.WorkTop;

        NativeMethods.SetWindowPos(
            tbHwnd,
            NativeMethods.HWND_TOPMOST,
            leftPx, topPx, Math.Max(1, tbWPx), Math.Max(1, tbHPx),
            NativeMethods.SWP_NOACTIVATE);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // 主窗关闭时一并关闭工具条 / 文本面板；Owner=this 在 .NET WPF 也会自动关，
        // 这里显式调以防 Owner 链路异常
        if (_toolbarWindow is not null)
        {
            try { _toolbarWindow.Close(); } catch { }
            _toolbarWindow = null;
        }
        if (_textPanelWindow is not null)
        {
            try { _textPanelWindow.Close(); } catch { }
            _textPanelWindow = null;
        }
    }

    /// <summary>打开右侧识别文本面板（不存在则新建，已存在则显示并刷新）。
    /// initial=true 时按"主窗右侧"策略放置；后续切换显隐时尊重用户拖动后的位置。
    ///
    /// 创建顺序与 PositionToolbarInitially 保持一致：先 Show 让面板测出 ActualWidth/Height，
    /// 再用 Win32 SetWindowPos 把面板按物理像素绝对定位到目标位置，规避 PerMonitor V2 下
    /// Window.Left/Top setter 用主屏 DPI 解读 DIP 的跨屏跳屏问题</summary>
    public void OpenTextPanel(bool initial)
    {
        if (_textPanelWindow is null)
        {
            _textPanelWindow = new OcrResultTextPanelWindow(
                onCopyAll: () =>
                {
                    var text = EffectiveFullText();
                    if (string.IsNullOrEmpty(text)) text = JoinAllText();
                    if (!string.IsNullOrEmpty(text)) CopyTextSilently(text);
                    ShowToast(string.IsNullOrEmpty(text) ? "没有识别到内容" : $"已复制全部 / {text.Length} 字");
                },
                onClose: () =>
                {
                    // 用户点关闭：直接收起面板，保留窗口对象方便下次切换显示时复用位置 / 内容
                    HideTextPanel();
                });
            _textPanelWindow.Owner = this;
            // 关键：刚 new 出来时不要在 OS 默认 (0,0) 闪现，先挪屏外再 Show
            _textPanelWindow.Left = -32000;
            _textPanelWindow.Top = -32000;
            _textPanelWindow.Width = 320;
            _textPanelWindow.Height = Math.Max(160, Math.Min(560, ActualHeight));
            _textPanelWindow.Show();
            UpdateTextPanelContent();
            PositionTextPanelInitially();
        }
        else
        {
            UpdateTextPanelContent();
            _textPanelWindow.Show();
            if (initial) PositionTextPanelInitially();
        }

        _toolbarWindow?.SetTextPanelVisible(true);
    }

    public void HideTextPanel()
    {
        try { _textPanelWindow?.Hide(); } catch { }
        _toolbarWindow?.SetTextPanelVisible(false);
    }

    /// <summary>把文本面板放到主窗右侧；右侧空间不够时落到左侧；左侧也不够时落到主窗下方。
    /// 与 PositionToolbarInitially 同一策略：用 anchor monitor 的物理像素绝对定位，
    /// 跨屏不会跳屏，多 DPI 下尺寸视觉一致</summary>
    private void PositionTextPanelInitially()
    {
        if (_textPanelWindow is null) return;
        var tp = _textPanelWindow;

        var tpHwnd = new WindowInteropHelper(tp).Handle;
        if (tpHwnd == IntPtr.Zero) return;

        var tpWDip = tp.Width > 0 ? tp.Width : 320;
        var tpHDip = tp.Height > 0 ? tp.Height : 360;
        int tpWPx = (int)Math.Ceiling(tpWDip * _anchorDpiX / 96.0);
        int tpHPx = (int)Math.Ceiling(tpHDip * _anchorDpiY / 96.0);

        int borderPxX = (int)Math.Round(_borderWidth * _anchorDpiX / 96.0);
        int borderPxY = (int)Math.Round(_borderWidth * _anchorDpiY / 96.0);
        int mainLeftPx = _anchorPhysical.Left - borderPxX;
        int mainTopPx = _anchorPhysical.Top - borderPxY;
        int mainWidthPx = _anchorPhysical.Width + borderPxX * 2;
        int mainHeightPx = _anchorPhysical.Height + borderPxY * 2;
        int mainRightPx = mainLeftPx + mainWidthPx;
        int mainBottomPx = mainTopPx + mainHeightPx;

        var monitor = MonitorQuery.FromRect(
            _anchorPhysical.Left, _anchorPhysical.Top,
            _anchorPhysical.Right, _anchorPhysical.Bottom);
        int gapPx = (int)Math.Round(8 * _anchorDpiX / 96.0);

        int leftPx;
        int topPx;
        // 优先右侧：截图右边 + gap
        if (mainRightPx + gapPx + tpWPx <= monitor.WorkRight)
        {
            leftPx = mainRightPx + gapPx;
            topPx = mainTopPx;
        }
        // 其次左侧
        else if (mainLeftPx - gapPx - tpWPx >= monitor.WorkLeft)
        {
            leftPx = mainLeftPx - gapPx - tpWPx;
            topPx = mainTopPx;
        }
        // 都不够：放主窗下方左对齐
        else
        {
            leftPx = mainLeftPx;
            topPx = mainBottomPx + gapPx;
        }

        // 边界保护：避免被推出 anchor monitor 工作区
        if (leftPx < monitor.WorkLeft) leftPx = monitor.WorkLeft;
        if (leftPx + tpWPx > monitor.WorkRight) leftPx = Math.Max(monitor.WorkLeft, monitor.WorkRight - tpWPx);
        if (topPx < monitor.WorkTop) topPx = monitor.WorkTop;
        if (topPx + tpHPx > monitor.WorkBottom) topPx = Math.Max(monitor.WorkTop, monitor.WorkBottom - tpHPx);

        NativeMethods.SetWindowPos(
            tpHwnd,
            NativeMethods.HWND_TOPMOST,
            leftPx, topPx, Math.Max(1, tpWPx), Math.Max(1, tpHPx),
            NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>用当前 _result + 选中状态 + 译文 overlay 刷新文本面板内容。
    /// 内容选择：有选中 → 显示选中段落；无选中 → 显示整段识别文本。
    /// 顶部元信息显示"段数 / 字数"，让用户对识别结果体量一目了然</summary>
    private void UpdateTextPanelContent()
    {
        if (_textPanelWindow is null) return;
        int sel = SelectedCount();
        string body;
        string meta;
        if (sel > 0)
        {
            body = JoinSelectedText();
            meta = $"已选 {sel}/{_result.Blocks.Count} 段 · {body.Length} 字";
        }
        else
        {
            body = EffectiveFullText();
            if (string.IsNullOrEmpty(body)) body = JoinAllText();
            meta = $"{_result.Blocks.Count} 段 · {body.Length} 字";
        }
        _textPanelWindow.SetContent(body);
        _textPanelWindow.SetMeta(meta);
    }

    /// <summary>工具栏按钮触发：在显隐两态之间切换。
    /// 隐藏状态包括"窗口未创建"和"已 Hide"，都通过 OpenTextPanel(initial=false) 重新显示</summary>
    public void CommandToggleTextPanel()
    {
        if (_textPanelWindow is null || _textPanelWindow.Visibility != Visibility.Visible)
        {
            OpenTextPanel(initial: _textPanelWindow is null);
        }
        else
        {
            HideTextPanel();
        }
    }

    /// <summary>Image / OverlayCanvas / TranslationCanvas 这些渲染层在 WindowBorderFrame 内部，
    /// 它们的可用尺寸 = Window 总尺寸 - 边框（两侧各占 _borderWidth）。
    /// polygon / 译文覆盖等所有空间映射统一用这个 inner size，否则 bordered 模式下会偏移、错位</summary>
    private (double Width, double Height) GetInnerSize()
    {
        double w = Math.Max(0, ActualWidth - _borderWidth * 2);
        double h = Math.Max(0, ActualHeight - _borderWidth * 2);
        return (w, h);
    }

    /// <summary>遍历 OcrResult.Blocks 在 OverlayCanvas 上建一一对应的 Polygon。
    /// Polygon.Tag 存储下标，让 mouse 回调能快速反查 block。
    /// 颜色按 default → 用户交互后再被 SetState 更新。
    ///
    /// 缩放系数用 Image 区域（Window 内部去掉 border）大小，而非 Window 整体。
    /// 否则 bordered 模式下 polygon 比截图大 2px、整体偏移 2px，与原图错位</summary>
    private void BuildPolygons()
    {
        OverlayCanvas.Children.Clear();
        _polygons.Clear();
        if (_result.SourceWidth <= 0 || _result.SourceHeight <= 0) return;

        var (innerW, innerH) = GetInnerSize();
        double sx = innerW / _result.SourceWidth;
        double sy = innerH / _result.SourceHeight;

        var fill = (Brush?)TryFindResource("ToolbarAccentSoft") ?? Brushes.SteelBlue;

        for (int i = 0; i < _result.Blocks.Count; i++)
        {
            var b = _result.Blocks[i];
            var polygon = new Polygon
            {
                Points = new PointCollection
                {
                    new WpfPoint(b.Box.X1 * sx, b.Box.Y1 * sy),
                    new WpfPoint(b.Box.X2 * sx, b.Box.Y2 * sy),
                    new WpfPoint(b.Box.X3 * sx, b.Box.Y3 * sy),
                    new WpfPoint(b.Box.X4 * sx, b.Box.Y4 * sy),
                },
                Fill = fill,
                Opacity = 0.18,
                Stroke = Brushes.Transparent,
                StrokeThickness = 1,
                Cursor = Cursors.IBeam,
                Tag = i,
                ToolTip = b.Text.Length > 80 ? b.Text[..80] + "…" : b.Text,
            };
            polygon.MouseEnter += OnPolygonMouseEnter;
            polygon.MouseLeave += OnPolygonMouseLeave;
            polygon.MouseLeftButtonDown += OnPolygonClick;
            OverlayCanvas.Children.Add(polygon);
            _polygons.Add(polygon);
        }
    }

    private enum BoxState { Default, Hover, Selected }

    private void SetState(int index, BoxState state)
    {
        if (index < 0 || index >= _polygons.Count) return;
        var p = _polygons[index];
        switch (state)
        {
            case BoxState.Default:
                p.Opacity = 0.18;
                p.Stroke = Brushes.Transparent;
                p.StrokeThickness = 1;
                break;
            case BoxState.Hover:
                p.Opacity = 0.42;
                p.Stroke = (Brush?)TryFindResource("ToolbarForeground") ?? Brushes.White;
                p.StrokeThickness = 1;
                break;
            case BoxState.Selected:
                p.Opacity = 0.70;
                p.Stroke = (Brush?)TryFindResource("ToolbarForeground") ?? Brushes.White;
                p.StrokeThickness = 2;
                break;
        }
    }

    private void RefreshAllStates()
    {
        for (int i = 0; i < _polygons.Count; i++)
        {
            if (_selected[i]) SetState(i, BoxState.Selected);
            else if (i == _hoverIndex) SetState(i, BoxState.Hover);
            else SetState(i, BoxState.Default);
        }
    }

    // ============== Polygon 事件 ==============

    private void OnPolygonMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Polygon p || p.Tag is not int idx) return;
        if (_selected[idx]) return;
        _hoverIndex = idx;
        SetState(idx, BoxState.Hover);
    }

    private void OnPolygonMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Polygon p || p.Tag is not int idx) return;
        if (_hoverIndex == idx) _hoverIndex = -1;
        if (!_selected[idx]) SetState(idx, BoxState.Default);
    }

    private void OnPolygonClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Polygon p || p.Tag is not int idx) return;
        e.Handled = true;

        var modifiers = Keyboard.Modifiers;
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
        {
            ClearSelection();
            _selected[idx] = true;
            RefreshAllStates();
            CopyTextSilently(_result.Blocks[idx].Text);
            ShowToast($"已复制：{Preview(_result.Blocks[idx].Text)}");
        }
        else
        {
            _selected[idx] = !_selected[idx];
            RefreshAllStates();
        }
        UpdateStatusBar();
    }

    // ============== Marquee 框选 ==============

    private void OnOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        _marqueeStart = e.GetPosition(OverlayCanvas);
        _isDragging = false;
        OverlayCanvas.CaptureMouse();

        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
        {
            ClearSelection();
            RefreshAllStates();
            UpdateStatusBar();
        }
    }

    private void OnOverlayMouseMove(object sender, MouseEventArgs e)
    {
        if (_marqueeStart is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelMarquee(); return; }

        var cur = e.GetPosition(OverlayCanvas);
        var dx = cur.X - _marqueeStart.Value.X;
        var dy = cur.Y - _marqueeStart.Value.Y;

        if (!_isDragging && Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;
        _isDragging = true;

        double l = Math.Min(_marqueeStart.Value.X, cur.X);
        double t = Math.Min(_marqueeStart.Value.Y, cur.Y);
        double w = Math.Abs(dx);
        double h = Math.Abs(dy);
        Canvas.SetLeft(MarqueeRect, l);
        Canvas.SetTop(MarqueeRect, t);
        MarqueeRect.Width = w;
        MarqueeRect.Height = h;
        MarqueeRect.Visibility = Visibility.Visible;

        var rect = new Rect(l, t, w, h);
        bool additive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        UpdateSelectionByMarquee(rect, additive);
    }

    private void OnOverlayMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_marqueeStart is null) return;
        OverlayCanvas.ReleaseMouseCapture();
        var wasDragging = _isDragging;
        _marqueeStart = null;
        _isDragging = false;
        MarqueeRect.Visibility = Visibility.Collapsed;
        UpdateStatusBar();
        if (wasDragging && SelectedCount() > 0)
        {
            ShowToast($"已选 {SelectedCount()} 段（Ctrl+C 复制）");
        }
    }

    private void OnOverlayMouseLeave(object sender, MouseEventArgs e)
    {
        if (_marqueeStart is not null && e.LeftButton != MouseButtonState.Pressed)
        {
            CancelMarquee();
        }
    }

    private void CancelMarquee()
    {
        _marqueeStart = null;
        _isDragging = false;
        try { OverlayCanvas.ReleaseMouseCapture(); } catch { }
        MarqueeRect.Visibility = Visibility.Collapsed;
    }

    private void UpdateSelectionByMarquee(Rect marquee, bool additive)
    {
        var snapshot = additive ? (bool[])_selected.Clone() : new bool[_polygons.Count];
        for (int i = 0; i < _polygons.Count; i++)
        {
            var bounds = _polygons[i].RenderedGeometry.Bounds;
            if (bounds.IntersectsWith(marquee))
            {
                snapshot[i] = true;
            }
        }
        _selected = snapshot;
        RefreshAllStates();
    }

    // ============== 键盘 ==============

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseSelf("escape");
                break;
            case Key.A when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                e.Handled = true;
                SelectAll();
                break;
            case Key.C when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                e.Handled = true;
                if (SelectedCount() > 0) CommandCopySelected();
                else CommandCopyAll();
                break;
            case Key.D when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                e.Handled = true;
                ClearSelection();
                RefreshAllStates();
                UpdateStatusBar();
                break;
        }
    }

    // ============== 右键菜单事件 ==============

    private void OnCopySelected(object sender, RoutedEventArgs e) => CommandCopySelected();
    private void OnCopyAll(object sender, RoutedEventArgs e) => CommandCopyAll();
    private void OnCloseClicked(object sender, RoutedEventArgs e) => CommandClose();
    private void OnSelectAllMenu(object sender, RoutedEventArgs e) => SelectAll();
    private void OnSwitchToQuick(object sender, RoutedEventArgs e) => CommandSwitchToQuick();
    private void OnTranslateSelected(object sender, RoutedEventArgs e) => CommandTranslateSelected();
    private void OnTranslateAll(object sender, RoutedEventArgs e) => CommandTranslateAll();
    private void OnTranslateClear(object sender, RoutedEventArgs e) => CommandTranslateClear();

    private void OnClearSelectionMenu(object sender, RoutedEventArgs e)
    {
        ClearSelection();
        RefreshAllStates();
        UpdateStatusBar();
    }

    // ============== Command 方法（工具条 / 右键菜单 / 键盘共用入口）==============

    public void CommandCopySelected()
    {
        var text = JoinSelectedText();
        if (string.IsNullOrEmpty(text))
        {
            ShowToast("没有选中任何文本");
            return;
        }
        CopyTextSilently(text);
        ShowToast($"已复制 {SelectedCount()} 段 / {text.Length} 字");
    }

    public void CommandCopyAll()
    {
        var text = EffectiveFullText();
        if (string.IsNullOrEmpty(text)) text = JoinAllText();
        if (string.IsNullOrEmpty(text))
        {
            ShowToast("没有识别到内容");
            return;
        }
        CopyTextSilently(text);
        ShowToast($"已复制全部 / {text.Length} 字");
    }

    public void CommandClose() => CloseSelf("command");

    /// <summary>"Quick 输出"按钮：把当次 OCR 结果按 Quick 模式重新走一遍（剪贴板 + 浮窗气泡 + toast），
    /// 然后关掉结果窗。不修改 settings.OcrResultMode —— 长期偏好应通过设置面板切换，
    /// 本按钮只是一次性"我这次想要 Quick 风格的输出"的临时通道</summary>
    public void CommandSwitchToQuick()
    {
        var text = EffectiveFullText();
        if (_quickFallback is null)
        {
            // 兜底：没传回调（理论上不会发生）— 至少把全文复制走，避免按钮按了什么都不做
            if (!string.IsNullOrEmpty(text)) CopyTextSilently(text);
            ShowToast("已复制全部");
            Dispatcher.BeginInvoke(new Action(() => CloseSelf("switch-quick-fallback")), DispatcherPriority.Background);
            return;
        }
        _log.Info("ocr result quick-output triggered");

        // 先关窗，再触发 Quick 渲染。
        // 顺序很重要：Quick 渲染会触发浮窗显示，如果结果窗还在 topmost、又遮在浮窗上方，用户就看不到气泡
        var fallback = _quickFallback;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            CloseSelf("quick-output");
            try { fallback(text); }
            catch (Exception ex) { _log.Warn("quick fallback failed", ("err", ex.Message)); }
        }), DispatcherPriority.Background);
    }

    /// <summary>工具栏"启用 AI"按钮（仅当 AI 未启用时显示）：
    /// 直接跳到设置面板的 AI 页让用户填 API Key，比 Toast 引导更直接。
    /// 没有 openSettings 回调时降级为 Toast 提示（理论上不会发生，AppHost 已注入）</summary>
    public void CommandOpenAiSettings()
    {
        if (_openSettings is null)
        {
            ShowToast("请先在设置启用 AI 并配置 API Key");
            return;
        }
        try { _openSettings("AI"); }
        catch (Exception ex) { _log.Warn("open ai settings failed", ("err", ex.Message)); }
    }

    public void CommandTranslateAll() => _ = TranslateAsync(Enumerable.Range(0, _result.Blocks.Count).ToList());

    public void CommandTranslateSelected()
    {
        var indices = new List<int>();
        for (int i = 0; i < _selected.Length; i++) if (_selected[i]) indices.Add(i);
        if (indices.Count == 0)
        {
            ShowToast("没有选中任何文本（点高亮 / 拖框先选）");
            return;
        }
        _ = TranslateAsync(indices);
    }

    public void CommandTranslateClear()
    {
        ClearAllTranslations();
        UpdateStatusBar();
        ShowToast("已清除所有译文");
    }

    // ============== 翻译 ==============

    private async Task TranslateAsync(IReadOnlyList<int> blockIndices)
    {
        if (_aiText is null || !_aiText.CanRun)
        {
            ShowToast("请先在设置启用 AI 并配置 API Key");
            return;
        }
        if (blockIndices.Count == 0) return;
        if (_translating) { ShowToast("翻译进行中，请稍候"); return; }

        _translating = true;
        var prevStatus = _toolbarWindow is not null ? "translating" : null;
        _toolbarWindow?.SetTranslateAllEnabled(false);
        _toolbarWindow?.SetTranslateSelectedEnabled(false);
        _toolbarWindow?.SetTranslateClearEnabled(false);
        _toolbarWindow?.SetStatus($"翻译中… ({blockIndices.Count} 段)");

        try
        {
            var sources = blockIndices.Select(i => _result.Blocks[i].Text).ToList();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            IReadOnlyList<string> translations;
            try
            {
                translations = await _aiText.TranslateBatchAsync(sources, cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                ShowToast("翻译超时（60 秒）");
                return;
            }
            catch (Exception ex)
            {
                _log.Warn("ocr translate failed", ("err", ex.Message), ("blocks", blockIndices.Count));
                ShowToast("翻译失败：" + ex.Message);
                return;
            }

            for (int k = 0; k < blockIndices.Count && k < translations.Count; k++)
            {
                var bi = blockIndices[k];
                var translated = (translations[k] ?? "").Trim();
                if (translated.Length == 0) continue;
                RenderTranslationOverlay(bi, translated);
            }
            ShowToast($"已翻译 {blockIndices.Count} 段");
        }
        finally
        {
            _translating = false;
            UpdateStatusBar();
        }
    }

    /// <summary>给指定 block 的 polygon 位置贴一个译文 Border：
    /// 用 polygon 的 AABB 作为定位基准（足够覆盖原文），背景用主题色不透明遮住原文，
    /// 文本字号按 AABB 高度 * 0.62 自适应让单行刚好填满。
    /// 已有的 overlay 会被替换（同一 block 二次翻译时直接覆盖）。</summary>
    private void RenderTranslationOverlay(int blockIndex, string translated)
    {
        if (blockIndex < 0 || blockIndex >= _result.Blocks.Count) return;
        if (_result.SourceWidth <= 0 || _result.SourceHeight <= 0) return;

        if (_translationOverlays[blockIndex] is { } old)
        {
            TranslationCanvas.Children.Remove(old);
            _translationOverlays[blockIndex] = null;
        }

        var (innerW, innerH) = GetInnerSize();
        double sx = innerW / _result.SourceWidth;
        double sy = innerH / _result.SourceHeight;
        var (l, t, r, b) = _result.Blocks[blockIndex].Box.AABB();
        double left = l * sx;
        double top = t * sy;
        double width = Math.Max(20, (r - l) * sx);
        double height = Math.Max(12, (b - t) * sy);
        double fontSize = Math.Max(8, Math.Min(36, height * 0.62));

        var src = _result.Blocks[blockIndex].Text.Trim();
        bool unchanged = string.Equals(src, translated, StringComparison.Ordinal);

        var tb = new TextBlock
        {
            Text = translated,
            FontSize = fontSize,
            FontFamily = (FontFamily?)TryFindResource("ToolbarFontFamily") ?? new FontFamily("Segoe UI"),
            Foreground = (Brush?)TryFindResource("ToolbarForeground") ?? Brushes.White,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(4, 0, 4, 0),
            Opacity = unchanged ? 0.6 : 1.0,
        };

        var border = new Border
        {
            Width = width,
            Height = height,
            Background = (Brush?)TryFindResource("ToolbarBackground") ?? Brushes.Black,
            BorderBrush = (Brush?)TryFindResource("ToolbarAccentSoft") ?? Brushes.SteelBlue,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Child = tb,
            ToolTip = unchanged ? $"原文与译文一致：{translated}" : $"原文：{src}\n译文：{translated}",
        };
        Canvas.SetLeft(border, left);
        Canvas.SetTop(border, top);
        TranslationCanvas.Children.Add(border);
        _translationOverlays[blockIndex] = border;
    }

    private void ClearAllTranslations()
    {
        for (int i = 0; i < _translationOverlays.Length; i++)
        {
            if (_translationOverlays[i] is { } b)
            {
                TranslationCanvas.Children.Remove(b);
                _translationOverlays[i] = null;
            }
        }
    }

    // ============== 选区辅助 ==============

    private void SelectAll()
    {
        if (_polygons.Count == 0) return;
        for (int i = 0; i < _selected.Length; i++) _selected[i] = true;
        RefreshAllStates();
        UpdateStatusBar();
        ShowToast($"已全选 {_polygons.Count} 段（Ctrl+C 复制）");
    }

    private void ClearSelection()
    {
        for (int i = 0; i < _selected.Length; i++) _selected[i] = false;
    }

    private int SelectedCount()
    {
        int n = 0;
        for (int i = 0; i < _selected.Length; i++) if (_selected[i]) n++;
        return n;
    }

    /// <summary>拼接选中 block 的文本，优先取译文（如果有 overlay），否则原文 —
    /// 这样"先翻译再复制选中"能自然拿到译文内容</summary>
    private string JoinSelectedText()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _result.Blocks.Count; i++)
        {
            if (!_selected[i]) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(GetEffectiveText(i));
        }
        return sb.ToString();
    }

    private string JoinAllText()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _result.Blocks.Count; i++)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(GetEffectiveText(i));
        }
        return sb.ToString();
    }

    private string EffectiveFullText()
        => OriginalFullText();

    private string OriginalFullText()
    {
        if (!string.IsNullOrWhiteSpace(_layoutFullText)) return _layoutFullText;
        var text = _result.FullText;
        return string.IsNullOrWhiteSpace(text) ? JoinAllOriginalText() : text.Trim();
    }

    private string JoinAllOriginalText()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _result.Blocks.Count; i++)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(_result.Blocks[i].Text);
        }
        return sb.ToString();
    }

    private string GetEffectiveText(int i)
    {
        if (_translationOverlays[i]?.Child is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
            return tb.Text;
        return _result.Blocks[i].Text;
    }

    // ============== 状态条 / Toast ==============

    private void UpdateStatusBar()
    {
        int total = _result.Blocks.Count;
        int sel = SelectedCount();

        // 工具栏状态胶囊：只在"有实际选中段落"或"完全未识别"时展示。
        // 旧的"点击 / 拖框 / Ctrl+A 全选"那种引导文案对老用户是视觉噪音，
        // 现在按钮 ToolTip 已经覆盖了同样的发现性引导，可以彻底去掉
        string status;
        bool copySelEnabled;
        if (sel > 0)
        {
            status = $"已选 {sel}/{total}";
            copySelEnabled = true;
        }
        else if (total == 0)
        {
            status = "未识别到文本";
            copySelEnabled = false;
        }
        else
        {
            status = "";
            copySelEnabled = false;
        }

        // 推送到工具条
        _toolbarWindow?.SetStatus(status);
        _toolbarWindow?.SetCopySelectedEnabled(copySelEnabled);
        _toolbarWindow?.SetCopyAllEnabled(total > 0);
        _toolbarWindow?.SetTranslateSelectedEnabled(sel > 0 && !_translating);
        _toolbarWindow?.SetTranslateAllEnabled(total > 0 && !_translating);
        bool hasTranslation = _translationOverlays.Any(o => o is not null);
        _toolbarWindow?.SetTranslateClearEnabled(hasTranslation && !_translating);

        // 选区变化时同步刷新文本面板内容（仅当面板已经打开）
        if (_textPanelWindow is not null && _textPanelWindow.Visibility == Visibility.Visible)
        {
            UpdateTextPanelContent();
        }

        // 同步右键菜单可用态
        MiCopySelected.IsEnabled = sel > 0;
        MiClearSel.IsEnabled = sel > 0;
        MiCopyAll.IsEnabled = total > 0;
        MiSelectAll.IsEnabled = total > 0 && sel < total;
        MiTranslateAll.IsEnabled = total > 0;
        MiTranslateSelected.IsEnabled = sel > 0;
        MiTranslateClear.IsEnabled = hasTranslation;

        // AI 未启用时把右键菜单的翻译项也隐藏
        var aiAvailable = _aiText is not null && _aiText.CanRun;
        var vis = aiAvailable ? Visibility.Visible : Visibility.Collapsed;
        MiTranslateAll.Visibility = vis;
        MiTranslateSelected.Visibility = vis;
        MiTranslateClear.Visibility = vis;
    }

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        ToastBorder.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private static string Preview(string text)
    {
        var compact = string.Join(' ', text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 28 ? compact : compact[..28] + "…";
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return text.Count(c => c == '\n') + 1;
    }

    private void CopyTextSilently(string text)
    {
        try
        {
            _clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            _log.Warn("ocr result copy failed", ("err", ex.Message));
        }
    }

    private bool _closed;
    private void CloseSelf(string reason)
    {
        if (_closed) return;
        _closed = true;
        _toastTimer.Stop();
        _log.Debug("ocr result window closed", ("reason", reason),
            ("blocks", _result.Blocks.Count), ("selected", SelectedCount()));
        try { Close(); } catch { }
        _onCloseRequested?.Invoke();
    }
}
