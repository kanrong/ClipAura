using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PopClip.App.Services;
using PopClip.App.UI.Annotation;
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
    /// <summary>PNG 字节的延迟取值器：仅在用户点"复制 / 保存"或"无标注的钉图"时才真正编码。
    /// 之前的实现是在 ctor 入参里直接接受 byte[] pngBytes，意味着 OcrCaptureCoordinator 在弹窗前
    /// 就必须强制走一次 PNG 编码 —— 全屏 1080p 时这一步阻塞 ~30ms，4K 时 ~100ms+。
    /// 改成 Lazy provider 后，BGRA32 原始像素直接构造 BitmapSource 显示，PNG 编码移到"真的需要字节流"的时机</summary>
    private readonly Func<byte[]> _pngBytesProvider;
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

    /// <summary>"钉到桌面"按钮的回调：参数为当前图像在屏幕上的物理像素 rect + 已合成的 PNG 字节。
    /// 字节内容是 PreviewImage + AnnotationCanvas 合成后的最终图像 —— 用户加过的标注（矩形 / 箭头 / 马赛克 / 文字…）
    /// 会被一起钉到桌面，避免出现"截图预览里看到带标注，钉到桌面却变回原图"的视觉断层。
    /// Coordinator 据此创建 PinnedScreenshotWindow 并定位。null 时按钮置灰</summary>
    private readonly Action<WinRectangle, byte[]>? _onPinRequested;

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

    public ScreenshotPreviewWindow(
        ILog log,
        BitmapSource imageSource,
        Func<byte[]> pngBytesProvider,
        WinRectangle anchorPhysical,
        uint anchorDpiX,
        uint anchorDpiY,
        ClipboardWriter clipboard,
        Action<WinRectangle> onOcrRequested,
        Action? onReshootRequested = null,
        Action<WinRectangle, byte[]>? onPinRequested = null)
    {
        _log = log;
        _pngBytesProvider = pngBytesProvider;
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

        // imageSource 由调用方用 OcrImage 的 BGRA32 像素直接 BitmapSource.Create 构造，
        // 已经 Freeze 且带 anchor monitor 的 effective DPI，DIP × 屏幕 DPI = 物理像素，
        // 与 Image Stretch="None" 配合实现 1:1 显示。这里只赋值，避免重复 PNG 解码
        PreviewImage.Source = imageSource;

        Loaded += OnLoaded;
        Closed += OnClosed;
        KeyDown += OnKeyDown;
        Frame.MouseLeftButtonDown += OnFrameMouseLeftButtonDown;

        // 标注画布控制器：在 Loaded 之前可以创建，但 Repaint 依赖 PreviewImage.ActualWidth；
        // 进入标注模式之前不会触发任何重绘，所以这里构造没问题
        _annotationController = new AnnotationCanvasController(AnnotationCanvas, PreviewImage, _annotationStore, _annotationOptions);
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
        // WindowDragHelper.BeginDrag 直接 SetWindowPos，让位移 1:1 跟随鼠标。
        // 主窗每次 SetWindowPos 后通过 onMoved 把工具栏窗口按同样的 (dx, dy) 推过去，
        // 让"图片 + 工具条"作为一个整体跟手，避免拖远后工具条还停在原地的视觉断层
        _toolbarStartRectCaptured = false;
        WindowDragHelper.BeginDrag(this, onMoved: (dx, dy) =>
        {
            if (_toolbarWindow is null) return;
            ApplyToolbarFollowOffset(_toolbarWindow, dx, dy);
        });
    }

    /// <summary>主窗拖动时同步把工具条挪过去。
    ///
    /// 用拖动起点缓存的 startRect 而不是当前 GetWindowRect 累加 —— 前者保证累计位移精确，
    /// 后者会因为拖动期间各种事件抢调度产生漂移；这里在第一次回调时缓存一次起点 rect，
    /// 之后每次都按 (start + delta) 重置，避免错位扩散</summary>
    private NativeMethods.RECT _toolbarStartRectForDrag;
    private bool _toolbarStartRectCaptured;

    private void ApplyToolbarFollowOffset(ScreenshotPreviewToolbarWindow tb, int dx, int dy)
    {
        var hwnd = new WindowInteropHelper(tb).Handle;
        if (hwnd == IntPtr.Zero) return;

        if (!_toolbarStartRectCaptured)
        {
            if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;
            _toolbarStartRectForDrag = rect;
            _toolbarStartRectCaptured = true;
        }
        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            _toolbarStartRectForDrag.Left + dx,
            _toolbarStartRectForDrag.Top + dy,
            0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
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
    /// 有标注时合成 PreviewImage + AnnotationCanvas 再编码为 PNG；无标注时走原 PNG 字节，零开销。
    ///
    /// 异步路径拆分：
    /// 1) UI 线程跑 RenderTargetBitmap.Render —— Visual 树访问必须 UI 线程，无法卸载；
    /// 2) Freeze 让 BitmapSource 跨线程安全后切到 Task.Run；
    /// 3) 后台线程 PngBitmapEncoder.Save 把字节编出来；
    /// 4) 回 UI 线程写剪贴板 + toast。
    /// 这样大图 + 大量标注时 PngEncode 不再阻塞 UI</summary>
    public void CommandCopy(Action<string>? toastSink = null)
    {
        _ = CommandCopyAsync(toastSink);
    }

    private async Task CommandCopyAsync(Action<string>? toastSink)
    {
        try
        {
            var bytes = await ResolveOutputPngBytesAsync().ConfigureAwait(true);
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
    /// 注意：SaveFileDialog 是模态对话框，Owner 给 this 即可保证置顶位置不偏。
    /// 落盘路径：UI 线程 RenderTargetBitmap → 后台 PngEncode + WriteAllBytesAsync → UI toast，
    /// 避免大图 PngEncode + 文件写入阻塞 UI</summary>
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
        _ = CommandSaveAsync(dialog.FileName, toastSink);
    }

    private async Task CommandSaveAsync(string filePath, Action<string>? toastSink)
    {
        try
        {
            var bytes = await ResolveOutputPngBytesAsync().ConfigureAwait(true);
            await File.WriteAllBytesAsync(filePath, bytes).ConfigureAwait(true);
            toastSink?.Invoke("截图已保存");
        }
        catch (Exception ex)
        {
            _log.Warn("screenshot save failed", ("err", ex.Message));
            toastSink?.Invoke("保存失败：" + ex.Message);
        }
    }

    /// <summary>把 PreviewImage + AnnotationCanvas 合成为带标注的最终 PNG。
    /// 无标注时直接走 _pngBytesProvider（Lazy 编码 / 第一次会触发后台编码，命中缓存近零开销），
    /// 避免合成路径无谓的 RenderTargetBitmap + PNG 编码两次开销。
    ///
    /// 必须从 UI 线程调用：RenderTargetBitmap.Render 访问 Visual 树。
    /// Render 完成后 RenderTargetBitmap 已 Freeze，可安全跨线程；PngBitmapEncoder.Save 切到
    /// Task.Run 后台线程，让 UI 在编码期间继续响应（鼠标拖动 / Hover 反馈不冻结）</summary>
    private async Task<byte[]> ResolveOutputPngBytesAsync()
    {
        if (_annotationStore.Items.Count == 0 || _annotationController is null)
        {
            // Lazy provider 首次调用触发实际编码：切到后台线程跑，避免阻塞 UI；
            // 已编码过则命中缓存几乎零成本
            return await Task.Run(_pngBytesProvider).ConfigureAwait(true);
        }
        if (PreviewImage.Source is not BitmapSource source)
        {
            return await Task.Run(_pngBytesProvider).ConfigureAwait(true);
        }

        var bmp = _annotationController.RenderComposite(source);
        return await Task.Run(() =>
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }).ConfigureAwait(false);
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
    /// Coordinator 通过 onPinRequested 回调拿到当前图像物理 rect + 已合成 PNG 字节，
    /// 创建 PinnedScreenshotWindow 并定位</summary>
    public void CommandPin()
    {
        if (_onPinRequested is null) return;
        _ = CommandPinAsync();
    }

    /// <summary>异步路径：先把 PreviewImage + AnnotationCanvas 合成成最终 PNG（含所有已绘标注），
    /// 再回调创建 PinnedScreenshotWindow。这样钉到桌面的图像与预览里看到的内容完全一致。
    ///
    /// 拆 async 方法是因为 CommandPin 由按钮 Click 同步调用，不能直接 await ——
    /// fire-and-forget 调度让按钮点击立即返回，UI 不会因为大图 PngEncode 卡住</summary>
    private async Task CommandPinAsync()
    {
        try
        {
            var anchor = GetCurrentImagePhysicalRect();
            // ResolveOutputPngBytesAsync 在无标注时走 Lazy provider（首次触发后台 PNG 编码，已编码则命中缓存），
            // 有标注时走 RenderTargetBitmap 合成 + 后台线程 PngEncode，让 UI 在编码期间不卡顿
            var bytes = await ResolveOutputPngBytesAsync().ConfigureAwait(true);
            _onPinRequested!(anchor, bytes);
        }
        catch (Exception ex)
        {
            _log.Warn("screenshot pin request failed", ("err", ex.Message));
            return;
        }
        // 钉图已在原位创建，关掉本预览避免两个一模一样的窗口叠在一起。
        // Close 必须在合成 + 回调完成之后做：合成走 RenderTargetBitmap 依赖 PreviewImage Visual 树，
        // 先 Close 会让 Visual 进入 Unloaded 状态，下游合成路径会读到空图
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
