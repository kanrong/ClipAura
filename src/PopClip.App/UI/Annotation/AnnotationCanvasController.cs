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

    /// <summary>自由涂鸦 draft 阶段缓存的 Polyline 引用。
    /// 用同一份 Polyline + 同一个 PointCollection，MouseMove 直接 Add 新点即可，
    /// 避免每次都重建 PointCollection 把所有点重新 Add 一遍（旧实现累计 O(n²)）。
    /// MouseUp 提交完真实 Annotation 后置 null，下次按下再创建</summary>
    private System.Windows.Shapes.Polyline? _draftFreehandPolyline;

    /// <summary>文本工具浮层 TextBox：用户在画布上单击空白处，会立刻显示一个空 TextBox 让用户键入。
    /// Enter / 失焦时把内容固化为 Annotation；ESC 取消则丢弃。同一时刻最多一个文本浮层</summary>
    private TextBox? _textInput;

    /// <summary>当前 _textInput 正在"二次编辑"的旧 Text Annotation。
    /// 提交时用 _editingTarget 找到原条目并整体替换，让历史栈不会出现"新旧两条文字叠在一起"</summary>
    private Annotation? _editingTarget;

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
        // 当鼠标点击落到 TextBox 内部时，TextBox 自身会 Handled=true 处理（移动 caret 等），
        // 事件不会冒泡到 Canvas → 此 handler 不会触发。所以走到这里几乎都是"点击 TextBox 外部的
        // Canvas 区域"，应该把当前浮层固化为 Annotation，再继续按当前工具处理本次点击 ——
        // 不能在这里 return，否则点击会被吞掉、文本浮层永远不结束（这是先前 v2 的 bug）
        CommitTextInputIfAny();

        var p = e.GetPosition(_canvas);
        if (_options.Kind == AnnotationKind.Text)
        {
            // 优先看是否点中已落地的 Text 标注：命中则进入"二次编辑"模式，
            // 否则才在该位置开新文本框。命中检测自己用 Bounds 包含点判定，
            // 避免依赖 VisualTreeHelper.HitTest（TextBlock 渲染后 ActualWidth 可能 0）
            var hit = HitTestExistingText(p);
            if (hit is not null)
            {
                BeginEditExistingText(hit);
            }
            else
            {
                ShowTextInputAt(p);
            }
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

    /// <summary>遍历 store 找出包含点 p 的 Text Annotation。
    /// 倒序遍历让"后画的覆盖先画的"——与 Z-order 视觉一致。
    /// Bounds 是文本框的 DIP 矩形，与 AnnotationRenderer.CreateText 的位置对齐</summary>
    private Annotation? HitTestExistingText(Point p)
    {
        var items = _store.Items;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            var a = items[i];
            if (a.Kind != AnnotationKind.Text) continue;
            // 文字 Annotation 的 Bounds 是初次提交时 TextBox 的尺寸；ActualWidth/Height 可能为 0
            // （例如 redo 后渲染前），那时候用一个最小命中半径兜底
            var b = a.Bounds;
            double w = b.Width > 0 ? b.Width : 80;
            double h = b.Height > 0 ? b.Height : Math.Max(16, a.FontSize <= 0 ? 16 : a.FontSize);
            var rect = new Rect(b.X, b.Y, w, h);
            if (rect.Contains(p)) return a;
        }
        return null;
    }

    /// <summary>把已落地的 Text Annotation 切换为浮层 TextBox 让用户继续编辑。
    /// 实现策略：暂时从 store 中移除原条目（避免编辑期间渲染层叠原文字 + 输入框），
    /// 编辑完成时按 Replace 语义重新写入；用户 ESC 取消则把原条目回滚回去</summary>
    private void BeginEditExistingText(Annotation target)
    {
        _editingTarget = target;
        // 暂时从 store 移除：让 Repaint 不再绘制旧文字。Remove 也会触发 Repaint
        _store.Remove(target);

        var p = new Point(target.Bounds.X, target.Bounds.Y);
        ShowTextInputAt(p, target.Text, target.Color, target.FontSize <= 0 ? _options.FontSize : target.FontSize);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_active || !_drawing) return;
        var p = e.GetPosition(_canvas);
        if (_options.Kind == AnnotationKind.Freehand && _draftFreehandPoints is not null)
        {
            _draftFreehandPoints.Add(p);
            AppendFreehandDraftPoint(p);
            return;
        }

        // Shift 按住时对矩形 / 椭圆做"强制正方形 / 正圆"约束。约束在 draft 阶段就生效，
        // 让用户实时看到调整后的形状，与 Photoshop / Snipaste 习惯一致
        p = ApplyShiftConstraint(_draftStart, p, _options.Kind);

        // 马赛克 draft 阶段不渲染真实像素化效果：BuildMosaicBitmap 走 CroppedBitmap + CopyPixels
        // + 双层循环求块平均，大区域拖动会按 MouseMove 频次（60~120 Hz）跑这条耗时路径，明显掉帧。
        // 拖动期间只画一个半透明灰色占位框给用户位置反馈，MouseUp 时 store.Add 触发 Repaint
        // 再走一次真实的 BuildMosaicBitmap —— 只算一次，开销可接受
        if (_options.Kind == AnnotationKind.Mosaic)
        {
            ReplaceDraft(BuildMosaicDraftPlaceholder(_draftStart, p));
            return;
        }

        ReplaceDraft(BuildDraftFromBounds(_draftStart, p));
    }

    /// <summary>当按住 Shift 时把 end 折算为"以 start 为锚 + 边长 = min(|dx|, |dy|) 的正方形对角点"，
    /// 让矩形 / 椭圆强制成正方形 / 正圆。仅对 Rectangle / Ellipse 生效，其它工具保留自由拖动。
    ///
    /// 实现要点：保留 dx / dy 的符号 —— 用户从右下往左上拖时也能得到反方向的正方形；
    /// 边长取 min(|dx|, |dy|) 而不是 max，让"用户实际拖出的区域不被强行放大"，
    /// 与 Photoshop / Figma 的 Shift-Lock 行为一致</summary>
    private static Point ApplyShiftConstraint(Point start, Point end, AnnotationKind kind)
    {
        if (kind is not AnnotationKind.Rectangle and not AnnotationKind.Ellipse) return end;
        if (!Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift)) return end;
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var size = Math.Min(Math.Abs(dx), Math.Abs(dy));
        var sx = dx >= 0 ? 1 : -1;
        var sy = dy >= 0 ? 1 : -1;
        return new Point(start.X + sx * size, start.Y + sy * size);
    }

    /// <summary>给 Mosaic 拖拽时画一个半透明灰底 + 虚线边框的占位矩形。
    /// 视觉上提示"这里会变成马赛克"，与最终落地的像素块效果完全不同，避免用户误以为这就是结果。
    /// 选用虚线 + 中性灰色，与已落地的实色 / 高对比标注分隔，让"未确定"的状态一目了然</summary>
    private FrameworkElement BuildMosaicDraftPlaceholder(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X);
        double y = Math.Min(a.Y, b.Y);
        double w = Math.Abs(a.X - b.X);
        double h = Math.Abs(a.Y - b.Y);
        var rect = new System.Windows.Shapes.Rectangle
        {
            Width = w,
            Height = h,
            Fill = new SolidColorBrush(Color.FromArgb(0x66, 0x88, 0x88, 0x88)),
            Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0x44, 0x44, 0x44)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        return rect;
    }

    /// <summary>把 draft Polyline 的 PointCollection 追加一个点。
    /// 首次调用（_draftFreehandPolyline 为 null）创建 Polyline 并加入 Canvas，
    /// 后续调用只追加点到现有 PointCollection —— WPF 内部会重新计算 path geometry，
    /// 但不需要重建 PointCollection 或重新遍历历史点</summary>
    private void AppendFreehandDraftPoint(Point p)
    {
        if (_draftFreehandPolyline is null)
        {
            var line = new System.Windows.Shapes.Polyline
            {
                Stroke = new SolidColorBrush(_options.Color),
                StrokeThickness = _options.StrokeThickness,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false,
            };
            line.Points = new PointCollection();
            foreach (var pt in _draftFreehandPoints!) line.Points.Add(pt);
            _draftFreehandPolyline = line;
            ReplaceDraft(line);
            return;
        }
        _draftFreehandPolyline.Points.Add(p);
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
            _draftFreehandPolyline = null;
        }
        else
        {
            // MouseUp 时按当前 Shift 状态再约束一次：与 Photoshop 行为一致 ——
            // 抬起前若仍按住 Shift 就锁定正方形，否则放手即解锁，落地形状跟随用户最终意图
            p = ApplyShiftConstraint(_draftStart, p, _options.Kind);
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
        _draftFreehandPolyline = null;
        if (_canvas.IsMouseCaptured) _canvas.ReleaseMouseCapture();
    }

    // ============= 文本工具浮层 =============

    /// <summary>初始空文本框；颜色 / 字号取自当前 ToolOptions</summary>
    private void ShowTextInputAt(Point p)
        => ShowTextInputAt(p, initialText: "", color: _options.Color, fontSize: _options.FontSize);

    /// <summary>显示文字浮层 TextBox。initialText 不为空时会预填，用于"二次编辑已有文字"场景。
    ///
    /// 视觉规则：浮层与最终落地文字保持一致 —— 透明背景、不带边框、字号 / 颜色继承当前选项；
    /// 仅靠系统插入符（Caret）让用户感知输入位置。这样用户提交后视觉上"原地保留为不带框的纯文字"，
    /// 与之前的"黑底浮层 → 黑底标注"差异完全消除。
    ///
    /// 字体策略：显式绑定到 Application.Resources["ToolbarFontFamily"]（即设置窗里的"浮窗字体"）
    /// 而不是依赖 Window 继承 —— 这样保证浮层 TextBox 与最终 TextBlock 用同一个字体族，
    /// 不会因为某层覆写 FontFamily 出现"输入态与提交后视觉跳变"。
    /// FontWeight 走 Bold：标注语义下需要在原图上"压得住"，SemiBold 实测在 1080p 下偏纤细</summary>
    private void ShowTextInputAt(Point p, string initialText, Color color, double fontSize)
    {
        _textInput = new TextBox
        {
            Background = System.Windows.Media.Brushes.Transparent,
            Foreground = new SolidColorBrush(color),
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontFamily = ResolveAnnotationFontFamily(),
            FontSize = fontSize <= 0 ? _options.FontSize : fontSize,
            FontWeight = FontWeights.Regular,
            CaretBrush = new SolidColorBrush(color),
            MinWidth = 40,
            Padding = new Thickness(0),
            AcceptsReturn = false,
            Text = initialText ?? "",
        };
        Canvas.SetLeft(_textInput, p.X);
        Canvas.SetTop(_textInput, p.Y);
        _textInput.KeyDown += OnTextInputKeyDown;
        _textInput.LostKeyboardFocus += OnTextInputLostFocus;
        _canvas.Children.Add(_textInput);
        _textInput.Focus();
        Keyboard.Focus(_textInput);
        if (!string.IsNullOrEmpty(initialText))
        {
            // 二次编辑：把光标放到末尾，方便继续输入；不全选避免误删
            _textInput.CaretIndex = initialText.Length;
        }
    }

    /// <summary>从 Application.Resources 拿"浮窗字体"作为标注文字的字体族；
    /// 任何主题切换后只要 ThemeManager 把 ToolbarFontFamily 重写过，下一次新建文本框就会用新字体。
    /// 取不到（应用未初始化主题）时退回到内置中文 UI 字体回退链</summary>
    private static FontFamily ResolveAnnotationFontFamily()
    {
        var app = System.Windows.Application.Current;
        if (app is not null)
        {
            try
            {
                if (app.Resources["ToolbarFontFamily"] is FontFamily ff) return ff;
            }
            catch { }
        }
        return new FontFamily("Microsoft YaHei UI, Microsoft YaHei, 微软雅黑, PingFang SC, Segoe UI");
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
        // ActualWidth/Height 没准时（首次输入时可能为 0），fallback 到字符串估算 + FontSize 推导，
        // 让 HitTestExistingText 的命中区在二次编辑模式也能工作
        double w = tb.ActualWidth > 0 ? tb.ActualWidth : Math.Max(40, (text?.Length ?? 0) * tb.FontSize * 0.6);
        double h = tb.ActualHeight > 0 ? tb.ActualHeight : Math.Max(16, tb.FontSize * 1.4);
        var bounds = new Rect(double.IsFinite(x) ? x : 0, double.IsFinite(y) ? y : 0, w, h);
        var color = (tb.Foreground as SolidColorBrush)?.Color ?? _options.Color;
        var fontSize = tb.FontSize;
        _canvas.Children.Remove(tb);
        _textInput = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            // 空文本提交 = 删除：
            // 1) 二次编辑场景：旧条目已经从 store 移除，丢弃即等价于"清空文字"；
            // 2) 新建场景：本来就没东西，等价于不落地。
            _editingTarget = null;
            return;
        }

        var newAnn = Annotation.CreateText(bounds, text.Trim(), color, fontSize);
        if (_editingTarget is not null)
        {
            // 二次编辑：原条目已 Remove，这里直接 Add 让最新状态可 Undo
            _editingTarget = null;
        }
        _store.Add(newAnn);
    }

    private void DiscardTextInputIfAny()
    {
        if (_textInput is null) return;
        var tb = _textInput;
        tb.KeyDown -= OnTextInputKeyDown;
        tb.LostKeyboardFocus -= OnTextInputLostFocus;
        _canvas.Children.Remove(tb);
        _textInput = null;

        // 二次编辑模式 ESC：把原 annotation 还回去，让用户感觉是"取消修改 / 回到原状"
        if (_editingTarget is not null)
        {
            var restored = _editingTarget;
            _editingTarget = null;
            _store.Add(restored);
        }
    }
}
