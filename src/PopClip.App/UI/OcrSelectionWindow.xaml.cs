using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using PopClip.Core.Logging;
using PopClip.Hooks.Interop;
using WinPoint = System.Drawing.Point;
using WinRectangle = System.Drawing.Rectangle;

namespace PopClip.App.UI;

/// <summary>区域 OCR / 截图的全屏蒙层选区窗。
/// 设计目标：
/// - 一个跨所有屏幕的全屏窗口（VirtualScreen 物理像素尺寸），鼠标拖框 → 截图 → OCR
/// - 选区中心保持透明让用户能看清要截的内容；外围用 4 块半透明黑色蒙层围合
/// - ESC / 右键 / 双击 → 取消；左键拖框 → 完成
/// - 完成事件交付物理像素 Rect（System.Drawing.Rectangle），方便上层 BitBlt 截屏
///
/// 关键改造：全程用 Win32 物理像素而非 WPF DIP 折算。
/// 旧版用 SystemParameters.VirtualScreenWidth/Height（DIP）+ MouseEventArgs.GetPosition（DIP）
/// 配合 PresentationSource DPI 反算回物理像素，在 PerMonitor V2 awareness 下
/// （尤其混合 DPI 多屏环境）会出现 ±10 ~ ±300 像素的累积误差 —— 用户报告全屏框选时
/// 实际只拿到 2259×1439（屏幕 2560×1440），就是这条 DIP 折算路径在累计精度损失。
///
/// 现在 Win32 SM_XVIRTUALSCREEN / SM_CXVIRTUALSCREEN 直接给物理像素 virtual screen 边界，
/// GetCursorPos 直接给鼠标物理像素坐标 —— 物理像素 rect 全程不经折算，1:1 对齐屏幕。</summary>
internal partial class OcrSelectionWindow : Window
{
    private readonly ILog _log;
    private bool _dragging;
    private WinPoint _dragStartPx;
    private WinPoint _dragCurrentPx;
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;
    private int _virtualLeftPx;
    private int _virtualTopPx;
    private int _virtualWidthPx;
    private int _virtualHeightPx;

    /// <summary>选区完成事件：参数为物理像素矩形（包含 Left/Top/Width/Height）。
    /// 取消（ESC / 右键）时不触发；调用方应同时订阅 Cancelled 以做清理</summary>
    public event Action<WinRectangle>? RegionSelected;
    public event Action? Cancelled;

