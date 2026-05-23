using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using PopClip.App.Services;
using PopClip.App.UI.Annotation;
using PopClip.Core.Logging;
using PopClip.Hooks.Interop;
using PopClip.Hooks.Window;
using WinRectangle = System.Drawing.Rectangle;
using WpfRectangle = System.Windows.Shapes.Rectangle;

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

    /// <summary>切到 OCR 识别的回调。参数为当前截图图像在屏幕上的物理像素 rect
    /// （= 用户在预览窗内挪动 / 跨屏拖动后图像区域的真实位置），让 OCR 结果窗能在同一个屏幕位置弹出，
    /// 视觉上像是"原地把截图换成了带高亮的 OCR 结果"，而非闪回最初的框选位置</summary>
    private readonly Action<WinRectangle> _onOcrRequested;

    /// <summary>"开始新的截图"按钮的回调：让 Coordinator 关闭当前预览 + 重新进入选区状态机。
    /// 与外部快捷键 / 托盘截图不同，这条路径要主动 Hide 预览 + 工具栏让屏幕保持干净。
    /// null 表示宿主未注入该能力（向后兼容场景），UI 上按钮会置灰</summary>
    private readonly Action? _onReshootRequested;

    /// <summary>"钉到桌面"按钮的回调：参数为当前图像在屏幕上的物理像素 rect。
    /// Coordinator 据此创建 PinnedScreenshotWindow 并定位。null 时按钮置灰</summary>
    private readonly Action<WinRectangle>? _onPinRequested;

    private readonly string _dimensionText;

    private ScreenshotPreviewToolbarWindow? _toolbarWindow;

    /// <summary>标注集合（v1：矩形 / 椭圆 / 直线 / 箭头 / 涂鸦 / 文本 / 马赛克 / 模糊 / 实心遮挡）</summary>
    private readonly AnnotationStore _annotationStore = new();

    /// <summary>当前工具的颜色 / 粗细 / 字号 / 实心模式等开关</summary>
    private readonly AnnotationToolOptions _annotationOptions = new();

    /// <summary>标注画布的输入 / 渲染控制器，把鼠标事件转换为 Annotation</summary>
    private AnnotationCanvasController? _annotationController;

    /// <summary>是否处于"标注编辑模式"。
    /// 进入：工具栏"编辑"按钮；退出：再次按按钮 / ESC（标注模式下 ESC 不关窗）</summary>
    private bool _annotationMode;

    /// <summary>当前未处理的隐私扫描命中点（图像像素坐标系）。
    /// 为 null = 未扫描或无命中；非空时 PrivacyHintCanvas 上画红色虚线框，
    /// 工具栏的"一键打码"按钮亮起。打码后调用方设回 null 同步视觉</summary>
    private IReadOnlyList<PrivacyHit>? _privacyHits;

    /// <summary>"一键打码"使用的马赛克像素块大小，由 Coordinator 在 ShowPrivacyHints 时同步</summary>
    private int _privacyMosaicBlockSize = 12;

    public ScreenshotPreviewWindow(
        ILog log,
        byte[] pngBytes,
        WinRectangle anchorPhysical,
        uint anchorDpiX,
        uint anchorDpiY,
        ClipboardWriter clipboard,
        Action<WinRectangle> onOcrRequested,
        Action? onReshootRequested = null,
        Action<WinRectangle>? onPinRequested = null)
    {
        _log = log;
        _pngBytes = pngBytes;
        _anchorPhysical = anchorPhysical;
        _anchorDpiX = anchorDpiX == 0 ? 96 : anchorDpiX;
        _anchorDpiY = anchorDpiY == 0 ? 96 : anchorDpiY;
        _clipboard = clipboard;
        _onOcrRequested = onOcrRequested;
        _onReshootRequested = onReshootRequested;
        _onPinRequested = onPinRequested;
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

        // 标注画布控制器：在 Loaded 之前可以创建，但 Repaint 依赖 PreviewImage.ActualWidth；
        // 进入标注模式之前不会触发任何重绘，所以这里构造没问题
        _annotationController = new AnnotationCanvasController(AnnotationCanvas, PreviewImage, _annotationStore, _annotationOptions);
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
        // 宿主未注入"重新截图"回调时按钮置灰，避免按了无反应
        _toolbarWindow.SetReshootEnabled(_onReshootRequested is not null);
        _toolbarWindow.SetPinEnabled(_onPinRequested is not null);
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
        // 不用 Window.DragMove() —— 它走 WM_SYSCOMMAND + SC_MOVE，受 Aero Snap 干预，
        // 窗口顶部拖到屏幕上沿时 OS 会把它"贴边 / 弹回"工作区，无法把截图上半部分推到屏幕外。
        // WindowDragHelper.BeginDrag 直接 SetWindowPos，让位移 1:1 跟随鼠标
        WindowDragHelper.BeginDrag(this);
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
                if (_annotationMode)
                {
                    // 标注模式下 ESC 仅退出标注，不关窗 —— 与 Snipaste 习惯一致
                    SetAnnotationMode(false);
                }
                else
                {
                    CommandClose();
                }
                break;
            case Key.C when ctrl:
                e.Handled = true;
                CommandCopy(sink);
                break;
            case Key.S when ctrl:
                e.Handled = true;
                CommandSave(sink);
                break;
            case Key.O when plain && !_annotationMode:
                e.Handled = true;
                CommandOcr();
                break;
            case Key.Z when ctrl && _annotationMode:
                e.Handled = true;
                _annotationStore.Undo();
                break;
            case Key.Y when ctrl && _annotationMode:
                e.Handled = true;
                _annotationStore.Redo();
                break;
        }
    }

    /// <summary>工具栏按钮触发：写入剪贴板。toastSink 由工具条传入自己的 ShowLocalToast，
    /// 让反馈贴近按钮位置而不是飘到主窗中央；null 时静默成功，仅在异常时记日志。
    /// 有标注时合成 PreviewImage + AnnotationCanvas 再编码为 PNG；无标注时走原 PNG 字节，零开销</summary>
    public void CommandCopy(Action<string>? toastSink = null)
    {
        try
        {
            var bytes = ResolveOutputPngBytes();
            _clipboard.SetImagePngBytes(bytes);
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
            var bytes = ResolveOutputPngBytes();
            File.WriteAllBytes(dialog.FileName, bytes);
            toastSink?.Invoke("截图已保存");
        }
        catch (Exception ex)
        {
            _log.Warn("screenshot save failed", ("err", ex.Message));
            toastSink?.Invoke("保存失败：" + ex.Message);
        }
    }

    /// <summary>把 PreviewImage + AnnotationCanvas 合成为带标注的最终 PNG。
    /// 无标注时直接复用原 _pngBytes，避免无谓的编解码消耗</summary>
    private byte[] ResolveOutputPngBytes()
    {
        if (_annotationStore.Items.Count == 0 || _annotationController is null) return _pngBytes;
        if (PreviewImage.Source is not BitmapSource source) return _pngBytes;

        var bmp = _annotationController.RenderComposite(source);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>工具栏按钮触发：切换到 OCR 识别。
    ///
    /// 与旧版差异：
    /// 1) 不主动 Close 本窗，由 Coordinator 在 OCR 结果窗 Show + Loaded 完成后再关闭，
    ///    让"截图图像 → OCR 高亮结果图"在视觉上几乎无缝衔接（两个窗口在同一位置短暂共存）；
    /// 2) 把当前图像在屏上的物理像素 rect 传给回调，让 OCR 结果窗在当前预览位置弹出，
    ///    而非用最初的框选位置 —— 用户拖动 / 跨屏后保留位置惯性</summary>
    public void CommandOcr()
    {
        var anchor = GetCurrentImagePhysicalRect();
        try { _onOcrRequested(anchor); }
        catch (Exception ex) { _log.Warn("screenshot ocr request failed", ("err", ex.Message)); }
    }

    /// <summary>工具栏按钮触发：开始一次新的截图。
    /// 立刻 Hide 自己（包括子工具栏）让屏幕保持干净，再调用 Coordinator 重新进入选区状态机。
    /// 注意：Hide 而非 Close —— 若用户在新选区里 ESC 取消，Coordinator 仍可决定要不要把本窗恢复回来；
    /// 但当前实现是直接关闭并触发新流程，所以 Hide 后会跟随真正 Close 一并清理</summary>
    public void CommandReshoot()
    {
        if (_onReshootRequested is null) return;
        try
        {
            // 隐藏本窗 + 工具栏：让 OcrSelectionWindow 的全屏蒙层下方能看到桌面真实内容，
            // 避免旧预览叠在新选区上影响用户判断
            if (_toolbarWindow is not null)
            {
                try { _toolbarWindow.Hide(); } catch { }
            }
            Hide();
            // 真正的 Close 留给 Coordinator 处理 —— Coordinator 会在拉起新选区前关闭旧窗，
            // 与"快捷键 / 托盘"路径保持一致的窗口生命周期管理
            _onReshootRequested();
        }
        catch (Exception ex)
        {
            _log.Warn("screenshot reshoot request failed", ("err", ex.Message));
        }
    }

    public void CommandClose() => Close();

    /// <summary>工具栏按钮触发：把当前预览的截图钉到桌面。
    /// 钉成功后关闭本预览窗，让"截图预览 → 钉图"切换在视觉上像是"原地变成钉图"。
    /// Coordinator 通过 onPinRequested 回调拿到当前图像物理 rect，创建 PinnedScreenshotWindow 并定位</summary>
    public void CommandPin()
    {
        if (_onPinRequested is null) return;
        var anchor = GetCurrentImagePhysicalRect();
        try { _onPinRequested(anchor); }
        catch (Exception ex) { _log.Warn("screenshot pin request failed", ("err", ex.Message)); return; }
        // 钉图已在原位创建，关掉本预览避免两个一模一样的窗口叠在一起
        Close();
    }

    // ============= 标注模式接口（供 ScreenshotPreviewToolbarWindow 调用） =============

    /// <summary>把工具栏拉起 / 收起标注层。
    /// 进入标注模式时：Frame.Cursor 改为默认（避免 SizeAll 误导拖动）；退出时恢复</summary>
    public void SetAnnotationMode(bool enabled)
    {
        if (_annotationMode == enabled) return;
        _annotationMode = enabled;
        _annotationController?.SetActive(enabled);
        Frame.Cursor = enabled ? Cursors.Cross : Cursors.SizeAll;
        _toolbarWindow?.SetAnnotationMode(enabled);
        if (enabled)
        {
            // 进入标注后获取焦点，让 Ctrl+Z / Ctrl+Y / ESC 直接命中本窗 KeyDown
            Focus();
            Keyboard.Focus(this);
            Activate();
        }
    }

    public AnnotationToolOptions AnnotationOptions => _annotationOptions;
    public AnnotationStore AnnotationStore => _annotationStore;
    public bool AnnotationMode => _annotationMode;

    // ============= 隐私扫描提示接口（M4） =============

    /// <summary>由 OcrCaptureCoordinator 在后台 OCR + PrivacyScanner 完成后调用：
    /// 用红色虚线框在 PrivacyHintCanvas 上提示命中区，并把工具栏的"一键打码"按钮变为可用状态。
    /// 提示坐标系：hits 给的是图像像素坐标（左上原点），需要按 PreviewImage DIP 比例换算。
    /// 没有命中时不调用本方法即可（PrivacyBanner 默认 Collapsed）</summary>
    public void ShowPrivacyHints(
        IReadOnlyList<PrivacyHit> hits,
        int sourceImagePixelWidth,
        int sourceImagePixelHeight,
        int mosaicBlockSize)
    {
        if (hits is null || hits.Count == 0)
        {
            ClearPrivacyHints();
            return;
        }
        _privacyHits = hits;
        _privacyMosaicBlockSize = mosaicBlockSize <= 0 ? 12 : mosaicBlockSize;

        PrivacyHintCanvas.Children.Clear();
        // 把图像像素 → DIP：PreviewImage 的 DIP 大小由 BitmapSource 自带 DPI 决定，
        // 等价于 sourceImagePixelWidth × 96 / dpiX。这里用 ActualWidth 直接换算更稳，
        // 避免 BitmapSource DPI 与 anchorDpi 同步脱节
        double dipW = PreviewImage.ActualWidth > 0 ? PreviewImage.ActualWidth : sourceImagePixelWidth;
        double dipH = PreviewImage.ActualHeight > 0 ? PreviewImage.ActualHeight : sourceImagePixelHeight;
        double scaleX = sourceImagePixelWidth <= 0 ? 1 : dipW / sourceImagePixelWidth;
        double scaleY = sourceImagePixelHeight <= 0 ? 1 : dipH / sourceImagePixelHeight;

        foreach (var h in hits)
        {
            var rect = new WpfRectangle
            {
                Width = Math.Max(1, h.Width * scaleX),
                Height = Math.Max(1, h.Height * scaleY),
                Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xE5, 0x39, 0x35)),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xE5, 0x39, 0x35)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(rect, h.Left * scaleX);
            Canvas.SetTop(rect, h.Top * scaleY);
            PrivacyHintCanvas.Children.Add(rect);
        }

        PrivacyBannerText.Text = $"⚠ 检测到 {hits.Count} 处疑似敏感信息，可一键打码";
        PrivacyBanner.Visibility = Visibility.Visible;
        _toolbarWindow?.SetPrivacyMosaicEnabled(true);
    }

    /// <summary>清掉隐私提示画面 + 让工具栏一键打码按钮回到 disabled。
    /// 在用户已"一键打码"或主动忽略后调用</summary>
    public void ClearPrivacyHints()
    {
        _privacyHits = null;
        PrivacyHintCanvas.Children.Clear();
        PrivacyBanner.Visibility = Visibility.Collapsed;
        _toolbarWindow?.SetPrivacyMosaicEnabled(false);
    }

    /// <summary>把当前所有隐私命中区作为 Mosaic 标注批量加入 AnnotationStore。
    /// 加入后清空隐私提示状态：用户能用 Ctrl+Z 撤销（每个命中区独立一条 Annotation，撤销逐条回退）。
    /// 工具栏调用此方法</summary>
    public void CommandApplyPrivacyMosaic(int blockSize)
    {
        if (_privacyHits is null || _privacyHits.Count == 0) return;
        if (PreviewImage.Source is not BitmapSource src) return;

        double dipW = PreviewImage.ActualWidth > 0 ? PreviewImage.ActualWidth : src.PixelWidth;
        double dipH = PreviewImage.ActualHeight > 0 ? PreviewImage.ActualHeight : src.PixelHeight;
        double scaleX = src.PixelWidth <= 0 ? 1 : dipW / src.PixelWidth;
        double scaleY = src.PixelHeight <= 0 ? 1 : dipH / src.PixelHeight;

        // 为了能 Ctrl+Z 单独撤销每条命中，逐条 Add 到 store。Mosaic 渲染由 AnnotationRenderer
        // 负责（裁剪源图像 → 缩小再用 NearestNeighbor 拉回 → ImageBrush 填充矩形）
        var bs = blockSize <= 0 ? 12 : blockSize;
        foreach (var h in _privacyHits)
        {
            var bounds = new Rect(
                h.Left * scaleX,
                h.Top * scaleY,
                Math.Max(1, h.Width * scaleX),
                Math.Max(1, h.Height * scaleY));
            _annotationStore.Add(global::PopClip.App.UI.Annotation.Annotation.CreateMosaic(bounds, bs));
        }

        ClearPrivacyHints();
    }

    public bool HasPrivacyHits => _privacyHits is not null && _privacyHits.Count > 0;

    /// <summary>由工具栏 OnPrivacyMosaicClicked 调用：用 host 缓存的 blockSize 应用到所有命中区</summary>
    public void RequestApplyPrivacyMosaic() => CommandApplyPrivacyMosaic(_privacyMosaicBlockSize);

    /// <summary>读取当前截图图像在屏幕上的物理像素位置。
    /// 用 GetWindowRect 取窗口物理矩形，再加上 PreviewImage 相对 Window 的 DIP 偏移（含 4 DIP Margin + 2 DIP Frame BorderThickness），
    /// 换算成物理像素后得到图像左上像素坐标，宽高直接复用最初的 anchor 尺寸（图像内容未变形）。
    ///
    /// 跨屏 / 异构 DPI 注意：GetWindowRect 始终返回物理像素，DIP 偏移用窗口创建时的 anchorDpi 换算 —
    /// 若用户在预览过程中把窗口拖到另一块 DPI 不同的屏，imageOffset 会有 sub-pixel 误差，
    /// 但 PreviewImage 是 Stretch=None 绘制，DPI 影响也只是边缘几像素，不影响 OCR 结果的视觉对齐</summary>
    private WinRectangle GetCurrentImagePhysicalRect()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return _anchorPhysical;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return _anchorPhysical;

        double dpiScaleX = _anchorDpiX / 96.0;
        double dpiScaleY = _anchorDpiY / 96.0;
        Point imageOffsetDip;
        try { imageOffsetDip = PreviewImage.TranslatePoint(new Point(0, 0), this); }
        catch { imageOffsetDip = new Point(0, 0); }
        int imageOffsetPxX = (int)Math.Round(imageOffsetDip.X * dpiScaleX);
        int imageOffsetPxY = (int)Math.Round(imageOffsetDip.Y * dpiScaleY);

        int x = rect.Left + imageOffsetPxX;
        int y = rect.Top + imageOffsetPxY;
        return new WinRectangle(x, y, _anchorPhysical.Width, _anchorPhysical.Height);
    }
}
