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

internal partial class OcrResultWindow : Window
{

    private readonly ILog _log;

    /// <summary>当前结果。loading 模式下初始为 OcrResult.Empty，
    /// PopulateResult 调用时替换为真实结果并重建 polygon / 工具栏状态。
    /// 因此不能用 readonly —— 整个窗口的生命周期中可能从"识别中态"过渡到"已识别态"</summary>
    private OcrResult _result;
    private readonly OcrImage _screenshot;
    private readonly ClipboardWriter _clipboard;
    private readonly AppSettings _settings;
    private readonly AiTextService? _aiText;
    private readonly Action? _onCloseRequested;
    private string _layoutFullText;

    /// <summary>窗口当前是否处于"OCR 识别中"加载态。
    /// loading 时：截图背景立即显示，polygon overlay / 工具栏 copy/translate 按钮禁用；
    /// PopulateResult 完成后转为 false，激活完整交互能力。
    /// 设计动机：让"截图预览 → OCR"切换可立即建立新窗体（保留位置 / 背景），
    /// OCR 异步完成时再叠 polygon，避免 1~3 秒识别期间 UI 空白卡住</summary>
    private bool _isLoading;

    /// <summary>窗口是否已 Close。Closed 事件触发后置 true，让 PopulateResult 等异步回调能感知
    /// "窗口已被用户提前关闭"（如 loading 期间用户按了 Esc / 切到截图 / 开始新识别），
    /// 此时延迟到达的 OCR 结果不应再尝试操作 UI 元素</summary>
    private bool _isClosed;

    /// <summary>用户在结果窗点"Quick 输出"时调用：交给 Coordinator 走一遍 Quick 模式渲染
    /// （剪贴板 + 浮窗气泡 + toast），不修改 settings.OcrResultMode。
    /// 用户的 OCR 模式偏好通过设置面板永久切换，本按钮只影响当次输出</summary>
    private readonly Action<string>? _quickFallback;

    /// <summary>用户点工具栏"切到截图预览"时调用。
    /// 参数：当前 OCR 结果图在屏幕上的物理像素 rect（含用户拖动 / 跨屏挪动后的真实位置），
    /// 让 Coordinator 据此把新打开的截图预览窗放到同一个位置，视觉上保留窗体位置惯性。
    ///
    /// null 表示宿主没有提供此能力（兜底场景），UI 上的切换按钮会被置灰。
    /// 切换流程改造：本窗不再自行 Close —— 等 Coordinator 把新预览窗 Show + Loaded 后再统一关旧窗，
    /// 让"高亮 OCR 图 → 原始截图"切换在视觉上无缝衔接</summary>
    private readonly Action<WinRectangle>? _switchToScreenshot;

    /// <summary>用户点工具栏"开始新识别"时调用：交给 Coordinator 关闭本窗并重新进入选区状态机。
    /// 与"切到截图"按钮的区别：reshoot 不复用当前截图，会真正进入选区让用户重新框选 OCR 区域。
    /// null 时按钮置灰</summary>
    private readonly Action? _reshootOcr;

    /// <summary>"钉到桌面"按钮触发：参数为当前结果窗截图区域的物理像素 rect。
    /// Coordinator 据此让 PinnedScreenshotManager 创建 PinnedScreenshotWindow。
    /// null 时按钮置灰</summary>
    private readonly Action<WinRectangle>? _pinScreenshot;
    private readonly Action? _saveSettings;

    /// <summary>"打开设置某一分页"的跨层回调（如 "AI" / "Ocr"）。
    /// 为空时跳转入口降级为 Toast 提示，避免空引用</summary>
    private readonly Action<string>? _openSettings;

    /// <summary>边框宽度（DIP）。bordered=true 时为 2.0，否则 0.0。
    /// 影响：1) WindowBorderFrame.BorderThickness；2) Window 总尺寸要扩出 2 * borderWidth
    /// 让里面的 Image / Grid 区域仍与原截图等大，保证 Stretch="Fill" 时 1:1 像素映射、文字不糊</summary>
    private readonly double _borderWidth;

