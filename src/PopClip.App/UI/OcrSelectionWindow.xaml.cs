using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PopClip.Core.Logging;

namespace PopClip.App.UI;

/// <summary>区域 OCR 的全屏蒙层选区窗。
/// 设计目标：
/// - 一个跨所有屏幕的全屏窗口（VirtualScreen 尺寸），鼠标拖框 → 截图 → OCR
/// - 选区中心保持透明让用户能看清要截的内容；外围用 4 块半透明黑色蒙层围合
/// - ESC / 右键 / 双击 → 取消；左键拖框 → 完成
/// - 完成事件交付物理像素 Rect（System.Drawing.Rectangle），方便上层 BitBlt 截屏</summary>
internal partial class OcrSelectionWindow : Window
{
    private readonly ILog _log;
    private bool _dragging;
    private Point _dragStartDip;
    private Point _dragCurrentDip;
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;
    private int _virtualLeftPx;
    private int _virtualTopPx;

    /// <summary>选区完成事件：参数为物理像素矩形（包含 Left/Top/Width/Height）。
    /// 取消（ESC / 右键）时不触发；调用方应同时订阅 Cancelled 以做清理</summary>
    public event Action<System.Drawing.Rectangle>? RegionSelected;
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
        // 跨多屏：用 SystemParameters 的 VirtualScreen 取所有屏物理像素并集，作为窗口几何范围。
        // 全屏窗口在多显卡 DPI 环境下要把 PerMonitorV2 DPI awareness 关掉，
        // 让窗口按 system DPI 处理 —— 否则不同显示器各自 scale，Canvas 内部坐标会错乱。
        // 这里依赖 app.manifest 中已有的 PerMonitorV2 设置，无法局部关掉，因此简单做法是
        // 取主屏 DPI 做整窗换算（多显异构 DPI 时选区在副屏会偏差几像素，可接受）
        var primary = PresentationSource.FromVisual(this);
        if (primary?.CompositionTarget is not null)
        {
            _dpiScaleX = primary.CompositionTarget.TransformToDevice.M11;
            _dpiScaleY = primary.CompositionTarget.TransformToDevice.M22;
        }

