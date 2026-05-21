using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PopClip.App.Services;
using PopClip.Core.Logging;
using PopClip.Hooks.Interop;
using PopClip.Hooks.Window;
using WinRectangle = System.Drawing.Rectangle;

namespace PopClip.App.UI;

/// <summary>截图完成后的预览窗：在原截图位置上叠加一张 1:1 的图像 + 工具栏。
///
/// 关键设计：
/// - BitmapSource 用 anchor monitor 的 DPI 而非默认 96 创建，使其 DIP 尺寸 × 当前屏 DPI 缩放
///   严格等于截图物理像素，Stretch="None" 时 WPF 不做任何缩放/插值 → 文字与原截图完全一致。
/// - 主图 Border + 工具栏 + 外层阴影 margin 全部由 WPF SizeToContent 测出 DIP；
///   Win32 SetWindowPos 把窗口按 anchor monitor 物理像素绝对定位 + 拉伸到对应物理尺寸，
///   规避 PerMonitor V2 跨屏时 Window.Left/Top 用错 DPI 解读的老问题。
/// - 工具栏默认位于图片下方；若下方空间不足则翻到上方；若上下两侧都塞不下（如满屏截图），
///   退化为贴在图片内部底部显示，保证始终能看见。</summary>
internal partial class ScreenshotPreviewWindow : Window
{
    private enum ToolbarPlacement
    {
        BottomOutside,
        TopOutside,
        BottomOverlay,
    }

    private readonly ILog _log;
    private readonly byte[] _pngBytes;
    private readonly WinRectangle _anchorPhysical;
    private readonly uint _anchorDpiX;
    private readonly uint _anchorDpiY;
    private readonly ClipboardWriter _clipboard;
    private readonly Action _onOcrRequested;
    private readonly DispatcherTimer _toastTimer;
    private ToolbarPlacement _toolbarPlacement = ToolbarPlacement.BottomOutside;
    private TranslateTransform? _toolbarOverlayTransform;
    private Point? _toolbarDragStart;
    private Point _toolbarDragOrigin;
    private double _toolbarOverlayOffsetX;
    private double _toolbarOverlayOffsetY;

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

        InitializeComponent();

        // 先把窗口挪到屏外，等 Loaded 阶段用 SetWindowPos 精确定位 + 调整尺寸。
        // 不在构造期设置 Width/Height —— 让 WPF 按内容 desired size 自然布局，
        // 后续直接读 ActualWidth/Height 换算成 anchor monitor 物理像素。
        Left = -32000;
        Top = -32000;
        SizeToContent = SizeToContent.WidthAndHeight;