    /// <summary>外圈阴影预留（DIP）。bordered=true 时与 ScreenshotPreviewWindow 同款 DropShadow，
    /// 用 Margin 给阴影留绘制空间，否则 AllowsTransparency 透明窗口会把阴影裁掉。
    /// false 时为 0，窗口紧贴截图边缘无任何外发光。
    /// 几何计算：Window 总 DIP = anchor DIP + 2 * (_borderWidth + _shadowMargin)，
    /// _outerInsetWidth 用作整体 inset，所有 polygon 位置 / 物理像素定位都用它</summary>
    private readonly double _shadowMargin;

    /// <summary>从窗口左上到截图左上的总 DIP inset（= _borderWidth + _shadowMargin）。
    /// 替代旧的"只用 _borderWidth"几何，让加阴影后所有定位计算保持单点真理</summary>
    private readonly double _outerInsetWidth;

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
    /// 顺序与 _result.Blocks 一致，下标可双向查询。
    /// loading → populated 切换时由 BuildPolygons 重建，不能 readonly</summary>
    private List<Polygon> _polygons = new();

    /// <summary>每个 block 对应的"译文覆盖框"。null 表示该 block 当前没有 overlay 显示。
    /// 与 _translatedText 解耦：用户切回原文时清掉 overlay（设为 null），但 _translatedText 保留
    /// 让二次切换译文显示能直接走缓存，免去重新调用 AI。
    /// PopulateResult 时按新 result.Blocks.Count 重新分配 —— 不能 readonly</summary>
    private Border?[] _translationOverlays;

    /// <summary>每个 block 对应的 OCR 原文贴图。用于工具栏"显示识别原文"开关，
    /// 与翻译 overlay 分层，避免翻译切换影响原文贴图偏好。
    /// PopulateResult 时按新 result.Blocks.Count 重新分配 —— 不能 readonly</summary>
    private Border?[] _recognizedTextOverlays;

    /// <summary>每个 block 的译文文本缓存。null = 从未翻译过；非空字符串 = 已翻译过且文本可复用。
    /// 切换"显示译文 → 原文 → 译文"时，第二次切回不需要重新请求 AI；仅当用户主动调 CommandTranslateClear
    /// 清空缓存，或选中段落集合扩张到新的未缓存段时，才会触发新的 AI 请求。
    /// PopulateResult 时按新 result.Blocks.Count 重新分配 —— 不能 readonly</summary>
    private string?[] _translatedText;

    /// <summary>当前是否处于"展示译文"态。
    /// false（默认）：原文模式，TranslationCanvas 上没有 overlay；
    /// true：译文模式，相关 block 的 overlay 已绘制并可见。
    /// CommandToggleTranslation 翻转此状态机，工具栏按钮图标 / ToolTip 也据此切换</summary>
    private bool _showingTranslation;

