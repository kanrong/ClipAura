using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PopClip.App.UI.Annotation;

/// <summary>把 AnnotationCanvas 的鼠标 / 键盘交互、Annotation 渲染、文本输入弹层
/// 全部封装在一个控制器里。ScreenshotPreviewWindow 只持有它一个引用，方便以后拓展更多工具。
///
/// 核心循环：
/// 1. 进入标注模式 → IsActive=true，PreviewImage 上方覆盖透明 Canvas，Cursor=Cross；
/// 2. 鼠标按下 → 按当前工具初始化 _draftPoints / _draftStart，添加临时 _draftElement 到 Canvas；
/// 3. 鼠标移动 → 更新 _draftPoints / _draftElement，实时反馈；
/// 4. 鼠标抬起 → 把 draft 转成 Annotation 存入 store，store.Changed 触发 Repaint。
///
/// 文本工具特例：单击 → 弹出可输入 TextBox 浮层，用户输入后 Enter / 失焦 → 存为 Annotation。
/// 涂鸦工具特例：所有 PointerMove 累计到 _draftPoints，PointerUp 时整体存盘</summary>
internal sealed class AnnotationCanvasController
{
    private readonly Canvas _canvas;
    private readonly System.Windows.Controls.Image _sourceImage;
    private readonly AnnotationStore _store;
    private readonly AnnotationToolOptions _options;

    private bool _active;
    private bool _drawing;
    private Point _draftStart;
    private List<Point>? _draftFreehandPoints;
    private FrameworkElement? _draftElement;

    /// <summary>文本工具浮层 TextBox：用户在画布上单击空白处，会立刻显示一个空 TextBox 让用户键入。
    /// Enter / 失焦时把内容固化为 Annotation；ESC 取消则丢弃。同一时刻最多一个文本浮层</summary>
    private TextBox? _textInput;

    public AnnotationCanvasController(Canvas canvas, System.Windows.Controls.Image sourceImage, AnnotationStore store, AnnotationToolOptions options)
    {
        _canvas = canvas;
        _sourceImage = sourceImage;
        _store = store;
        _options = options;

        _canvas.MouseLeftButtonDown += OnMouseDown;
        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseLeftButtonUp += OnMouseUp;
        _canvas.MouseLeave += OnMouseLeave;

        _store.Changed += Repaint;
    }

    public bool IsActive => _active;