        PreviewImage.Source = LoadBitmap(_pngBytes, _anchorDpiX, _anchorDpiY);
        DimensionText.Text = $"{_anchorPhysical.Width} × {_anchorPhysical.Height}";

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBorder.Visibility = Visibility.Collapsed;
        };

        Loaded += OnLoaded;
        Closed += (_, _) => _toastTimer.Stop();
        KeyDown += OnKeyDown;
        Frame.MouseLeftButtonDown += OnFrameMouseLeftButtonDown;
        ToolbarPanel.MouseLeftButtonDown += OnToolbarMouseLeftButtonDown;
        ToolbarPanel.MouseMove += OnToolbarMouseMove;
        ToolbarPanel.MouseLeftButtonUp += OnToolbarMouseLeftButtonUp;
        ToolbarPanel.LostMouseCapture += OnToolbarLostMouseCapture;
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
        // 第一次测量后先决定工具栏在主图上方还是下方，再用最终布局算窗口物理像素位置。
        // 必须 UpdateLayout 后才能拿到准确的 ActualWidth/Height
        UpdateLayout();
        ChooseToolbarPlacement();
        UpdateLayout();
        // 关闭 SizeToContent，避免后续 SetWindowPos 改 hwnd 尺寸时 WPF 又把 size 调回 desired size，
        // 与 Win32 物理像素摆位反复争用
        SizeToContent = SizeToContent.Manual;
        PlaceWindowAtAnchorPhysical();
        Activate();
        Focus();
    }

    /// <summary>按 anchor 截图在 anchor monitor 工作区中的位置，决定工具栏挂在主图外侧还是图内。
    /// 优先放图片下方；若下方空间不足且上方足够，则翻到上方；若上下都不够，则贴到图片内部底部，
    /// 让满屏截图时也保留可点击入口。</summary>
    private void ChooseToolbarPlacement()
    {
        var monitor = MonitorQuery.FromRect(
            _anchorPhysical.Left, _anchorPhysical.Top,
            _anchorPhysical.Right, _anchorPhysical.Bottom);

        double dpiScaleY = _anchorDpiY / 96.0;
        // 工具栏一侧需要占据的物理像素：主图边框（2 DIP）+ 距主图的间距（8 DIP）+ 自身高度 + 阴影外边距（14 DIP）
        double toolbarDipH = ToolbarPanel.ActualHeight > 0 ? ToolbarPanel.ActualHeight : 38;
        int toolbarTotalPx = (int)Math.Ceiling((2 + 8 + toolbarDipH + 14) * dpiScaleY);

        int spaceBelowPx = monitor.WorkBottom - _anchorPhysical.Bottom;
        int spaceAbovePx = _anchorPhysical.Top - monitor.WorkTop;

        ToolbarPlacement placement = spaceBelowPx >= toolbarTotalPx
            ? ToolbarPlacement.BottomOutside
            : spaceAbovePx >= toolbarTotalPx
                ? ToolbarPlacement.TopOutside
                : ToolbarPlacement.BottomOverlay;
        AttachToolbarTo(placement);
    }

    /// <summary>把 ToolbarPanel reparent 到指定挂点。
    /// XAML 里 ToolbarPanel 初始挂在 ToolbarBottomHost，运行时若需要换位置必须先解绑旧 parent，
    /// 否则 WPF 会抛 "Specified element is already the logical child of another element" 异常。</summary>
    private void AttachToolbarTo(ToolbarPlacement placement)
    {
        _toolbarPlacement = placement;
        ContentControl host = placement switch
        {
            ToolbarPlacement.TopOutside => ToolbarTopHost,
            ToolbarPlacement.BottomOverlay => ToolbarOverlayHost,
            _ => ToolbarBottomHost,
        };

        var current = ToolbarPanel.Parent;
        if (current is ContentControl prevHost && !ReferenceEquals(prevHost, host))
        {
            prevHost.Content = null;
        }
        if (host.Content is null)
        {
            host.Content = ToolbarPanel;
        }
        // 图外布局需要给主图和工具栏之间留 8 DIP；图内布局则收成贴底内边距，避免压到边框阴影
        ToolbarPanel.Margin = placement switch
        {
            ToolbarPlacement.TopOutside => new Thickness(0, 0, 0, 8),
            ToolbarPlacement.BottomOverlay => new Thickness(0, 0, 0, 10),
            _ => new Thickness(0, 8, 0, 0),
        };
        if (placement == ToolbarPlacement.BottomOverlay)
        {
            _toolbarOverlayTransform ??= new TranslateTransform();
            ToolbarPanel.RenderTransform = _toolbarOverlayTransform;
            ToolbarPanel.RenderTransformOrigin = new Point(0, 0);
        }
        else
        {
            _toolbarDragStart = null;
            _toolbarOverlayOffsetX = 0;
            _toolbarOverlayOffsetY = 0;
            if (_toolbarOverlayTransform is not null)
            {
                _toolbarOverlayTransform.X = 0;
                _toolbarOverlayTransform.Y = 0;
            }
            ToolbarPanel.RenderTransform = Transform.Identity;
        }
    }

    /// <summary>把窗口物理像素绝对定位到屏幕，让主图区域正好覆盖 anchor 截图区域。
    /// 因为窗口外有 14 DIP 阴影 margin、主图边框 2 DIP、可能还有上方工具栏，
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

    private void OnFrameMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { }
    }

    /// <summary>图内模式允许拖动工具栏本身，避免满屏截图时遮住关键内容；图外模式仍拖整个窗口。</summary>
    private void OnToolbarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (_toolbarPlacement == ToolbarPlacement.BottomOverlay)
        {
            if (IsInteractiveToolbarChild(e.OriginalSource as DependencyObject)) return;
            _toolbarDragStart = e.GetPosition(Frame);
            _toolbarDragOrigin = new Point(_toolbarOverlayOffsetX, _toolbarOverlayOffsetY);
            ToolbarPanel.CaptureMouse();
            e.Handled = true;
            return;
        }

        OnFrameMouseLeftButtonDown(sender, e);
    }

    private void OnToolbarMouseMove(object sender, MouseEventArgs e)
    {
        if (_toolbarPlacement != ToolbarPlacement.BottomOverlay ||
            _toolbarDragStart is null ||
            _toolbarOverlayTransform is null ||
            !ToolbarPanel.IsMouseCaptured)
        {
            return;
        }

        var current = e.GetPosition(Frame);
        var start = _toolbarDragStart.Value;
        double nextX = _toolbarDragOrigin.X + (current.X - start.X);
        double nextY = _toolbarDragOrigin.Y + (current.Y - start.Y);

        var bounds = GetToolbarOverlayBounds();
        _toolbarOverlayOffsetX = Math.Clamp(nextX, bounds.minX, bounds.maxX);
        _toolbarOverlayOffsetY = Math.Clamp(nextY, bounds.minY, bounds.maxY);
        _toolbarOverlayTransform.X = _toolbarOverlayOffsetX;
        _toolbarOverlayTransform.Y = _toolbarOverlayOffsetY;
        e.Handled = true;
    }

    private void OnToolbarMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_toolbarPlacement != ToolbarPlacement.BottomOverlay || e.ChangedButton != MouseButton.Left) return;
        ReleaseToolbarOverlayDrag();
    }

    private void OnToolbarLostMouseCapture(object sender, MouseEventArgs e)
        => ReleaseToolbarOverlayDrag();

    private void ReleaseToolbarOverlayDrag()
    {
        _toolbarDragStart = null;
        if (ToolbarPanel.IsMouseCaptured)
        {
            ToolbarPanel.ReleaseMouseCapture();
        }
    }

    /// <summary>把拖动范围限制在截图内部，避免工具栏被拖出画面后再次丢失。</summary>
    private (double minX, double maxX, double minY, double maxY) GetToolbarOverlayBounds()
    {
        double hostW = Math.Max(0, Frame.ActualWidth - 4);
        double hostH = Math.Max(0, Frame.ActualHeight - 4);
        double panelW = ToolbarPanel.ActualWidth;
        double panelH = ToolbarPanel.ActualHeight;

        double minX = -(hostW - panelW) / 2;
        double maxX = (hostW - panelW) / 2;
        double bottomInset = ToolbarPanel.Margin.Bottom;
        double minY = -(hostH - panelH - bottomInset);
        double maxY = -bottomInset;

        if (minX > maxX) (minX, maxX) = (0, 0);
        if (minY > maxY) minY = maxY;
        return (minX, maxX, minY, maxY);
    }

    private static bool IsInteractiveToolbarChild(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button) return true;
            if (source is Border border && Equals(border.Name, "ToolbarPanel")) break;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool plain = Keyboard.Modifiers == ModifierKeys.None;

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Close();
                break;
            case Key.C when ctrl:
                e.Handled = true;
                OnCopyClicked(this, new RoutedEventArgs());
                break;
            case Key.S when ctrl:
                e.Handled = true;
                OnSaveClicked(this, new RoutedEventArgs());
                break;
            case Key.O when plain:
                e.Handled = true;
                OnOcrClicked(this, new RoutedEventArgs());
                break;
        }
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            _clipboard.SetImagePngBytes(_pngBytes);
            ShowToast("截图已复制");
        }
        catch (Exception ex)
        {
            _log.Warn("screenshot copy failed", ("err", ex.Message));
            ShowToast("复制失败：" + ex.Message);
        }
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
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
            ShowToast("截图已保存");
        }
        catch (Exception ex)
        {
            _log.Warn("screenshot save failed", ("err", ex.Message));
            ShowToast("保存失败：" + ex.Message);
        }
    }

    private void OnOcrClicked(object sender, RoutedEventArgs e)
    {
        Close();
        try { _onOcrRequested(); }
        catch (Exception ex) { _log.Warn("screenshot ocr request failed", ("err", ex.Message)); }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        ToastBorder.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }
}
