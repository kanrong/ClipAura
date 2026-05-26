using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PopClip.App.Services;
using PopClip.Core.Logging;
using PopClip.Hooks.Interop;
using PopClip.Hooks.Window;
using WinRectangle = System.Drawing.Rectangle;

namespace PopClip.App.UI;

/// <summary>钉图窗：把已截好的图像固定到桌面，作为参考层长期可见。
///
/// 与 ScreenshotPreviewWindow 的差异：
/// - 不抢焦点：ShowActivated=False + WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW，不进 Alt+Tab；
/// - ESC 关闭：单 ESC 触发 Close。难点是 WS_EX_NOACTIVATE 阻塞键盘消息送到本窗的消息队列 ——
///   解决办法是在用户主动点击钉图时临时摘掉该样式 + SetForegroundWindow，让窗口能收到 WM_KEYDOWN；
///   失焦时（Deactivated）立刻把样式还回去，避免污染 Alt+Tab、避免抢前台
/// - 多窗口共存：由 PinnedScreenshotManager 持有，关一个不影响其他实例；
/// - 双击图像 → 触发 OCR 回调（与 ScreenshotPreviewWindow 的 CommandOcr 同款链路）；
/// - Ctrl+滚轮缩放，10% ~ 400%；右键菜单提供 透明度 / 置顶 / 缩放 / 编辑 等。
///
/// 缩放实现：用 Image 的 Width/Height 显式数值，让 WPF 走 NearestNeighbor 放大，
/// 200% 时文字仍清晰可读；不用 LayoutTransform，那会破坏 Border 阴影边界</summary>
internal partial class PinnedScreenshotWindow : Window
{
    private static readonly double[] Opacities = { 1.0, 0.8, 0.6, 0.4 };

    private readonly ILog _log;
    private readonly byte[] _pngBytes;
    private readonly uint _anchorDpiX;
    private readonly uint _anchorDpiY;
    private readonly int _imagePixelWidth;
    private readonly int _imagePixelHeight;
    private readonly ClipboardWriter _clipboard;
    private readonly Action<PinnedScreenshotWindow> _onClosed;

    /// <summary>双击图像时回调。Coordinator 在该回调里走 RecognizeImageAsync。
    /// 参数 anchorRect = 当前窗口下的图像物理像素区域，让 OCR 结果窗在原位弹出</summary>
    private readonly Action<PinnedScreenshotWindow, WinRectangle>? _onOcrRequested;

    private nint _hwnd;

    /// <summary>当前缩放档位，1.0 = 1:1 物理像素。范围 0.1 ~ 4.0；
    /// 由 Ctrl+滚轮 / 右键菜单档位修改，影响 PreviewImage 的 Width/Height</summary>
    private double _scale = 1.0;

    /// <summary>用户设定的"非悬停态"透明度。鼠标悬停时强制 1.0，离开时恢复为 _userOpacity</summary>
    private double _userOpacity = 1.0;

    private bool _hovering;
    private bool _closed;

    public PinnedScreenshotWindow(
        ILog log,
        byte[] pngBytes,
        uint anchorDpiX,
        uint anchorDpiY,
        ClipboardWriter clipboard,
        Action<PinnedScreenshotWindow> onClosed,
        Action<PinnedScreenshotWindow, WinRectangle>? onOcrRequested)
    {
        _log = log;
        _pngBytes = pngBytes;
        _anchorDpiX = anchorDpiX == 0 ? 96 : anchorDpiX;
        _anchorDpiY = anchorDpiY == 0 ? 96 : anchorDpiY;
        _clipboard = clipboard;
        _onClosed = onClosed;
        _onOcrRequested = onOcrRequested;

        InitializeComponent();

        var bitmap = LoadBitmap(_pngBytes, _anchorDpiX, _anchorDpiY);
        _imagePixelWidth = bitmap.PixelWidth;
        _imagePixelHeight = bitmap.PixelHeight;
        PreviewImage.Source = bitmap;

        // 1:1 物理像素 = pixelWidth * 96 / dpi DIP，使 WPF Stretch=Fill 时不发生像素再采样
        ApplyScale(1.0);

        // 初始位置在屏外，等 Loaded 后由 ShowAnchored 决定
        Left = -32000;
        Top = -32000;
        SizeToContent = SizeToContent.WidthAndHeight;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosedInternal;
        KeyDown += OnKeyDown;
        Deactivated += OnWindowDeactivated;
        Frame.MouseLeftButtonDown += OnFrameLeftButtonDown;
        PreviewImage.MouseLeftButtonDown += OnImageLeftButtonDown;
        MouseEnter += OnWindowMouseEnter;
        MouseLeave += OnWindowMouseLeave;
        PreviewMouseWheel += OnPreviewMouseWheel;

        UpdateMenuChecks();
    }