    /// <summary>切换标注模式：true → 显示画布、拦截事件；false → 隐藏画布、放行点击给主图</summary>
    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        _canvas.IsHitTestVisible = active;
        _canvas.Cursor = active ? Cursors.Cross : Cursors.Arrow;
        // 退出标注模式时若有未提交的文本浮层 / draft，立刻提交
        if (!active)
        {
            CommitTextInputIfAny();
            CancelDraftIfAny();
        }
    }

    /// <summary>把 store.Items 完整重绘到 Canvas。
    /// 不复用旧 element：每次重画一次 —— 标注数量 v1 上限通常 &lt; 30 个，重绘成本可忽略，
    /// 换来"撤销 / 重做"路径上的渲染状态零侵入</summary>
    public void Repaint()
    {
        // 清掉旧的渲染元素，但保留 _draftElement / _textInput（活跃的临时层）
        var children = _canvas.Children;
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var c = children[i];
            if (ReferenceEquals(c, _draftElement)) continue;
            if (ReferenceEquals(c, _textInput)) continue;
            children.RemoveAt(i);
        }

        var src = _sourceImage.Source as BitmapSource;
        var w = _sourceImage.ActualWidth > 0 ? _sourceImage.ActualWidth : _sourceImage.Width;
        var h = _sourceImage.ActualHeight > 0 ? _sourceImage.ActualHeight : _sourceImage.Height;
        foreach (var a in _store.Items)
        {
            try
            {
                var el = AnnotationRenderer.Render(a, src, w, h);
                _canvas.Children.Add(el);
            }
            catch { }
        }
    }

    /// <summary>把当前已落地的 Annotation + 源图合成为一张 RenderTargetBitmap，
    /// 用于复制到剪贴板 / 保存 PNG 时输出"带标注的最终图"。
    /// 输出像素尺寸严格等于 source.PixelWidth × source.PixelHeight，保证 DPI 维持源图原 DPI</summary>
    public RenderTargetBitmap RenderComposite(BitmapSource source)
    {
        // 把 source + canvas 共同绘制到一个 DrawingVisual：
        // 1) 先画 source；2) 再用 VisualBrush 把 Canvas 绘成等大覆盖层。
        // 直接 RenderTargetBitmap 一次性渲染整个 PreviewImage + Canvas Grid 也可，
        // 但那会受窗口大小 / Stretch 影响；自己控 DrawingVisual 最稳
        var w = source.PixelWidth;
        var h = source.PixelHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(0, 0, w, h));

            // Canvas 的 ActualWidth/Height 是 DIP，要把 Canvas 内容缩放到 source 像素坐标。
            // VisualBrush + ScaleTransform 的组合可以让一个 Brush 把整个 Canvas 当源像素直接绘制
            var canvasW = _canvas.ActualWidth > 0 ? _canvas.ActualWidth : w;
            var canvasH = _canvas.ActualHeight > 0 ? _canvas.ActualHeight : h;
            var brush = new VisualBrush(_canvas)
            {
                Stretch = Stretch.None,
                Viewport = new Rect(0, 0, 1, 1),
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewbox = new Rect(0, 0, canvasW, canvasH),
                ViewboxUnits = BrushMappingMode.Absolute,
            };
            dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
        }
        var bmp = new RenderTargetBitmap(w, h, source.DpiX, source.DpiY, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    // ============= 鼠标交互 =============

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_active) return;
        // 文本浮层激活中：让 TextBox 自己处理事件
        if (_textInput is not null && _textInput.IsKeyboardFocused) return;
        // 用户点空白处：先提交可能存在的旧文本浮层，再处理本次按下
        CommitTextInputIfAny();

        var p = e.GetPosition(_canvas);
        if (_options.Kind == AnnotationKind.Text)
        {
            ShowTextInputAt(p);
            e.Handled = true;
            return;
        }

        _drawing = true;
        _draftStart = p;
        if (_options.Kind == AnnotationKind.Freehand)
        {
            _draftFreehandPoints = new List<Point> { p };
        }
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_active || !_drawing) return;
        var p = e.GetPosition(_canvas);
        if (_options.Kind == AnnotationKind.Freehand && _draftFreehandPoints is not null)
        {
            _draftFreehandPoints.Add(p);
            ReplaceDraft(BuildDraftFreehand(_draftFreehandPoints));
            return;
        }

        ReplaceDraft(BuildDraftFromBounds(_draftStart, p));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_active || !_drawing)
        {
            _drawing = false;
            return;
        }
        _drawing = false;
        _canvas.ReleaseMouseCapture();

        var p = e.GetPosition(_canvas);
        Annotation? finalAnn;
        if (_options.Kind == AnnotationKind.Freehand)
        {
            if (_draftFreehandPoints is null || _draftFreehandPoints.Count < 2)
            {
                CancelDraftIfAny();
                return;
            }
            finalAnn = Annotation.CreateFreehand(_draftFreehandPoints, _options.Color, _options.StrokeThickness);
            _draftFreehandPoints = null;
        }
        else
        {
            finalAnn = BuildAnnotationFromBounds(_draftStart, p);
        }

        // 移除临时 draft 元素，store.Add 后会通过 Repaint 重新画 final 元素
        if (_draftElement is not null)
        {
            _canvas.Children.Remove(_draftElement);
            _draftElement = null;
        }
        if (finalAnn is null) return;
        // 矩形 / 椭圆 / 遮罩等需要面积；过小的拖动当作误触舍弃
        if (IsTrivialBounds(finalAnn)) return;
        _store.Add(finalAnn);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_active || !_drawing) return;
        // 鼠标拖到 canvas 外仍保留 capture，正常路径会在 MouseUp 收尾；
        // 这里只处理 capture 已丢失（少数极端情况）的 fallback：清掉 draft 让用户重画
        if (!_canvas.IsMouseCaptured)
        {
            CancelDraftIfAny();
            _drawing = false;
        }
    }

    private bool IsTrivialBounds(Annotation a)
    {
        if (a.Kind is AnnotationKind.Freehand or AnnotationKind.Text) return false;
        if (a.Kind is AnnotationKind.Line or AnnotationKind.Arrow)
        {
            // 线段长度 < 2 DIP 视为点，舍弃
            var p0 = a.Points[0];
            var p1 = a.Points[1];
            var dx = p1.X - p0.X;
            var dy = p1.Y - p0.Y;
            return dx * dx + dy * dy < 4;
        }
        return a.Bounds.Width < 3 || a.Bounds.Height < 3;
    }

    private FrameworkElement BuildDraftFromBounds(Point a, Point b)
    {
        // 临时 draft 与最终 Annotation 视觉一致：直接 build 一个 Annotation 渲染即可。
        // Mosaic / Blur 在 draft 阶段也用同款渲染，让用户实时预览效果
        var ann = BuildAnnotationFromBounds(a, b);
        if (ann is null) return new System.Windows.Shapes.Rectangle();
        var src = _sourceImage.Source as BitmapSource;
        var w = _sourceImage.ActualWidth > 0 ? _sourceImage.ActualWidth : _sourceImage.Width;
        var h = _sourceImage.ActualHeight > 0 ? _sourceImage.ActualHeight : _sourceImage.Height;
        return AnnotationRenderer.Render(ann, src, w, h);
    }

    private FrameworkElement BuildDraftFreehand(List<Point> points)
    {
        var ann = Annotation.CreateFreehand(points, _options.Color, _options.StrokeThickness);
        var src = _sourceImage.Source as BitmapSource;
        var w = _sourceImage.ActualWidth > 0 ? _sourceImage.ActualWidth : _sourceImage.Width;
        var h = _sourceImage.ActualHeight > 0 ? _sourceImage.ActualHeight : _sourceImage.Height;
        return AnnotationRenderer.Render(ann, src, w, h);
    }

    private Annotation? BuildAnnotationFromBounds(Point start, Point end)
    {
        var bounds = new Rect(
            Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
            Math.Abs(start.X - end.X), Math.Abs(start.Y - end.Y));
        return _options.Kind switch
        {
            AnnotationKind.Rectangle => Annotation.CreateRectangle(bounds, _options.Color, _options.StrokeThickness, _options.Filled),
            AnnotationKind.Ellipse => Annotation.CreateEllipse(bounds, _options.Color, _options.StrokeThickness, _options.Filled),
            AnnotationKind.Line => Annotation.CreateLine(start, end, _options.Color, _options.StrokeThickness),
            AnnotationKind.Arrow => Annotation.CreateArrow(start, end, _options.Color, _options.StrokeThickness),
            AnnotationKind.Mosaic => Annotation.CreateMosaic(bounds, _options.MosaicBlockSize),
            AnnotationKind.Blur => Annotation.CreateBlur(bounds, _options.BlurRadius),
            AnnotationKind.Mask => Annotation.CreateMask(bounds, _options.Color),
            _ => null,
        };
    }

    private void ReplaceDraft(FrameworkElement el)
    {
        if (_draftElement is not null) _canvas.Children.Remove(_draftElement);
        _draftElement = el;
        _canvas.Children.Add(el);
    }

    private void CancelDraftIfAny()
    {
        if (_draftElement is not null) _canvas.Children.Remove(_draftElement);
        _draftElement = null;
        _draftFreehandPoints = null;
        if (_canvas.IsMouseCaptured) _canvas.ReleaseMouseCapture();
    }

    // ============= 文本工具浮层 =============

    private void ShowTextInputAt(Point p)
    {
        // 文本工具的浮层：让用户键入后 Enter / 失焦提交。
        // 字号 / 颜色取自当前 ToolOptions；TextBox 的 Foreground 与最终渲染颜色一致，避免预期差
        _textInput = new TextBox
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)),
            Foreground = new SolidColorBrush(_options.Color),
            BorderBrush = new SolidColorBrush(_options.Color),
            BorderThickness = new Thickness(1),
            FontSize = _options.FontSize,
            FontWeight = FontWeights.SemiBold,
            CaretBrush = new SolidColorBrush(_options.Color),
            MinWidth = 60,
            Padding = new Thickness(4, 1, 4, 1),
            AcceptsReturn = false,
        };
        Canvas.SetLeft(_textInput, p.X);
        Canvas.SetTop(_textInput, p.Y);
        _textInput.KeyDown += OnTextInputKeyDown;
        _textInput.LostKeyboardFocus += OnTextInputLostFocus;
        _canvas.Children.Add(_textInput);
        _textInput.Focus();
        Keyboard.Focus(_textInput);
    }

    private void OnTextInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitTextInputIfAny();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DiscardTextInputIfAny();
        }
    }

    private void OnTextInputLostFocus(object? sender, KeyboardFocusChangedEventArgs e)
    {
        CommitTextInputIfAny();
    }

    private void CommitTextInputIfAny()
    {
        if (_textInput is null) return;
        var tb = _textInput;
        // 先解绑事件再 detach，避免移除 Canvas 时 LostFocus 再触发一次造成递归
        tb.KeyDown -= OnTextInputKeyDown;
        tb.LostKeyboardFocus -= OnTextInputLostFocus;
        var text = tb.Text ?? "";
        var x = Canvas.GetLeft(tb);
        var y = Canvas.GetTop(tb);
        var bounds = new Rect(double.IsFinite(x) ? x : 0, double.IsFinite(y) ? y : 0, tb.ActualWidth, tb.ActualHeight);
        _canvas.Children.Remove(tb);
        _textInput = null;

        if (string.IsNullOrWhiteSpace(text)) return;
        _store.Add(Annotation.CreateText(bounds, text.Trim(), _options.Color, _options.FontSize));
    }

    private void DiscardTextInputIfAny()
    {
        if (_textInput is null) return;
        var tb = _textInput;
        tb.KeyDown -= OnTextInputKeyDown;
        tb.LostKeyboardFocus -= OnTextInputLostFocus;
        _canvas.Children.Remove(tb);
        _textInput = null;
    }
}