    /// <summary>当前是否把 OCR 原文直接贴到结果图对应 block 位置。</summary>
    private bool _showingInlineText;

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
        OcrImage screenshot,
        WinRectangle anchorPhysical,
        uint anchorDpiX,
        uint anchorDpiY,
        ClipboardWriter clipboard,
        AppSettings settings,
        AiTextService? aiText,
        string? layoutFullText = null,
        Action<string>? quickFallback = null,
        Action<string>? openSettings = null,
        Action? saveSettings = null,
        Action? onCloseRequested = null,
        Action<WinRectangle>? switchToScreenshot = null,
        Action? reshootOcr = null,
        Action<WinRectangle>? pinScreenshot = null,
        bool isLoading = false)
    {
        _log = log;
        _result = result;
        _isLoading = isLoading;
        _screenshot = screenshot;
        _clipboard = clipboard;
        _settings = settings;
        _aiText = aiText;
        _layoutFullText = string.IsNullOrWhiteSpace(layoutFullText) ? "" : layoutFullText.Trim();
        _quickFallback = quickFallback;
        _openSettings = openSettings;
        _saveSettings = saveSettings;
        _onCloseRequested = onCloseRequested;
        _switchToScreenshot = switchToScreenshot;
        _reshootOcr = reshootOcr;
        _pinScreenshot = pinScreenshot;
        _anchorPhysical = anchorPhysical;
        _anchorDpiX = anchorDpiX == 0 ? 96 : anchorDpiX;
        _anchorDpiY = anchorDpiY == 0 ? 96 : anchorDpiY;
        _selected = new bool[result.Blocks.Count];
        _translationOverlays = new Border?[result.Blocks.Count];
        _recognizedTextOverlays = new Border?[result.Blocks.Count];
        _translatedText = new string?[result.Blocks.Count];
        _showingInlineText = _settings.OcrInlineTextVisible;

        InitializeComponent();

        // loading 模式下立即把截图背景中央的"识别中…"提示挂出来。
        // 比工具栏状态胶囊更醒目；BuildPolygons 跳过时只剩截图 + 提示文字
        if (_isLoading)
        {
            LoadingHintBorder.Visibility = Visibility.Visible;
        }

        // 边框模式：四周加 2px 主题色边框 + 4 DIP 阴影 padding；无边模式保持 0。
        // 关键：Window 总尺寸要扩出 _outerInsetWidth * 2，让里面的 Grid/Image 区域仍等于原截图大小，
        // 否则 Stretch="Fill" 把截图缩放到比截图稍小的 Image 区域，文字像素错位 → 模糊。
        // 同时窗口位置往左上偏 _outerInsetWidth，让 Image 区域的中心仍贴回原截图位置。
        // 阴影 margin 之前用 10 DIP 是想给 BlurRadius=10 的阴影留足够余量，但实际让窗口边距
        // 截图视觉边距 10 DIP，用户拖动窗口时根本贴不到屏幕边。改为 4 DIP + 更轻的阴影
        // (BlurRadius=4 / Depth=1)，视觉上几乎贴齐窗口边，与 ScreenshotPreviewWindow 一致
        _borderWidth = _settings.OcrResultWindowBordered ? 2.0 : 0.0;
        _shadowMargin = _settings.OcrResultWindowBordered ? 4.0 : 0.0;
        _outerInsetWidth = _borderWidth + _shadowMargin;
        WindowBorderFrame.BorderThickness = new Thickness(_borderWidth);
        // 用 Margin 把 WindowBorderFrame 推离窗口边界，给 DropShadowEffect 留绘制余量
        // （AllowsTransparency=True 的透明窗口会把超出 Margin 的阴影像素裁掉）
        WindowBorderFrame.Margin = new Thickness(_shadowMargin);
        if (_settings.OcrResultWindowBordered)
        {
            // 立体感与截图预览严格一致：BlurRadius=4 / ShadowDepth=1 / Opacity=0.20，
            // 保留轻量层次但不抢主图视线，也不让窗口边距截图边过远
            WindowShadowEffect.BlurRadius = 4;
            WindowShadowEffect.ShadowDepth = 1;
            WindowShadowEffect.Opacity = 0.20;
        }

        // 初始把窗口放到屏幕外（主屏负坐标），先让 WPF 完成 layout 测量；
        // Loaded 阶段再用 Win32 SetWindowPos 把窗口精确定位到 anchor 物理像素位置。
        // 不直接用目标 DIP 设置 Left/Top —— 跨屏异构 DPI 时 WPF Window.Left/Top 会按主屏 DPI 解读，
        // 导致副屏框选的结果窗飘回主屏（与 FloatingToolbar / ToolbarToast 走 Win32 SetWindowPos 同因同治）
        double dipScaleX = _anchorDpiX / 96.0;
        double dipScaleY = _anchorDpiY / 96.0;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        Width = Math.Max(120, anchorPhysical.Width / dipScaleX + _outerInsetWidth * 2);
        Height = Math.Max(80, anchorPhysical.Height / dipScaleY + _outerInsetWidth * 2);

        ScreenshotImage.Source = LoadBitmap(screenshot);

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

    private static BitmapSource LoadBitmap(OcrImage image)
    {
        if (image.PixelFormat != OcrImagePixelFormat.Bgra32)
            throw new NotSupportedException($"不支持的 OCR 图像格式：{image.PixelFormat}");
        var pixels = CopyAsOpaqueBgra32(image);
        var bmp = BitmapSource.Create(
            image.Width,
            image.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            image.Stride);
        bmp.Freeze();
        return bmp;
    }

    private static byte[] CopyAsOpaqueBgra32(OcrImage image)
    {
        var pixels = new byte[checked(image.Stride * image.Height)];
        Buffer.BlockCopy(image.Pixels, 0, pixels, 0, pixels.Length);
        for (var y = 0; y < image.Height; y++)
        {
            var row = y * image.Stride;
            for (var x = 0; x < image.Width; x++)
            {
                pixels[row + x * 4 + 3] = 255;
            }
        }
        return pixels;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // 第一步：用 Win32 SetWindowPos 把窗口精确定位到 anchor 物理像素位置。
        // WPF Window.Show 期间会按 Left=-32000 把窗口放到屏幕外，Loaded 时再次覆盖到 anchor monitor 上。
        // 紧接着 UpdateLayout 让 WPF 在新 DPI 下重算 ActualWidth/ActualHeight，
        // 后续 BuildPolygons 的缩放系数 = innerSize / SourceWidth 才是当前屏 DPI 下的真实 DIP 比例
        PlaceWindowAtAnchorPhysical();
        UpdateLayout();

        // loading 态下没有 block 可绘制；BuildPolygons / RenderRecognizedTextOverlays 都会在
        // PopulateResult 中被调用。这里只先建好工具栏 / 文本面板的"识别中"状态
        if (!_isLoading)
        {
            BuildPolygons();
            if (_showingInlineText)
            {
                RenderRecognizedTextOverlays();
            }
        }

        // 工具条窗口在主窗 Loaded 之后再 Show：此时主窗 Left/Top/ActualWidth/ActualHeight 都是稳定值，
        // 可以根据主窗位置算出工具条初始位置。
        // base.Show 前先把工具条挪到屏幕外（-32000），避免 Show 期间在 OS 默认位置短暂闪现
        var aiAvailable = _aiText is not null && _aiText.CanRun;
        _toolbarWindow = new OcrResultToolbarWindow(this, aiAvailable);
        _toolbarWindow.Left = -32000;
        _toolbarWindow.Top = -32000;
        // 宿主未注入"切到截图"回调时把按钮置灰，避免点击无反应造成困惑
        _toolbarWindow.SetSwitchToScreenshotEnabled(_switchToScreenshot is not null);
        _toolbarWindow.SetReshootOcrEnabled(_reshootOcr is not null);
        _toolbarWindow.SetPinToScreenEnabled(_pinScreenshot is not null);
        // 工具条 SizeToContent=WidthAndHeight，必须先 Show 一次让它测出 ActualWidth/Height
        _toolbarWindow.Show();
        PositionToolbarInitially();

        if (_settings.OcrTextPanelVisible)
        {
            OpenTextPanel(initial: true, persistPreference: false);
        }
        else
        {
            _toolbarWindow.SetTextPanelVisible(false);
        }

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

        int insetPxX = (int)Math.Round(_outerInsetWidth * _anchorDpiX / 96.0);
        int insetPxY = (int)Math.Round(_outerInsetWidth * _anchorDpiY / 96.0);
        int x = _anchorPhysical.Left - insetPxX;
        int y = _anchorPhysical.Top - insetPxY;
        int w = _anchorPhysical.Width + insetPxX * 2;
        int h = _anchorPhysical.Height + insetPxY * 2;

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

        // 基于 anchorPhysical 而非"主窗外圈尺寸"算工具条位置：让工具条视觉上离截图实际边沿
        // 仅一个 8 DIP 间距，与 ScreenshotPreviewWindow.PositionToolbarInitially 完全一致。
        // 旧写法用 mainBottomPx（含 _outerInsetWidth 阴影外圈）作为锚，工具条会被推远 ~12 DIP，
        // 用户反馈"OCR 比截图工具栏更远"就是这条阴影 inset 在视觉上加了距离
        int gapOutsidePx = (int)Math.Round(8 * _anchorDpiY / 96.0);
        int gapInsidePx = (int)Math.Round(12 * _anchorDpiY / 96.0);
        // 水平居中于 anchor 截图区域（左右阴影 inset 等量，居中点等价于截图中心）
        int leftPx = _anchorPhysical.Left + (_anchorPhysical.Width - tbWPx) / 2;
        int topBelowPx = _anchorPhysical.Bottom + gapOutsidePx;
        int topAbovePx = _anchorPhysical.Top - gapOutsidePx - tbHPx;
        int topInsidePx = _anchorPhysical.Bottom - tbHPx - gapInsidePx;

        // anchor monitor 工作区，用于判断"工具条放在哪里能完整可见"
        var monitor = MonitorQuery.FromRect(
            _anchorPhysical.Left, _anchorPhysical.Top,
            _anchorPhysical.Right, _anchorPhysical.Bottom);

        // 三档自动选位：下方 → 上方 → 内底部，与 ScreenshotPreviewWindow 同策略
        int topPx;
        if (topBelowPx + tbHPx <= monitor.WorkBottom) topPx = topBelowPx;
        else if (topAbovePx >= monitor.WorkTop) topPx = topAbovePx;
        else topPx = topInsidePx;

        // 边界保护：避免工具条横向被推出 anchor monitor 工作区外（主窗贴屏左/右时会发生）
        if (leftPx < monitor.WorkLeft) leftPx = monitor.WorkLeft;
        if (leftPx + tbWPx > monitor.WorkRight) leftPx = monitor.WorkRight - tbWPx;
        if (topPx < monitor.WorkTop) topPx = monitor.WorkTop;
        if (topPx + tbHPx > monitor.WorkBottom) topPx = Math.Max(monitor.WorkTop, monitor.WorkBottom - tbHPx);

        NativeMethods.SetWindowPos(
            tbHwnd,
            NativeMethods.HWND_TOPMOST,
            leftPx, topPx, Math.Max(1, tbWPx), Math.Max(1, tbHPx),
            NativeMethods.SWP_NOACTIVATE);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _isClosed = true;
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

    /// <summary>Image / OverlayCanvas / RecognizedTextCanvas / TranslationCanvas 这些渲染层在 WindowBorderFrame 内部，
    /// 它们的可用尺寸 = Window 总尺寸 - 边框 - 阴影 margin（两侧各占 _outerInsetWidth）。
    /// polygon / 译文覆盖等所有空间映射统一用这个 inner size，否则 bordered 模式下会偏移、错位</summary>
    private (double Width, double Height) GetInnerSize()
    {
        double w = Math.Max(0, ActualWidth - _outerInsetWidth * 2);
        double h = Math.Max(0, ActualHeight - _outerInsetWidth * 2);
        return (w, h);
    }

    /// <summary>遍历 OcrResult.Blocks 在 OverlayCanvas 上建一一对应的 Polygon。
    /// Polygon.Tag 存储下标，让 mouse 回调能快速反查 block。
    /// 颜色按 default → 用户交互后再被 SetState 更新。
    ///
    /// 缩放系数用 Image 区域（Window 内部去掉 border）大小，而非 Window 整体。
    /// 否则 bordered 模式下 polygon 比截图大 2px、整体偏移 2px，与原图错位</summary>
    /// <summary>把 loading 态的结果窗升级为已识别态。
    /// 调用方负责保证 result 来自当前截图（OcrCaptureCoordinator 在 OCR 异步完成后回到 UI 线程调用）。
    ///
    /// 这一路径承担"截图预览 → OCR"切换的视觉无缝化：
    /// 1. 截图背景在窗口创建时就显示（避免识别期间空白闪烁）；
    /// 2. 工具栏先显示"识别中…"占位，复制 / 翻译按钮置灰；
    /// 3. OCR 完成回到这里时刷新 _result / 各数组、重建 polygon、激活工具栏全部能力。
    ///
    /// 不打断用户在 loading 时已经做的"切回截图 / 开始新识别"等操作 —— 那些操作会触发新窗体生命周期，
    /// 本窗口被关闭，PopulateResult 自然不会再回调到。
    /// 重复调用是允许的：例如用户在 loading 期间已经看到结果但又想换 provider 重识别 —— 直接 PopulateResult
    /// 第二次即可，内部会重建所有数组与 polygon。</summary>
    public void PopulateResult(OcrResult result, string? layoutFullText = null)
    {
        // loading 期间用户可能已经按了 Esc / "切到截图" / "开始新识别"，把当前 loading 窗 Close 掉了。
        // 此时延迟到达的 OCR 结果不应再操作 UI 元素（避免空 polygon 渲染、占用资源）
        if (_isClosed) return;

        // 极端情况：OCR 在 Window.Show() 返回后、Loaded 事件触发前完成 —— 此时 ActualWidth/Height
        // 还是 0，BuildPolygons 的缩放系数会算出 0，polygon 全部坍缩到 (0,0)。
        // 订阅 Loaded 让真实 layout 完成后再回调 PopulateResult，避免空 polygon 渲染
        if (!IsLoaded)
        {
            RoutedEventHandler? handler = null;
            handler = (_, _) =>
            {
                Loaded -= handler;
                PopulateResult(result, layoutFullText);
            };
            Loaded += handler;
            return;
        }

        _result = result;
        _layoutFullText = string.IsNullOrWhiteSpace(layoutFullText) ? "" : layoutFullText.Trim();

        // 重新按新 block 数分配所有"per-block"数组。
        // 旧的选中 / 翻译缓存 / inline text overlay 都失效（block 含义变了），全部清掉
        _selected = new bool[result.Blocks.Count];
        _translationOverlays = new Border?[result.Blocks.Count];
        _recognizedTextOverlays = new Border?[result.Blocks.Count];
        _translatedText = new string?[result.Blocks.Count];
        _hoverIndex = -1;
        _marqueeStart = null;
        _isDragging = false;
        _showingTranslation = false;
        _translating = false;
        // TranslationCanvas / RecognizedTextCanvas 上的 overlay 是 loading 升级前留下的（理论上不会有，
        // 但 PopulateResult 也允许 populated → populated 二次调用，所以这里清干净更稳）
        TranslationCanvas.Children.Clear();
        RecognizedTextCanvas.Children.Clear();
        MarqueeRect.Visibility = Visibility.Collapsed;

        _isLoading = false;
        // 关掉截图背景中央的"识别中…"提示；接下来 BuildPolygons 会让 polygon 出现在截图上
        LoadingHintBorder.Visibility = Visibility.Collapsed;
        BuildPolygons();
        if (_showingInlineText)
        {
            RenderRecognizedTextOverlays();
        }
        UpdateStatusBar();
    }

    public void CommandClose() => CloseSelf("command");

    // ============== 状态条 / Toast ==============

    private void UpdateStatusBar()
    {
        int total = _result.Blocks.Count;
        int sel = SelectedCount();

        // loading 态：截图背景已经显示，但 OCR 还在跑 —— 工具栏 / 文本面板 / 右键菜单都按"识别中"渲染。
        // 复制 / 翻译类按钮全部置灰；"切到截图"和"开始新识别"保持可用，方便用户在长时识别时放弃当前任务
        if (_isLoading)
        {
            _toolbarWindow?.SetStatus("识别中…");
            _toolbarWindow?.SetCopySelectedEnabled(false);
            _toolbarWindow?.SetCopyAllEnabled(false);
            _toolbarWindow?.SetTranslateToggleEnabled(false);
            _toolbarWindow?.SetTranslateToggleShowingTranslation(false);
            _toolbarWindow?.SetInlineTextVisible(_showingInlineText);

            if (_textPanelWindow is not null && _textPanelWindow.Visibility == Visibility.Visible)
            {
                UpdateTextPanelContent();
            }

            MiCopySelected.IsEnabled = false;
            MiClearSel.IsEnabled = false;
            MiCopyAll.IsEnabled = false;
            MiSelectAll.IsEnabled = false;
            MiTranslateAll.IsEnabled = false;
            MiTranslateSelected.IsEnabled = false;
            var loadingAiAvailable = _aiText is not null && _aiText.CanRun;
            var loadingVis = loadingAiAvailable ? Visibility.Visible : Visibility.Collapsed;
            MiTranslateAll.Visibility = loadingVis;
            MiTranslateSelected.Visibility = loadingVis;
            return;
        }

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
        _toolbarWindow?.SetTranslateToggleEnabled(total > 0 && !_translating);
        _toolbarWindow?.SetTranslateToggleShowingTranslation(_showingTranslation);
        _toolbarWindow?.SetInlineTextVisible(_showingInlineText);

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
        MiTranslateAll.IsEnabled = total > 0 && !_translating;
        MiTranslateSelected.IsEnabled = sel > 0 && !_translating;

        // AI 未启用时把右键菜单的翻译项也隐藏
        var aiAvailable = _aiText is not null && _aiText.CanRun;
        var vis = aiAvailable ? Visibility.Visible : Visibility.Collapsed;
        MiTranslateAll.Visibility = vis;
        MiTranslateSelected.Visibility = vis;
    }

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        ToastBorder.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
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