    public byte[] PngBytes => _pngBytes;

    private static BitmapSource LoadBitmap(byte[] pngBytes, uint dpiX, uint dpiY)
    {
        // 复用 ScreenshotPreviewWindow 的 PNG → BitmapSource 路径，DPI 校准让 1:1 显示像素精确
        using var ms = new MemoryStream(pngBytes);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        BitmapSource source = frame;
        if (frame.Format != PixelFormats.Bgra32 && frame.Format != PixelFormats.Bgr32 && frame.Format != PixelFormats.Pbgra32)
        {
            source = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        }
        int stride = source.PixelWidth * (source.Format.BitsPerPixel / 8);
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        var bmp = BitmapSource.Create(
            source.PixelWidth, source.PixelHeight,
            dpiX, dpiY,
            source.Format, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd != 0) WindowStyleHelper.ApplyNoActivateToolWindow(_hwnd);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLayout();
    }

    /// <summary>把窗口物理像素绝对定位到 anchor 区域，主图区域刚好覆盖原始截图位置。
    /// 与 ScreenshotPreviewWindow.PlaceWindowAtAnchorPhysical 同款算法</summary>
    public void ShowAtAnchor(WinRectangle anchorPhysical)
    {
        // 必须先 Show 才能 SetWindowPos：window 句柄在 Show 之前不存在
        if (!IsVisible)
        {
            Left = -32000;
            Top = -32000;
            base.Show();
            UpdateLayout();
            SizeToContent = SizeToContent.Manual;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;

        double dpiScaleX = _anchorDpiX / 96.0;
        double dpiScaleY = _anchorDpiY / 96.0;

        var imageOffsetDip = PreviewImage.TranslatePoint(new Point(0, 0), this);
        int imageOffsetPxX = (int)Math.Round(imageOffsetDip.X * dpiScaleX);
        int imageOffsetPxY = (int)Math.Round(imageOffsetDip.Y * dpiScaleY);

        var windowSizeDip = MeasureContentWindowSizeDip();
        int winWidthPx = (int)Math.Ceiling(windowSizeDip.Width * dpiScaleX);
        int winHeightPx = (int)Math.Ceiling(windowSizeDip.Height * dpiScaleY);
        int winLeftPx = anchorPhysical.Left - imageOffsetPxX;
        int winTopPx = anchorPhysical.Top - imageOffsetPxY;

        WindowStyleHelper.ShowNoActivate(hwnd, winLeftPx, winTopPx, Math.Max(1, winWidthPx), Math.Max(1, winHeightPx));
    }

    private void OnClosedInternal(object? sender, EventArgs e)
    {
        if (_closed) return;
        _closed = true;
        try { _onClosed(this); } catch { }
    }

    private void OnFrameLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        EnsureKeyboardFocusable();
        WindowDragHelper.BeginDrag(this);
    }