    public OcrSelectionWindow(ILog log)
    {
        _log = log;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseRightButtonDown += OnRightDown;
        KeyDown += OnKeyDown;
        Loaded += OnLoadedInternal;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // PerMonitor V2 awareness 下 GetSystemMetrics 返回物理像素（与进程 DPI 无关），
        // 直接拿 virtual screen 物理像素四角，避开 SystemParameters.VirtualScreen* 走主屏 DPI 折 DIP 的精度损失
        _virtualLeftPx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        _virtualTopPx = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        _virtualWidthPx = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        _virtualHeightPx = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        // 当前 PresentationSource 的 DPI 用于把"物理像素选区"换算成 WPF Canvas 的 DIP 位置，
        // 仅用于绘制 SelectionRect / Mask 这些视觉层；物理像素选区不依赖此换算
        var primary = PresentationSource.FromVisual(this);
        if (primary?.CompositionTarget is not null)
        {
            _dpiScaleX = primary.CompositionTarget.TransformToDevice.M11;
            _dpiScaleY = primary.CompositionTarget.TransformToDevice.M22;
        }

        // 用 Win32 SetWindowPos 把窗口按 virtual screen 物理像素绝对定位 + 拉伸，
        // OnSourceInitialized 时 HWND 已经创建，跳过 WPF 的 Left/Top/Width/Height
        // DIP 折算，跨屏 / 异构 DPI 环境下窗口边界严格 = SM_XVIRTUALSCREEN ~ SM_CXVIRTUALSCREEN
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_TOPMOST,
                _virtualLeftPx, _virtualTopPx,
                Math.Max(1, _virtualWidthPx), Math.Max(1, _virtualHeightPx),
                NativeMethods.SWP_NOACTIVATE);
        }
    }

    private void OnLoadedInternal(object sender, RoutedEventArgs e)
    {
        // 初始时整屏铺一层蒙层；用户开始拖框后再切到"四周蒙层"展示模式。
        // 不预设 HintBorder 位置：xaml 里默认 Collapsed，UpdateHint 在拖出非零选区时才会显示并定位
        ResetMasks();
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
        SelectionRectOuter.Width = 0;
        SelectionRectOuter.Height = 0;
        Activate();
        Focus();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 一律用 GetCursorPos 取物理像素鼠标坐标：避免 e.GetPosition 在 PerMonitor V2 多屏混合 DPI 下
        // 的 DIP 折算误差。鼠标坐标全程保持物理像素口径，最终给上层的也是物理像素 rect
        if (!NativeMethods.GetCursorPos(out var pt)) return;
        _dragging = true;
        _dragStartPx = new WinPoint(pt.X, pt.Y);
        _dragCurrentPx = _dragStartPx;
        UpdateSelectionVisual();
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (!NativeMethods.GetCursorPos(out var pt)) return;
        _dragCurrentPx = new WinPoint(pt.X, pt.Y);
        UpdateSelectionVisual();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        if (NativeMethods.GetCursorPos(out var pt))
        {
            _dragCurrentPx = new WinPoint(pt.X, pt.Y);
        }
        var physical = CurrentSelectionPx();
        // 鼠标按下后误点的微小矩形（< 6×6 px）视为取消，避免空白截图触发 OCR
        if (physical.Width < 6 || physical.Height < 6)
        {
            RaiseCancelled();
            return;
        }

        // 关键：WPF 的 Close() 仅向消息队列投递 WM_CLOSE，蒙层在屏幕上消失需要等一次 DWM 合成。
        // 必须先把窗口在视觉树上彻底隐藏（Hide + Visibility=Collapsed 让 DWM 跳过合成），
        // 然后用 Dispatcher Background 优先级把截图事件推到下一帧 —— 此时蒙层已不在屏幕上。
        // 之前直接 Close() + Invoke 会让 CopyFromScreen 截到半透明黑色蒙层，
        // detector 还能看出文字框轮廓但 recognizer 拿到的颜色全被压暗 → 输出乱码
        Hide();
        Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Close();
            try { RegionSelected?.Invoke(physical); }
            catch (Exception ex) { _log.Warn("ocr region callback failed", ("err", ex.Message)); }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnRightDown(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
        }
        RaiseCancelled();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            RaiseCancelled();
            e.Handled = true;
        }
    }

    private void RaiseCancelled()
    {
        Close();
        try { Cancelled?.Invoke(); }
        catch (Exception ex) { _log.Warn("ocr cancel callback failed", ("err", ex.Message)); }
    }

    /// <summary>当前选区物理像素 rect（直接基于 GetCursorPos 的物理坐标，不经 DIP 折算）。
    /// Width/Height 用 Abs 容许"任意方向拖框"。
    /// +1 修正：让 rect 包含两端像素。Win32 GetCursorPos 在屏幕物理像素 0 ~ W-1 范围（W 为屏宽），
    /// 用户从屏幕左上 (0,0) 拖到屏幕右下 (W-1, H-1) 时，按 abs(diff) 只能给出 W-1 × H-1，
    /// 差屏幕宽高各 1 像素 —— 用户反馈"全屏截图右下角少了一部分"就源于此。
    /// 改为 max-min+1 后，从屏幕 4 角拖到对角，能给出严格 = 屏幕物理分辨率的 rect</summary>
    private WinRectangle CurrentSelectionPx()
    {
        var x1 = Math.Min(_dragStartPx.X, _dragCurrentPx.X);
        var y1 = Math.Min(_dragStartPx.Y, _dragCurrentPx.Y);
        var x2 = Math.Max(_dragStartPx.X, _dragCurrentPx.X);
        var y2 = Math.Max(_dragStartPx.Y, _dragCurrentPx.Y);
        return new WinRectangle(x1, y1, x2 - x1 + 1, y2 - y1 + 1);
    }

    private void UpdateSelectionVisual()
    {
        var physical = CurrentSelectionPx();
        // 视觉层走 DIP：把物理像素 rect 平移 - 减去 virtualLeftPx，再除以 dpiScale，得到 Canvas 内 DIP 坐标。
        // 物理像素 rect 自身已经是权威数据源，DIP rect 只是给用户看的反馈
        double leftDip = (physical.X - _virtualLeftPx) / _dpiScaleX;
        double topDip = (physical.Y - _virtualTopPx) / _dpiScaleY;
        double widthDip = physical.Width / _dpiScaleX;
        double heightDip = physical.Height / _dpiScaleY;

        Canvas.SetLeft(SelectionRectOuter, leftDip);
        Canvas.SetTop(SelectionRectOuter, topDip);
        SelectionRectOuter.Width = widthDip;
        SelectionRectOuter.Height = heightDip;
        Canvas.SetLeft(SelectionRect, leftDip);
        Canvas.SetTop(SelectionRect, topDip);
        SelectionRect.Width = widthDip;
        SelectionRect.Height = heightDip;
        UpdateMasksAround(leftDip, topDip, widthDip, heightDip);
        UpdateHint(physical, leftDip, topDip, widthDip, heightDip);
    }

    /// <summary>选区周围的四块蒙层：上方覆盖到选区顶部、下方从选区底部往下、左右两侧填补中央带。
    /// 这样选区中心是完全透明的，让用户能精确看到要 OCR 的内容</summary>
    private void UpdateMasksAround(double selX, double selY, double selW, double selH)
    {
        var w = ActualWidth > 0 ? ActualWidth : _virtualWidthPx / _dpiScaleX;
        var h = ActualHeight > 0 ? ActualHeight : _virtualHeightPx / _dpiScaleY;

        Canvas.SetLeft(MaskTop, 0); Canvas.SetTop(MaskTop, 0);
        MaskTop.Width = w; MaskTop.Height = Math.Max(0, selY);

        Canvas.SetLeft(MaskBottom, 0); Canvas.SetTop(MaskBottom, selY + selH);
        MaskBottom.Width = w; MaskBottom.Height = Math.Max(0, h - (selY + selH));

        Canvas.SetLeft(MaskLeft, 0); Canvas.SetTop(MaskLeft, selY);
        MaskLeft.Width = Math.Max(0, selX); MaskLeft.Height = selH;

        Canvas.SetLeft(MaskRight, selX + selW); Canvas.SetTop(MaskRight, selY);
        MaskRight.Width = Math.Max(0, w - (selX + selW)); MaskRight.Height = selH;
    }

    private void ResetMasks()
    {
        var w = _virtualWidthPx / _dpiScaleX;
        var h = _virtualHeightPx / _dpiScaleY;
        Canvas.SetLeft(MaskTop, 0); Canvas.SetTop(MaskTop, 0);
        MaskTop.Width = w; MaskTop.Height = h;
        MaskBottom.Width = 0; MaskBottom.Height = 0;
        MaskLeft.Width = 0; MaskLeft.Height = 0;
        MaskRight.Width = 0; MaskRight.Height = 0;
    }

    private void UpdateHint(WinRectangle physical, double selX, double selY, double selW, double selH)
    {
        // 没有实际拖出选区时不展示任何提示，避免视觉污染。
        // 拖出有效面积时只展示"宽 × 高 px"，不再附带"ESC 取消 / 松开鼠标识别"等引导
        if (physical.Width < 1 || physical.Height < 1)
        {
            HintBorder.Visibility = Visibility.Collapsed;
            return;
        }
        HintBorder.Visibility = Visibility.Visible;
        HintText.Text = $"{physical.Width} × {physical.Height} px";
        // 提示框跟随选区右下角；超出屏幕时翻到选区上方
        var px = selX + selW + 12;
        var py = selY + selH + 12;
        if (px + 200 > ActualWidth) px = Math.Max(8, selX);
        if (py + 32 > ActualHeight) py = Math.Max(8, selY - 36);
        Canvas.SetLeft(HintBorder, px);
        Canvas.SetTop(HintBorder, py);
    }
}
