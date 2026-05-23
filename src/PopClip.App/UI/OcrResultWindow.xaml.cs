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

    /// <summary>打开右侧识别文本面板（不存在则新建，已存在则显示并刷新）。
    /// initial=true 时按"主窗右侧"策略放置；后续切换显隐时尊重用户拖动后的位置。
    ///
    /// 创建顺序与 PositionToolbarInitially 保持一致：先 Show 让面板测出 ActualWidth/Height，
    /// 再用 Win32 SetWindowPos 把面板按物理像素绝对定位到目标位置，规避 PerMonitor V2 下
    /// Window.Left/Top setter 用主屏 DPI 解读 DIP 的跨屏跳屏问题</summary>
    public void OpenTextPanel(bool initial, bool persistPreference = true)
    {
        if (_textPanelWindow is null)
        {
            // 面板复制走的是"当前显示内容"，自然涵盖原文 / 译文两种态。
            // 翻译回调只在 AI 启用时传入；面板根据回调是否为 null 决定是否显示翻译按钮
            Func<string, Task<string?>>? translateAsync = (_aiText is not null && _aiText.CanRun)
                ? async (source) =>
                {
                    if (string.IsNullOrWhiteSpace(source)) return null;
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    try
                    {
                        var list = await _aiText!.TranslateBatchAsync(new[] { source }, cts.Token).ConfigureAwait(true);
                        return list.Count > 0 ? (list[0] ?? "").Trim() : null;
                    }
                    catch (OperationCanceledException)
                    {
                        ShowToast("翻译超时（60 秒）");
                        return null;
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("panel translate failed", ("err", ex.Message));
                        ShowToast("翻译失败：" + ex.Message);
                        return null;
                    }
                }
                : null;

            _textPanelWindow = new OcrResultTextPanelWindow(
                onCopyAll: () =>
                {
                    var text = _textPanelWindow?.CurrentDisplayedText ?? "";
                    if (string.IsNullOrEmpty(text))
                    {
                        text = EffectiveFullText();
                        if (string.IsNullOrEmpty(text)) text = JoinAllText();
                    }
                    if (!string.IsNullOrEmpty(text)) CopyTextSilently(text);
                    // 反馈走面板自己的 ShowLocalToast — 比飞回主结果窗中央 toast 更贴近用户操作点
                    var msg = string.IsNullOrEmpty(text) ? "没有识别到内容" : $"已复制 / {text.Length} 字";
                    _textPanelWindow?.ShowLocalToast(msg);
                },
                onClose: () =>
                {
                    // 用户点关闭：直接收起面板，保留窗口对象方便下次切换显示时复用位置 / 内容
                    HideTextPanel();
                },
                translateAsync: translateAsync);
            _textPanelWindow.Owner = this;
            // 关键：刚 new 出来时不要在 OS 默认 (0,0) 闪现，先挪屏外再 Show。
            // 默认 Width 加宽到 480 让一行能容纳约 30~40 个中文 / 60~80 个 ASCII，
            // 整段段落不被频繁折行，与 OCR Quick 气泡的阅读宽度保持一致
            _textPanelWindow.Left = -32000;
            _textPanelWindow.Top = -32000;
            _textPanelWindow.Width = 480;
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
        PersistTextPanelPreference(visible: true, persistPreference);
    }

    public void HideTextPanel(bool persistPreference = true)
    {
        try { _textPanelWindow?.Hide(); } catch { }
        _toolbarWindow?.SetTextPanelVisible(false);
        PersistTextPanelPreference(visible: false, persistPreference);
    }

    private void PersistTextPanelPreference(bool visible, bool persistPreference)
    {
        if (!persistPreference || _settings.OcrTextPanelVisible == visible) return;
        _settings.OcrTextPanelVisible = visible;
        try { _saveSettings?.Invoke(); }
        catch (Exception ex) { _log.Warn("save ocr text panel preference failed", ("err", ex.Message)); }
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

        int insetPxX = (int)Math.Round(_outerInsetWidth * _anchorDpiX / 96.0);
        int insetPxY = (int)Math.Round(_outerInsetWidth * _anchorDpiY / 96.0);
        int mainLeftPx = _anchorPhysical.Left - insetPxX;
        int mainTopPx = _anchorPhysical.Top - insetPxY;
        int mainWidthPx = _anchorPhysical.Width + insetPxX * 2;
        int mainHeightPx = _anchorPhysical.Height + insetPxY * 2;
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
    /// <summary>同步预览面板的"源文本"。一直传原文，不传 Effective（带主窗 polygon overlay 译文的）—
    /// 否则工具栏切到译文模式时面板正文会被主窗 polygon overlay 污染。
    /// 预览面板拥有自己的翻译按钮和缓存，原文 / 译文显示状态由面板自身维护，与工具栏完全独立。
    /// SetContent 内部会自动判断 _originalText 是否变化、是否需要让缓存失效</summary>
    private void UpdateTextPanelContent()
    {
        if (_textPanelWindow is null) return;

        // loading 态下还没有任何 block，面板内显示"识别中…"占位。
        // 避免空白面板让用户误以为窗口卡死
        if (_isLoading)
        {
            _textPanelWindow.SetContent("识别中…");
            _textPanelWindow.SetMeta("OCR 正在处理截图");
            return;
        }

        int sel = SelectedCount();
        string body;
        string meta;
        if (sel > 0)
        {
            body = JoinSelectedOriginalText();
            meta = $"已选 {sel}/{_result.Blocks.Count} 段 · {body.Length} 字";
        }
        else
        {
            body = OriginalFullText();
            meta = $"{_result.Blocks.Count} 段 · {body.Length} 字";
        }
        _textPanelWindow.SetContent(body);
        _textPanelWindow.SetMeta(meta);
    }

    private string JoinSelectedOriginalText()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _result.Blocks.Count; i++)
        {
            if (!_selected[i]) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(_result.Blocks[i].Text);
        }
        return sb.ToString();
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

    /// <summary>工具栏按钮触发：显示 / 隐藏结果图上的 OCR 原文贴图，并写回用户偏好。</summary>
    public void CommandToggleInlineText(Action<string>? toastSink = null)
    {
        _showingInlineText = !_showingInlineText;
        if (_showingInlineText)
        {
            RenderRecognizedTextOverlays();
            toastSink?.Invoke("已显示识别原文贴图");
        }
        else
        {
            HideRecognizedTextOverlays();
            toastSink?.Invoke("已隐藏识别原文贴图");
        }

        _settings.OcrInlineTextVisible = _showingInlineText;
        _saveSettings?.Invoke();
        UpdateStatusBar();
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
                // 默认态比旧的 0.18 略加深，让"哪些行被识别成 block"一眼看清；
                // 与 Hover (0.45) / Selected (0.7) 的对比仍足够明显，不会与 hover 视觉混淆
                Opacity = 0.32,
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
                p.Opacity = 0.32;
                p.Stroke = Brushes.Transparent;
                p.StrokeThickness = 1;
                break;
            case BoxState.Hover:
                p.Opacity = 0.50;
                p.Stroke = (Brush?)TryFindResource("ToolbarForeground") ?? Brushes.White;
                p.StrokeThickness = 1;
                break;
            case BoxState.Selected:
                p.Opacity = 0.72;
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
            // 复制时不再弹中央 toast：选中态 polygon 高亮 + 工具栏状态胶囊
            // 已足够表达"复制成功"，再叠加 toast 反而遮挡内容，分散注意力
            CopyTextSilently(_result.Blocks[idx].Text);
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
        // loading 态下不接受任何 marquee 操作：还没有 polygon 可以选中，
        // 启动 marquee 也会让"识别中…"画面上突然冒出一个虚框，体验混乱
        if (_isLoading) return;

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
        _marqueeStart = null;
        _isDragging = false;
        MarqueeRect.Visibility = Visibility.Collapsed;
        // 框选完成不弹中央 toast：选中 polygon 自身已变深，
        // 工具栏状态胶囊也会同步显示"已选 X/Y"，无需再叠 toast 干扰阅读
        UpdateStatusBar();
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
        // Escape 在 loading 态也必须有效（用户取消识别），其他快捷键依赖已识别结果故 loading 时屏蔽
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseSelf("escape");
            return;
        }
        if (_isLoading) return;

        switch (e.Key)
        {
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
    private void OnTranslateSelected(object sender, RoutedEventArgs e) => CommandTranslateMenu(selectedOnly: true);
    private void OnTranslateAll(object sender, RoutedEventArgs e) => CommandTranslateMenu(selectedOnly: false);

    /// <summary>右键菜单"翻译全部 / 翻译选中"入口。
    /// 与工具栏 toggle 走相同的 ShowTranslationAsync，自动复用缓存。
    /// selectedOnly=true 但用户未选任何段时给 toast 提示</summary>
    private void CommandTranslateMenu(bool selectedOnly)
    {
        if (_aiText is null || !_aiText.CanRun)
        {
            ShowToast("请先在设置启用 AI 并配置 API Key");
            return;
        }
        if (_translating) { ShowToast("翻译进行中，请稍候"); return; }

        List<int> indices;
        if (selectedOnly)
        {
            indices = new();
            for (int i = 0; i < _selected.Length; i++) if (_selected[i]) indices.Add(i);
            if (indices.Count == 0)
            {
                ShowToast("没有选中任何文本（点高亮 / 拖框先选）");
                return;
            }
        }
        else
        {
            indices = Enumerable.Range(0, _result.Blocks.Count).ToList();
        }
        _ = ShowTranslationAsync(indices);
    }

    private void OnClearSelectionMenu(object sender, RoutedEventArgs e)
    {
        ClearSelection();
        RefreshAllStates();
        UpdateStatusBar();
    }

    // ============== Command 方法（工具条 / 右键菜单 / 键盘共用入口）==============

    /// <summary>复制选中段落到剪贴板。toastSink 为 null 时反馈走主窗中央 toast；
    /// 非 null 时（如工具栏按钮调用时传自己的 ShowLocalToast）反馈贴近触发点</summary>
    public void CommandCopySelected(Action<string>? toastSink = null)
    {
        var sink = toastSink ?? ShowToast;
        var text = JoinSelectedText();
        if (string.IsNullOrEmpty(text))
        {
            sink("没有选中任何文本");
            return;
        }
        CopyTextSilently(text);
        sink($"已复制 {SelectedCount()} 段 / {text.Length} 字");
    }

    public void CommandCopyAll(Action<string>? toastSink = null)
    {
        var sink = toastSink ?? ShowToast;
        var text = EffectiveFullText();
        if (string.IsNullOrEmpty(text)) text = JoinAllText();
        if (string.IsNullOrEmpty(text))
        {
            sink("没有识别到内容");
            return;
        }
        CopyTextSilently(text);
        sink($"已复制全部 / {text.Length} 字");
    }

    public void CommandCopyScreenshot(Action<string>? toastSink = null)
    {
        var sink = toastSink ?? ShowToast;
        try
        {
            _clipboard.SetImagePngBytes(_screenshot.GetPngBytes());
            sink("截图已复制");
        }
        catch (Exception ex)
        {
            _log.Warn("ocr screenshot copy failed", ("err", ex.Message));
            sink("复制截图失败：" + ex.Message);
        }
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

    /// <summary>工具栏"切到截图预览"按钮入口。
    /// 关闭结果窗后由 Coordinator 用同一张截图打开 ScreenshotPreviewWindow，
    /// 让用户能继续做"复制图片 / 保存为 PNG / 再次 OCR"等截图侧操作。
    ///
    /// 切换流程改造：
    /// 1) 把当前结果图在屏上的物理像素 rect 传给回调，让 Coordinator 用同一个位置打开截图预览，
    ///    保留用户对结果窗的拖动 / 跨屏位置惯性；
    /// 2) 本窗不在这里 Close —— 由 Coordinator 在新预览窗 Loaded 之后再统一关闭，
    ///    让"高亮 OCR 图 → 原始截图"切换视觉上几乎无缝衔接（同位置叠两层短暂共存）</summary>
    public void CommandSwitchToScreenshot()
    {
        if (_switchToScreenshot is null) return;
        _log.Info("ocr result switch-to-screenshot triggered");
        var anchor = GetCurrentImagePhysicalRect();
        try { _switchToScreenshot(anchor); }
        catch (Exception ex) { _log.Warn("switch-to-screenshot failed", ("err", ex.Message)); }
    }

    /// <summary>工具栏"开始新识别"按钮入口：交给 Coordinator 关闭本窗 + 拉起新的选区状态机。
    /// 先 Hide 本窗 + 工具栏让屏幕保持干净，再触发 reshoot 回调；
    /// 与"快捷键 / 托盘"路径不同，这条路径需要主动隐藏旧结果窗，避免 OCR 选区蒙层下还看到旧的高亮图</summary>
    public void CommandReshootOcr()
    {
        if (_reshootOcr is null) return;
        _log.Info("ocr result reshoot triggered");
        try
        {
            if (_toolbarWindow is not null) { try { _toolbarWindow.Hide(); } catch { } }
            if (_textPanelWindow is not null) { try { _textPanelWindow.Hide(); } catch { } }
            Hide();
            _reshootOcr();
        }
        catch (Exception ex) { _log.Warn("reshoot ocr failed", ("err", ex.Message)); }
    }

    /// <summary>工具栏"钉到桌面"按钮入口：把当前 OCR 结果底层的截图复制一份到 PinnedScreenshotWindow。
    /// 与"切到截图预览"的差别：钉图不在乎 OCR 结果，只复用截图像素；OCR 结果窗本身保留不动，
    /// 让用户能继续在结果窗交互而钉图独立漂浮在桌面。
    /// 重复点击会创建多个钉图实例，符合"工作台多张截图"使用直觉</summary>
    public void CommandPinToScreen()
    {
        if (_pinScreenshot is null) return;
        var anchor = GetCurrentImagePhysicalRect();
        try { _pinScreenshot(anchor); }
        catch (Exception ex) { _log.Warn("pin-to-screen failed", ("err", ex.Message)); }
    }

    /// <summary>读取当前结果图在屏幕上的物理像素位置。
    /// Window 内部从外到内的 DIP 层次：_shadowMargin（阴影留白）+ _borderWidth（主题边框）→ 图像区域。
    /// 用 GetWindowRect 取窗口物理矩形 + _outerInsetWidth 物理像素偏移，得到图像区域左上像素坐标；
    /// 宽高直接复用最初的 anchor 尺寸（图像内容未变形）。
    ///
    /// 用于"切到截图预览"按钮：让新预览窗在用户拖动 / 跨屏挪动后的当前位置打开，而不是闪回最初的框选位置</summary>
    private WinRectangle GetCurrentImagePhysicalRect()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return _anchorPhysical;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return _anchorPhysical;

        int insetPxX = (int)Math.Round(_outerInsetWidth * _anchorDpiX / 96.0);
        int insetPxY = (int)Math.Round(_outerInsetWidth * _anchorDpiY / 96.0);
        int x = rect.Left + insetPxX;
        int y = rect.Top + insetPxY;
        return new WinRectangle(x, y, _anchorPhysical.Width, _anchorPhysical.Height);
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

    /// <summary>工具栏"翻译 / 原文"切换按钮入口。
    /// 状态机：原文 → 译文（按需调 AI） → 原文（隐藏 overlay） → 译文（直接走缓存）……
    ///
    /// 决定翻译范围：有选中段则译选中段，否则译全部段。这与"复制选中 / 复制全部"自适应行为一致，
    /// 只用一个按钮即可承载两种意图，UI 更紧凑。
    /// 缓存复用：_translatedText 在窗口生命周期内保留，多次切回译文显示时不需要重新请求 AI；
    /// 仅当用户主动调 CommandTranslateClear 清空，或新选中段未曾翻译时，才会发请求</summary>
    public void CommandToggleTranslation(Action<string>? toastSink = null)
    {
        if (_aiText is null || !_aiText.CanRun)
        {
            toastSink?.Invoke("请先在设置启用 AI 并配置 API Key");
            return;
        }
        if (_translating) { toastSink?.Invoke("翻译进行中，请稍候"); return; }

        if (_showingTranslation)
        {
            // 当前是译文模式 → 切回原文。仅清理 overlay 视觉层，保留 _translatedText 缓存
            HideAllTranslationOverlays();
            _showingTranslation = false;
            UpdateStatusBar();
            toastSink?.Invoke("已切换为原文显示");
            return;
        }

        // 当前是原文模式 → 切到译文。先决定本次要展示译文的 block 集合
        var indices = new List<int>();
        for (int i = 0; i < _selected.Length; i++) if (_selected[i]) indices.Add(i);
        if (indices.Count == 0)
        {
            // 无选中 → 全部段都译
            indices = Enumerable.Range(0, _result.Blocks.Count).ToList();
        }
        if (indices.Count == 0)
        {
            toastSink?.Invoke("没有识别到内容");
            return;
        }

        _ = ShowTranslationAsync(indices, toastSink);
    }

    // ============== 翻译 ==============

    /// <summary>翻译并渲染指定 block 集合的译文 overlay。
    /// - 命中缓存的段直接渲染（_translatedText[i] 不为空时跳过 AI 请求）；
    /// - 未缓存的段批量调用 AI 翻译，结果写入缓存。
    /// 全部成功后切到译文显示模式</summary>
    private async Task ShowTranslationAsync(IReadOnlyList<int> indices, Action<string>? toastSink = null)
    {
        var needTranslate = indices.Where(i => string.IsNullOrEmpty(_translatedText[i])).ToList();

        _translating = true;
        _toolbarWindow?.SetTranslateToggleEnabled(false);
        if (needTranslate.Count > 0)
        {
            _toolbarWindow?.SetStatus($"翻译中… ({needTranslate.Count} 段)");
        }

        try
        {
            if (needTranslate.Count > 0)
            {
                var sources = needTranslate.Select(i => _result.Blocks[i].Text).ToList();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                IReadOnlyList<string> translations;
                try
                {
                    translations = await _aiText!.TranslateBatchAsync(sources, cts.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    toastSink?.Invoke("翻译超时（60 秒）");
                    return;
                }
                catch (Exception ex)
                {
                    _log.Warn("ocr translate failed", ("err", ex.Message), ("blocks", needTranslate.Count));
                    toastSink?.Invoke("翻译失败：" + ex.Message);
                    return;
                }

                for (int k = 0; k < needTranslate.Count && k < translations.Count; k++)
                {
                    var bi = needTranslate[k];
                    var translated = (translations[k] ?? "").Trim();
                    if (translated.Length == 0) continue;
                    _translatedText[bi] = translated;
                }
            }

            // 渲染所有 indices 的 overlay（缓存命中即直接画，没命中刚才已请求并写入缓存）
            int rendered = 0;
            foreach (var i in indices)
            {
                var text = _translatedText[i];
                if (string.IsNullOrEmpty(text)) continue;
                RenderTranslationOverlay(i, text);
                rendered++;
            }

            _showingTranslation = rendered > 0;
            int cached = indices.Count - needTranslate.Count;
            if (needTranslate.Count > 0 && cached > 0)
            {
                toastSink?.Invoke($"已翻译 {needTranslate.Count} 段（{cached} 段走缓存）");
            }
            else if (needTranslate.Count > 0)
            {
                toastSink?.Invoke($"已翻译 {needTranslate.Count} 段");
            }
            else
            {
                toastSink?.Invoke($"已显示译文 {indices.Count} 段");
            }
        }
        finally
        {
            _translating = false;
            UpdateStatusBar();
        }
    }

    /// <summary>把 TranslationCanvas 上所有 overlay 移除，但保留 _translatedText 缓存。
    /// 切换"译文 → 原文"显示时使用，让二次切回译文时直接复用缓存</summary>
    private void HideAllTranslationOverlays()
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

    private void RenderRecognizedTextOverlays()
    {
        HideRecognizedTextOverlays();
        for (var i = 0; i < _result.Blocks.Count; i++)
        {
            RenderRecognizedTextOverlay(i);
        }
    }

    private void HideRecognizedTextOverlays()
    {
        for (var i = 0; i < _recognizedTextOverlays.Length; i++)
        {
            if (_recognizedTextOverlays[i] is { } b)
            {
                RecognizedTextCanvas.Children.Remove(b);
                _recognizedTextOverlays[i] = null;
            }
        }
    }

    /// <summary>把 OCR 原文贴到对应 block 的 AABB 区域，方便用户直接核对识别文本与图像位置。</summary>
    private void RenderRecognizedTextOverlay(int blockIndex)
    {
        if (blockIndex < 0 || blockIndex >= _result.Blocks.Count) return;
        if (_result.SourceWidth <= 0 || _result.SourceHeight <= 0) return;

        var text = _result.Blocks[blockIndex].Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        var (innerW, innerH) = GetInnerSize();
        double sx = innerW / _result.SourceWidth;
        double sy = innerH / _result.SourceHeight;
        var (l, t, r, b) = _result.Blocks[blockIndex].Box.AABB();
        double left = l * sx;
        double top = t * sy;
        double width = Math.Max(20, (r - l) * sx);
        double height = Math.Max(12, (b - t) * sy);
        double fontSize = Math.Max(8, Math.Min(34, height * 0.60));

        var tb = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontFamily = (FontFamily?)TryFindResource("ToolbarFontFamily") ?? new FontFamily("Segoe UI"),
            Foreground = (Brush?)TryFindResource("ToolbarForeground") ?? Brushes.White,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(4, 0, 4, 0),
        };

        var border = new Border
        {
            Width = width,
            Height = height,
            Background = (Brush?)TryFindResource("ToolbarToastBackground") ?? Brushes.Black,
            BorderBrush = (Brush?)TryFindResource("ToolbarAccentSoft") ?? Brushes.SteelBlue,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Opacity = 0.94,
            Child = tb,
            ToolTip = text,
        };
        Canvas.SetLeft(border, left);
        Canvas.SetTop(border, top);
        RecognizedTextCanvas.Children.Add(border);
        _recognizedTextOverlays[blockIndex] = border;
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
            // 同时清掉文本缓存：下次切到译文模式会重新调 AI（"清除"= 完全重置）
            _translatedText[i] = null;
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
