using System.IO;
using System.Windows;
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

/// <summary>截图完成后的预览窗：在原截图位置上叠加一张 1:1 的图像 + 独立工具条子窗口。
///
/// 关键设计：
/// - BitmapSource 用 anchor monitor 的 DPI 而非默认 96 创建，使其 DIP 尺寸 × 当前屏 DPI 缩放
///   严格等于截图物理像素，Stretch="None" 时 WPF 不做任何缩放/插值 → 文字与原截图完全一致。
/// - 工具条独立成 ScreenshotPreviewToolbarWindow 子窗口（透明 Topmost，可拖到屏幕任意位置）。
///   这样窄高截图（如侧栏 / 长条 OCR 区域）的主窗宽度严格 = 截图宽度，
///   不会被工具条宽度反推撑宽变形。
/// - 主图 Border + 外层阴影 margin 全部由 WPF 按内容 desired size 测量；
///   Win32 SetWindowPos 把窗口按 anchor monitor 物理像素绝对定位 + 拉伸到对应物理尺寸，
///   规避 PerMonitor V2 跨屏时 Window.Left/Top 用错 DPI 解读的老问题。
/// - 工具条按"主图下方 → 上方 → 内底部"三档自动选位，与 OcrResultWindow 同策略；
///   用户拖动后位置自由，独立 Window 可跨屏任意拖。</summary>
internal partial class ScreenshotPreviewWindow : Window
{
    private readonly ILog _log;
    private readonly byte[] _pngBytes;
    private readonly WinRectangle _anchorPhysical;
    private readonly uint _anchorDpiX;
    private readonly uint _anchorDpiY;
    private readonly ClipboardWriter _clipboard;
    private readonly Action _onOcrRequested;
    private readonly string _dimensionText;

    private ScreenshotPreviewToolbarWindow? _toolbarWindow;

    public ScreenshotPreviewWindow(
        ILog log,
        byte[] pngBytes,
        WinRectangle anchorPhysical,
        uint anchorDpiX,
        uint anchorDpiY,
        ClipboardWriter clipboard,
        Action onOcrRequested)
    {
        _log = log;
        _pngBytes = pngBytes;
        _anchorPhysical = anchorPhysical;
        _anchorDpiX = anchorDpiX == 0 ? 96 : anchorDpiX;
        _anchorDpiY = anchorDpiY == 0 ? 96 : anchorDpiY;
        _clipboard = clipboard;
        _onOcrRequested = onOcrRequested;
        _dimensionText = $"{_anchorPhysical.Width} × {_anchorPhysical.Height}";

        InitializeComponent();

        // 先把窗口挪到屏外，等 Loaded 阶段用 SetWindowPos 精确定位 + 调整尺寸。
        // 不在构造期设置 Width/Height —— 让 WPF 按内容 desired size 自然布局，
        // 后续直接读 ActualWidth/Height 换算成 anchor monitor 物理像素。
        Left = -32000;
        Top = -32000;
        SizeToContent = SizeToContent.WidthAndHeight;

        PreviewImage.Source = LoadBitmap(_pngBytes, _anchorDpiX, _anchorDpiY);

        Loaded += OnLoaded;
        Closed += OnClosed;
        KeyDown += OnKeyDown;
        Frame.MouseLeftButtonDown += OnFrameMouseLeftButtonDown;
    }

    /// <summary>把 PNG 字节解码为带 anchor DPI 标记的 BitmapSource。
    ///
    /// 为什么不直接用 BitmapImage：BitmapImage 默认 DPI 为 96（或读 PNG metadata 的 96），
    /// 让 Image 控件 DIP 大小 = PixelWidth；当目标显示 DIP 大小不等于 PixelWidth 时
    /// WPF 会做缩放（Stretch="Fill" 也会触发），即使 BitmapScalingMode=NearestNeighbor 也可能
    /// 因亚像素对齐产生模糊。把 BitmapSource 的 DPI 设为 anchor monitor 的 effective DPI，
    /// DIP 大小 = PixelWidth × 96 / anchorDpi = anchorPhysical / dpiScale，
    /// 与 Image 控件 Stretch="None" 时的 desired size 完全一致，绘制时 1 物理像素对 1 source 像素。</summary>
    private static BitmapSource LoadBitmap(byte[] pngBytes, uint dpiX, uint dpiY)
    {
        using var ms = new MemoryStream(pngBytes);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];