        // SystemParameters.VirtualScreen* 返回 DIP，需转物理像素作为返回给 OCR 的截图坐标基准
        _virtualLeftPx = (int)Math.Round(SystemParameters.VirtualScreenLeft * _dpiScaleX);
        _virtualTopPx = (int)Math.Round(SystemParameters.VirtualScreenTop * _dpiScaleY);
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    private void OnLoadedInternal(object sender, RoutedEventArgs e)
    {
        // 初始时整屏铺一层蒙层；用户开始拖框后再切到"四周蒙层"展示模式
        ResetMasks();
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
        SelectionRectOuter.Width = 0;
        SelectionRectOuter.Height = 0;
        Canvas.SetLeft(HintBorder, 16);
        Canvas.SetTop(HintBorder, 16);
        Activate();
        Focus();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStartDip = e.GetPosition(RootCanvas);
        _dragCurrentDip = _dragStartDip;
        UpdateSelectionVisual();
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragCurrentDip = e.GetPosition(RootCanvas);
        UpdateSelectionVisual();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        var rect = CurrentSelectionDip();
        // 鼠标按下后误点的微小矩形（< 6×6 DIP）视为取消，避免空白截图触发 OCR
        if (rect.Width < 6 || rect.Height < 6)
        {
            RaiseCancelled();
            return;
        }
        // DIP → 物理像素，并叠加 VirtualScreen 偏移（多屏副屏可能是负坐标）
        var physical = new System.Drawing.Rectangle(
            _virtualLeftPx + (int)Math.Round(rect.X * _dpiScaleX),
            _virtualTopPx + (int)Math.Round(rect.Y * _dpiScaleY),
            (int)Math.Round(rect.Width * _dpiScaleX),
            (int)Math.Round(rect.Height * _dpiScaleY));
        // 立即关闭窗口再触发事件：截图过程中蒙层还在屏幕上会被一同截入
        Close();
        try { RegionSelected?.Invoke(physical); }
        catch (Exception ex) { _log.Warn("ocr region callback failed", ("err", ex.Message)); }
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

    private Rect CurrentSelectionDip()
    {
        var x = Math.Min(_dragStartDip.X, _dragCurrentDip.X);
        var y = Math.Min(_dragStartDip.Y, _dragCurrentDip.Y);
        var w = Math.Abs(_dragStartDip.X - _dragCurrentDip.X);
        var h = Math.Abs(_dragStartDip.Y - _dragCurrentDip.Y);
        return new Rect(x, y, w, h);
    }

    private void UpdateSelectionVisual()
    {
        var rect = CurrentSelectionDip();
        Canvas.SetLeft(SelectionRectOuter, rect.X);
        Canvas.SetTop(SelectionRectOuter, rect.Y);
        SelectionRectOuter.Width = rect.Width;
        SelectionRectOuter.Height = rect.Height;
        Canvas.SetLeft(SelectionRect, rect.X);
        Canvas.SetTop(SelectionRect, rect.Y);
        SelectionRect.Width = rect.Width;
        SelectionRect.Height = rect.Height;
        UpdateMasksAround(rect);
        UpdateHint(rect);
    }

    /// <summary>选区周围的四块蒙层：上方覆盖到选区顶部、下方从选区底部往下、左右两侧填补中央带。
    /// 这样选区中心是完全透明的，让用户能精确看到要 OCR 的内容</summary>
    private void UpdateMasksAround(Rect sel)
    {
        var w = ActualWidth > 0 ? ActualWidth : SystemParameters.VirtualScreenWidth;
        var h = ActualHeight > 0 ? ActualHeight : SystemParameters.VirtualScreenHeight;

        Canvas.SetLeft(MaskTop, 0); Canvas.SetTop(MaskTop, 0);
        MaskTop.Width = w; MaskTop.Height = Math.Max(0, sel.Y);

        Canvas.SetLeft(MaskBottom, 0); Canvas.SetTop(MaskBottom, sel.Y + sel.Height);
        MaskBottom.Width = w; MaskBottom.Height = Math.Max(0, h - (sel.Y + sel.Height));

        Canvas.SetLeft(MaskLeft, 0); Canvas.SetTop(MaskLeft, sel.Y);
        MaskLeft.Width = Math.Max(0, sel.X); MaskLeft.Height = sel.Height;

        Canvas.SetLeft(MaskRight, sel.X + sel.Width); Canvas.SetTop(MaskRight, sel.Y);
        MaskRight.Width = Math.Max(0, w - (sel.X + sel.Width)); MaskRight.Height = sel.Height;
    }

    private void ResetMasks()
    {
        var w = SystemParameters.VirtualScreenWidth;
        var h = SystemParameters.VirtualScreenHeight;
        Canvas.SetLeft(MaskTop, 0); Canvas.SetTop(MaskTop, 0);
        MaskTop.Width = w; MaskTop.Height = h;
        MaskBottom.Width = 0; MaskBottom.Height = 0;
        MaskLeft.Width = 0; MaskLeft.Height = 0;
        MaskRight.Width = 0; MaskRight.Height = 0;
    }

    private void UpdateHint(Rect rect)
    {
        var sizeText = $"{(int)Math.Round(rect.Width * _dpiScaleX)} × {(int)Math.Round(rect.Height * _dpiScaleY)} px";
        HintText.Text = $"{sizeText} · ESC 取消 · 松开鼠标识别";
        // 提示框跟随选区右下角；超出屏幕时翻到选区上方
        var px = rect.X + rect.Width + 12;
        var py = rect.Y + rect.Height + 12;
        if (px + 200 > ActualWidth) px = Math.Max(8, rect.X);
        if (py + 32 > ActualHeight) py = Math.Max(8, rect.Y - 36);
        Canvas.SetLeft(HintBorder, px);
        Canvas.SetTop(HintBorder, py);
    }
}
