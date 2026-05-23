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
    /// <summary>选区交互状态机：
    /// Idle — 等待用户第一次按下鼠标；
    /// Dragging — 用户按住鼠标拖框中（按下到松开期间）；
    /// WaitingForEnd — 用户在 Dragging 中"按下即松开"几乎没移动，把这次松开点视作起点，
    ///   等待下一次单击来确定终点。在此状态下鼠标移动会实时更新预览选区，方便用户调整终点。
    ///
    /// 设计动机：原版只支持"按住拖框"，对手腕力量小或触控板用户不友好；
    /// 新增"单击起点 + 再次单击终点"路径让用户可以分两步精确定位选区角点。</summary>
    private enum SelectionMode { Idle, Dragging, WaitingForEnd }

    /// <summary>把 MouseDown 到 MouseUp 的位移视为"误点 / 单击"的阈值（物理像素）。
    /// 仅当位移小于此阈值时切到 WaitingForEnd，否则按拖框完成选区。
    /// 数值 5 既能容忍触控板的轻微抖动，也不会把短拖框误判为单击</summary>
    private const int ClickMovementThresholdPx = 5;

    private readonly ILog _log;
    private SelectionMode _mode = SelectionMode.Idle;
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
        // 初始把"差集外圈"设为整个全屏蒙层 DIP 区域；内圈选区清零，
        // 视觉上 = 全屏铺一层 #99000000 蒙层（与旧 ResetMasks 等价但只更新两个 RectangleGeometry）
        MaskOuterGeometry.Rect = new Rect(0, 0,
            _virtualWidthPx / _dpiScaleX,
            _virtualHeightPx / _dpiScaleY);
        MaskInnerGeometry.Rect = Rect.Empty;
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
        var currentPx = new WinPoint(pt.X, pt.Y);

        if (_mode == SelectionMode.WaitingForEnd)
        {
            // 单击模式第二步：当前点击位置即终点，立刻完成选区
            _dragCurrentPx = currentPx;
            FinalizeSelection();
            return;
        }

        // 进入 Dragging：等到 MouseUp 才能判断本次是"拖框"还是"单击设起点"
        _mode = SelectionMode.Dragging;
        _dragStartPx = currentPx;
        _dragCurrentPx = currentPx;
        UpdateSelectionVisual();
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        // Dragging 和 WaitingForEnd 都要跟随鼠标实时刷新选区预览，
        // 单击模式下移动鼠标也能即时看到"起点固定 / 终点跟手"的方框
        if (_mode == SelectionMode.Idle) return;
        if (!NativeMethods.GetCursorPos(out var pt)) return;
        _dragCurrentPx = new WinPoint(pt.X, pt.Y);
        UpdateSelectionVisual();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode != SelectionMode.Dragging) return;
        ReleaseMouseCapture();
        if (NativeMethods.GetCursorPos(out var pt))
        {
            _dragCurrentPx = new WinPoint(pt.X, pt.Y);
        }

        // 位移小于阈值视为单击：保留 _dragStartPx 作为起点，切到 WaitingForEnd 等待第二次单击。
        // 注意 _dragCurrentPx 此刻已经等于松开点（在原地或附近），UI 会展示一个微小或空选区，
        // 用户挪动鼠标时 OnMouseMove 会按 currentPx 把另一角带过去
        int dx = Math.Abs(_dragCurrentPx.X - _dragStartPx.X);
        int dy = Math.Abs(_dragCurrentPx.Y - _dragStartPx.Y);
        if (dx < ClickMovementThresholdPx && dy < ClickMovementThresholdPx)
        {
            _mode = SelectionMode.WaitingForEnd;
            UpdateSelectionVisual();
            return;
        }

        FinalizeSelection();
    }

    private void OnRightDown(object sender, MouseButtonEventArgs e)
    {
        // 任何模式下右键都立刻取消，包括 WaitingForEnd 时让用户能放弃这次"已设起点"的状态。
        // 鼠标 capture / mode 重置统一交给 RaiseCancelled 处理
        RaiseCancelled();
    }

    /// <summary>真正提交本次选区。Dragging 路径下由 MouseUp 调用，WaitingForEnd 路径下由 MouseDown 调用。
    /// 内部判断选区尺寸合法性（&lt; 6 px 视为取消），合法则按原 Hide → Dispatcher.Background → Close 顺序
    /// 把蒙层从合成里彻底移除后再触发 RegionSelected，避免上层 CopyFromScreen 截到半透明黑蒙层</summary>
    private void FinalizeSelection()
    {
        var physical = CurrentSelectionPx();
        if (physical.Width < 6 || physical.Height < 6)
        {
            // 误操作场景：单击模式下两次点击位置过于接近，或拖框只画了几像素，
            // 视为放弃本次选区，直接取消并清理状态
            _mode = SelectionMode.Idle;
            RaiseCancelled();
            return;
        }

        _mode = SelectionMode.Idle;
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
        // Dragging 中 ESC 没经过 OnMouseUp 释放捕获时会留下 Mouse.Capture 残留，
        // 影响后续窗口的鼠标事件分发；保险起见显式释放一次
        if (_mode == SelectionMode.Dragging) ReleaseMouseCapture();
        _mode = SelectionMode.Idle;
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
        UpdateMaskInner(leftDip, topDip, widthDip, heightDip);
        UpdateHint(physical, leftDip, topDip, widthDip, heightDip);
    }

    /// <summary>更新 CombinedGeometry 内圈（选区透明区域）。
    /// MaskOuterGeometry（全屏）只在 OnLoaded 设置一次后不动，OnMouseMove 频繁路径下
    /// 只改 MaskInnerGeometry.Rect 一个属性 —— Path 是单个 Visual，不触发任何 element 的
    /// layout pass，仅 GPU 重新计算 path 差集后 alpha blend。
    /// 对比旧 4-Rectangle 方案：每帧需要 4 个 element 各自重新 measure / arrange + 大块
    /// 半透明 GPU 重绘，缩小操作（一侧 mask 大幅扩大）会明显卡顿，鼠标快速移入旧选区
    /// 内部时框选追不上鼠标。换成 Path 后这两条路径都流畅</summary>
    private void UpdateMaskInner(double selX, double selY, double selW, double selH)
    {
        if (selW <= 0 || selH <= 0)
        {
            MaskInnerGeometry.Rect = Rect.Empty;
            return;
        }
        MaskInnerGeometry.Rect = new Rect(selX, selY, selW, selH);
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
        // WaitingForEnd 状态下用户已经"单击设了起点"但还没确定终点 —— 这是一个非典型操作，
        // 第一次使用的用户容易困惑。在尺寸文本后附上"再次单击确认终点"的引导，
        // 让用户明确知道接下来该做什么。Dragging 状态下保持纯尺寸显示，不打扰
        HintText.Text = _mode == SelectionMode.WaitingForEnd
            ? $"{physical.Width} × {physical.Height} px · 再次单击确认终点"
            : $"{physical.Width} × {physical.Height} px";
        // 提示框跟随选区右下角；超出屏幕时翻到选区上方
        var px = selX + selW + 12;
        var py = selY + selH + 12;
        if (px + 200 > ActualWidth) px = Math.Max(8, selX);
        if (py + 32 > ActualHeight) py = Math.Max(8, selY - 36);
        Canvas.SetLeft(HintBorder, px);
        Canvas.SetTop(HintBorder, py);
    }
}