    /// <summary>双击图像 → 触发 OCR；单击执行 DragMove。
    /// 把 OCR 入口收敛到双击：避免与单击拖动冲突，与 macOS / Snipaste 双击进入操作面板的习惯一致</summary>
    private void OnImageLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount >= 2)
        {
            e.Handled = true;
            CommandOcr();
            return;
        }
        EnsureKeyboardFocusable();
        WindowDragHelper.BeginDrag(this);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // 键盘消息能到这里的前提是用户刚点过钉图：EnsureKeyboardFocusable 把 WS_EX_NOACTIVATE 暂时摘了，
        // SetForegroundWindow 让本窗成为前台 → WM_KEYDOWN 才会派发过来。失焦时由 OnWindowDeactivated
        // 把样式加回去，保持"不进 Alt+Tab、不抢前台"的钉图初衷
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            CommandCopy();
        }
    }

    /// <summary>把当前是否处于"已临时获得键盘焦点"的状态记下来。
    /// true = WS_EX_NOACTIVATE 已被摘掉、窗口可接收键盘消息；false = 仍然是不可激活态。
    /// 用 bool 而不是每次读 GWL_EXSTYLE，是因为 SetWindowLong 多次重复设置同样的值会触发
    /// WM_STYLECHANGED / 重新计算非客户区，开销小但没必要</summary>
    private bool _keyboardFocusedTemporarily;

    /// <summary>临时把钉图窗升级为"可激活"，让 OS 把键盘消息送过来。
    /// 实现路径：摘掉 WS_EX_NOACTIVATE → SetForegroundWindow → Focus()。这条路径只在用户主动
    /// 点击钉图时触发，不会主动抢其它前台应用的焦点</summary>
    private void EnsureKeyboardFocusable()
    {
        if (_keyboardFocusedTemporarily) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;

        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((ex & NativeMethods.WS_EX_NOACTIVATE) != 0)
        {
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex & ~NativeMethods.WS_EX_NOACTIVATE);
        }
        // SetForegroundWindow 在严格条件下才会成功（前台进程必须是当前进程 / 用户输入触发），
        // 这里是用户鼠标点击场景，满足"输入驱动"条件，调用一般会成功
        NativeMethods.SetForegroundWindow(hwnd);
        Activate();
        Focus();
        _keyboardFocusedTemporarily = true;
    }

    /// <summary>窗口失去焦点（点到桌面 / 其他应用 / 别的钉图）时立刻恢复 WS_EX_NOACTIVATE：
    /// 让钉图回到"不进 Alt+Tab、点击其他窗口时不抢焦"的默认行为。
    /// 不在 LostKeyboardFocus 而在 Deactivated 上做：前者只跟踪 WPF 内部键盘焦点，会被弹出菜单等触发，
    /// 后者只在 Win32 层窗口真的失活时触发，更准</summary>
    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (!_keyboardFocusedTemporarily) return;
        _keyboardFocusedTemporarily = false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((ex & NativeMethods.WS_EX_NOACTIVATE) == 0)
        {
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex | NativeMethods.WS_EX_NOACTIVATE);
        }
    }

    private void OnWindowMouseEnter(object sender, MouseEventArgs e)
    {
        _hovering = true;
        ApplyOpacity();
    }

    private void OnWindowMouseLeave(object sender, MouseEventArgs e)
    {
        _hovering = false;
        ApplyOpacity();
    }

    /// <summary>Ctrl+滚轮缩放：每格放大/缩小 10%，clamp 到 [0.1, 4.0]。
    /// 不带 Ctrl 时不响应，让用户在编辑应用 / 浏览器场景下滚动桌面其它窗口不会误改钉图大小</summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        e.Handled = true;
        var delta = e.Delta > 0 ? 0.1 : -0.1;
        ApplyScale(_scale + delta);
        // 缩放后窗口大小变化：右键菜单档位也同步显示
        UpdateMenuChecks();
    }

    private void ApplyScale(double newScale)
    {
        var clamped = Math.Max(0.1, Math.Min(4.0, newScale));
        _scale = clamped;
        // PreviewImage 控件大小：1:1 物理像素 = pixelWidth * 96 / dpi DIP；缩放再乘 _scale。
        var dipW = _imagePixelWidth * 96.0 / _anchorDpiX * _scale;
        var dipH = _imagePixelHeight * 96.0 / _anchorDpiY * _scale;
        PreviewImage.Width = dipW;
        PreviewImage.Height = dipH;

        // 缩放改 Image.Width/Height 后让 Window hwnd 跟随内容收缩 / 扩张。
        // 不调这一步的话 hwnd 还停留在最初 ShowAtAnchor 设的大小，Frame 看着是紧贴图片
        // （HorizontalAlignment / VerticalAlignment = Center），但 hwnd 未占用区域既显示透明
        // 又能被点击穿透到桌面，给用户"边框上下有多余的细线、空白处鼠标穿过去"的错觉
        SyncWindowSizeToContent();
    }

    /// <summary>把 hwnd 物理尺寸刷成"内容 desired size × DPI"，保持窗口左上不变。
    /// 拖动 / 跨屏后窗口仍按原 anchorDpi 换算，与"图像 1:1 显示"的语义一致。</summary>
    private void SyncWindowSizeToContent()
    {
        if (!IsLoaded) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;

        double dpiScaleX = _anchorDpiX / 96.0;
        double dpiScaleY = _anchorDpiY / 96.0;
        var windowSizeDip = MeasureContentWindowSizeDip();
        int winWidthPx = (int)Math.Ceiling(windowSizeDip.Width * dpiScaleX);
        int winHeightPx = (int)Math.Ceiling(windowSizeDip.Height * dpiScaleY);
        WindowStyleHelper.ShowNoActivate(hwnd, rect.Left, rect.Top, Math.Max(1, winWidthPx), Math.Max(1, winHeightPx));
    }

    /// <summary>按内容重新测量钉图窗口尺寸，避免透明 hwnd 沿用旧的大尺寸。</summary>
    private Size MeasureContentWindowSizeDip()
    {
        RootChrome.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = RootChrome.DesiredSize;
        if (desired.Width > 0 && desired.Height > 0) return desired;

        var border = Frame.BorderThickness;
        var margin = RootChrome.Margin;
        return new Size(
            PreviewImage.Width + border.Left + border.Right + margin.Left + margin.Right,
            PreviewImage.Height + border.Top + border.Bottom + margin.Top + margin.Bottom);
    }

    private void ApplyOpacity()
    {
        // 鼠标悬停时强制 100%，让用户能看清细节；离开后恢复用户档位
        Opacity = _hovering ? 1.0 : _userOpacity;
    }

    private void OnOpacityClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        if (mi.Tag is not string tag || !double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return;
        _userOpacity = Math.Max(0.1, Math.Min(1.0, v));
        ApplyOpacity();
        UpdateMenuChecks();
    }

    private void OnTopmostClicked(object sender, RoutedEventArgs e)
    {
        Topmost = MenuTopmost.IsChecked;
    }

    private void OnScaleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        if (mi.Tag is not string tag || !double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return;
        ApplyScale(v);
        UpdateMenuChecks();
    }

    private void OnMenuCopyClicked(object sender, RoutedEventArgs e) => CommandCopy();
    private void OnMenuSaveClicked(object sender, RoutedEventArgs e) => CommandSave();
    private void OnMenuOcrClicked(object sender, RoutedEventArgs e) => CommandOcr();
    private void OnMenuCloseClicked(object sender, RoutedEventArgs e) => Close();

    public void CommandCopy()
    {
        try { _clipboard.SetImagePngBytes(_pngBytes); }
        catch (Exception ex) { _log.Warn("pinned screenshot copy failed", ("err", ex.Message)); }
    }

    public void CommandSave()
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存钉图",
            Filter = "PNG 图片 (*.png)|*.png",
            FileName = $"ClipAura-Pinned-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            AddExtension = true,
            DefaultExt = ".png",
        };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllBytes(dialog.FileName, _pngBytes); }
        catch (Exception ex) { _log.Warn("pinned screenshot save failed", ("err", ex.Message)); }
    }

    public void CommandOcr()
    {
        if (_onOcrRequested is null) return;
        var rect = GetCurrentImagePhysicalRect();
        try { _onOcrRequested(this, rect); }
        catch (Exception ex) { _log.Warn("pinned screenshot ocr failed", ("err", ex.Message)); }
    }

    /// <summary>读取 PreviewImage 在屏幕上的物理像素 rect。
    /// 与 ScreenshotPreviewWindow.GetCurrentImagePhysicalRect 同算法，
    /// 不同点：缩放后 PreviewImage 的 ActualWidth/Height ≠ anchorPhysical 尺寸，
    /// 这里直接读 ActualWidth/Height 推回物理像素，让 OCR 结果窗在缩放后位置弹出</summary>
    private WinRectangle GetCurrentImagePhysicalRect()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return WinRectangle.Empty;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return WinRectangle.Empty;

        double dpiScaleX = _anchorDpiX / 96.0;
        double dpiScaleY = _anchorDpiY / 96.0;
        Point imageOffsetDip;
        try { imageOffsetDip = PreviewImage.TranslatePoint(new Point(0, 0), this); }
        catch { imageOffsetDip = new Point(0, 0); }

        int imageOffsetPxX = (int)Math.Round(imageOffsetDip.X * dpiScaleX);
        int imageOffsetPxY = (int)Math.Round(imageOffsetDip.Y * dpiScaleY);
        int widthPx = (int)Math.Round(PreviewImage.ActualWidth * dpiScaleX);
        int heightPx = (int)Math.Round(PreviewImage.ActualHeight * dpiScaleY);

        return new WinRectangle(rect.Left + imageOffsetPxX, rect.Top + imageOffsetPxY, Math.Max(1, widthPx), Math.Max(1, heightPx));
    }

    /// <summary>把"当前 _scale / _userOpacity"对齐到右键菜单的勾选状态。
    /// 不在 100% 档位上的缩放（如 Ctrl+滚轮拉到 110%）所有四档全部不勾，
    /// 与 Snipaste 行为一致 —— 用户改完会自动勾回最近档位</summary>
    private void UpdateMenuChecks()
    {
        MenuOpacity100.IsChecked = NearlyEquals(_userOpacity, 1.0);
        MenuOpacity80.IsChecked = NearlyEquals(_userOpacity, 0.8);
        MenuOpacity60.IsChecked = NearlyEquals(_userOpacity, 0.6);
        MenuOpacity40.IsChecked = NearlyEquals(_userOpacity, 0.4);
        MenuScale50.IsChecked = NearlyEquals(_scale, 0.5);
        MenuScale100.IsChecked = NearlyEquals(_scale, 1.0);
        MenuScale200.IsChecked = NearlyEquals(_scale, 2.0);
        MenuScale400.IsChecked = NearlyEquals(_scale, 4.0);
    }

    private static bool NearlyEquals(double a, double b) => Math.Abs(a - b) < 0.01;
}