        BitmapSource source = frame;
        if (frame.Format != System.Windows.Media.PixelFormats.Bgra32 &&
            frame.Format != System.Windows.Media.PixelFormats.Bgr32 &&
            frame.Format != System.Windows.Media.PixelFormats.Pbgra32)
        {
            source = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        }

        int stride = source.PixelWidth * (source.Format.BitsPerPixel / 8);
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);

        var bmp = BitmapSource.Create(
            source.PixelWidth,
            source.PixelHeight,
            dpiX,
            dpiY,
            source.Format,
            null,
            pixels,
            stride);
        bmp.Freeze();
        return bmp;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 第一次测量后才能拿到准确的 ActualWidth/Height，再用 Win32 物理像素定位窗口
        UpdateLayout();
        // 关闭 SizeToContent，避免后续 SetWindowPos 改 hwnd 尺寸时 WPF 又把 size 调回 desired size
        SizeToContent = SizeToContent.Manual;
        PlaceWindowAtAnchorPhysical();

        // 子工具条窗口在主窗 Loaded 之后再 Show：此时主窗几何已稳定，可以算出工具条位置。
        // Show 前先把工具条挪到屏外（-32000），避免 Show 期间在 OS 默认位置短暂闪现
        _toolbarWindow = new ScreenshotPreviewToolbarWindow(this, _dimensionText);
        _toolbarWindow.Left = -32000;
        _toolbarWindow.Top = -32000;
        // SizeToContent=WidthAndHeight，必须先 Show 一次让它测出 ActualWidth/Height
        _toolbarWindow.Show();
        PositionToolbarInitially();

        Activate();
        Focus();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // 主窗关闭时一并关闭工具条；Owner=this 在 .NET WPF 也会自动关，
        // 这里显式调以防 Owner 链路异常
        if (_toolbarWindow is not null)
        {
            try { _toolbarWindow.Close(); } catch { }
            _toolbarWindow = null;
        }
    }

    /// <summary>把窗口物理像素绝对定位到屏幕，让主图区域正好覆盖 anchor 截图区域。
    /// 因为窗口外有 4 DIP 阴影 margin、主图边框 2 DIP，
    /// 必须用 TranslatePoint 求出 PreviewImage 相对窗口左上的 DIP 偏移，
    /// 再按 anchor monitor DPI 转成物理像素偏移，最后 SetWindowPos 把窗口推到正确位置。</summary>
    private void PlaceWindowAtAnchorPhysical()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;

        double dpiScaleX = _anchorDpiX / 96.0;
        double dpiScaleY = _anchorDpiY / 96.0;

        // PreviewImage 相对 Window 客户区左上的 DIP 偏移；UpdateLayout 之后才是准确值
        var imageOffsetDip = PreviewImage.TranslatePoint(new Point(0, 0), this);
        int imageOffsetPxX = (int)Math.Round(imageOffsetDip.X * dpiScaleX);
        int imageOffsetPxY = (int)Math.Round(imageOffsetDip.Y * dpiScaleY);

        // Window 总物理像素 = 客户区 DIP × dpiScale。客户区 = ActualWidth × dpiScale，
        // 因为 AllowsTransparency=True + WindowStyle=None 时没有非客户区
        int winWidthPx = (int)Math.Ceiling(ActualWidth * dpiScaleX);
        int winHeightPx = (int)Math.Ceiling(ActualHeight * dpiScaleY);

        int winLeftPx = _anchorPhysical.Left - imageOffsetPxX;
        int winTopPx = _anchorPhysical.Top - imageOffsetPxY;

        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            winLeftPx,
            winTopPx,
            Math.Max(1, winWidthPx),
            Math.Max(1, winHeightPx),
            NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>把工具条窗口放到合适位置：
    /// 优先主图下方 8 DIP，下方空间不够则上方 8 DIP，上下都不够则贴主图内底部 12 DIP。
    /// 用 Win32 SetWindowPos 物理像素绝对定位，跨屏 / 异构 DPI 不会跳到主屏。
    /// 用户拖动后位置自由（独立 Window 可任意拖），后续不再受约束</summary>
    private void PositionToolbarInitially()
    {
        if (_toolbarWindow is null) return;
        var tb = _toolbarWindow;

        var tbHwnd = new WindowInteropHelper(tb).Handle;
        if (tbHwnd == IntPtr.Zero) return;

        // 工具条 ActualWidth/Height 是 DIP（PerMonitor V2 下 content 大小恒定，与所在屏 DPI 无关）。
        // 跨到 anchor monitor 后视觉一致 → 物理像素 = DIP × anchor DPI / 96
        var tbWDip = tb.ActualWidth > 0 ? tb.ActualWidth : 220;
        var tbHDip = tb.ActualHeight > 0 ? tb.ActualHeight : 44;
        int tbWPx = (int)Math.Ceiling(tbWDip * _anchorDpiX / 96.0);
        int tbHPx = (int)Math.Ceiling(tbHDip * _anchorDpiY / 96.0);

        // anchor monitor 工作区，用于判断"工具条放在哪里能完整可见"
        var monitor = MonitorQuery.FromRect(
            _anchorPhysical.Left, _anchorPhysical.Top,
            _anchorPhysical.Right, _anchorPhysical.Bottom);

        int gapOutsidePx = (int)Math.Round(8 * _anchorDpiY / 96.0);
        int gapInsidePx = (int)Math.Round(12 * _anchorDpiY / 96.0);

        // 水平居中于 anchor 截图区域；垂直三档：下方外侧 > 上方外侧 > 主图内底部
        int leftPx = _anchorPhysical.Left + (_anchorPhysical.Width - tbWPx) / 2;
        int topBelowPx = _anchorPhysical.Bottom + gapOutsidePx;
        int topAbovePx = _anchorPhysical.Top - gapOutsidePx - tbHPx;
        int topInsidePx = _anchorPhysical.Bottom - tbHPx - gapInsidePx;

        int topPx;
        if (topBelowPx + tbHPx <= monitor.WorkBottom) topPx = topBelowPx;
        else if (topAbovePx >= monitor.WorkTop) topPx = topAbovePx;
        else topPx = topInsidePx;

        // 边界保护：避免工具条横向 / 纵向被推出 anchor monitor 工作区外
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

    private void OnFrameMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool plain = Keyboard.Modifiers == ModifierKeys.None;

        // C# 不允许 `?.MethodName` 形式取方法组；显式构造 Action<string>? 让 toastSink 可为 null
        Action<string>? sink = _toolbarWindow is null ? null : _toolbarWindow.ShowLocalToast;

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CommandClose();
                break;
            case Key.C when ctrl:
                e.Handled = true;
                CommandCopy(sink);
                break;
            case Key.S when ctrl:
                e.Handled = true;
                CommandSave(sink);
                break;
            case Key.O when plain:
                e.Handled = true;
                CommandOcr();
                break;
        }
    }

    /// <summary>工具栏按钮触发：写入剪贴板。toastSink 由工具条传入自己的 ShowLocalToast，
    /// 让反馈贴近按钮位置而不是飘到主窗中央；null 时静默成功，仅在异常时记日志</summary>
    public void CommandCopy(Action<string>? toastSink = null)
    {
        try
        {
            _clipboard.SetImagePngBytes(_pngBytes);
            toastSink?.Invoke("截图已复制");
        }
        catch (Exception ex)
        {
            _log.Warn("screenshot copy failed", ("err", ex.Message));
            toastSink?.Invoke("复制失败：" + ex.Message);
        }
    }

    /// <summary>工具栏按钮触发：弹保存对话框。
    /// 注意：SaveFileDialog 是模态对话框，Owner 给 this 即可保证置顶位置不偏</summary>
    public void CommandSave(Action<string>? toastSink = null)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 图片 (*.png)|*.png",
            FileName = $"ClipAura-Screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            AddExtension = true,
            DefaultExt = ".png",
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, _pngBytes);
            toastSink?.Invoke("截图已保存");
        }
        catch (Exception ex)
        {
            _log.Warn("screenshot save failed", ("err", ex.Message));
            toastSink?.Invoke("保存失败：" + ex.Message);
        }
    }

    /// <summary>工具栏按钮触发：关闭预览并触发 OCR。
    /// 顺序：先关本窗 → onOcrRequested 在 Coordinator 里打开 OCR 结果窗，
    /// 避免本窗 topmost 在 OCR 窗之上挡视线</summary>
    public void CommandOcr()
    {
        Close();
        try { _onOcrRequested(); }
        catch (Exception ex) { _log.Warn("screenshot ocr request failed", ("err", ex.Message)); }
    }

    public void CommandClose() => Close();
}
