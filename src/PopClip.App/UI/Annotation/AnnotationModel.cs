using System.Windows;
using System.Windows.Media;

namespace PopClip.App.UI.Annotation;

/// <summary>标注类型枚举。
/// v1 覆盖 90% 沟通场景；序号标记 / 模板等 v2 项不在此枚举内</summary>
internal enum AnnotationKind
{
    Rectangle,        // 空心矩形
    FilledRectangle,  // 实心矩形
    Ellipse,          // 空心椭圆
    FilledEllipse,    // 实心椭圆
    Line,             // 直线
    Arrow,            // 直线 + 三角箭头头
    Freehand,         // 自由涂鸦（折线）
    Text,             // 可拖动文本框
    Mosaic,           // 像素化遮罩
    Blur,             // 高斯模糊遮罩
    Mask,             // 实心遮挡（纯色填充）
}

/// <summary>不可变的标注实体。
/// 字段语义按 Kind 不同：
/// - Rectangle / FilledRectangle / Ellipse / FilledEllipse / Mosaic / Blur / Mask：
///   Bounds 表示矩形包围盒（DIP，相对 PreviewImage 左上）
/// - Line / Arrow：Points 含 2 个点（起点 / 终点）
/// - Freehand：Points 包含全部折线点（&gt;=2）
/// - Text：Bounds 表示文本框位置 + 估算宽高，Text 是显示内容，FontSize 决定字体大小
///
/// 颜色与粗细由调用方在创建时决定；store 仅持有，不再校验。
/// 设计为 immutable 是为了配合 Undo/Redo —— 历史栈直接持引用，避免共享可变状态。</summary>
internal sealed class Annotation
{
    public AnnotationKind Kind { get; }
    public Rect Bounds { get; }
    public IReadOnlyList<Point> Points { get; }
    public Color Color { get; }
    public double StrokeThickness { get; }
    public string Text { get; }
    public double FontSize { get; }

    /// <summary>遮罩档位：
    /// - Mosaic: 8 / 12 / 16（像素块大小）
    /// - Blur: 8 / 16 / 32（BlurEffect Radius）
    /// - 其他类型忽略</summary>
    public int MaskLevel { get; }

    private Annotation(AnnotationKind kind, Rect bounds, IReadOnlyList<Point> points,
        Color color, double strokeThickness, string text, double fontSize, int maskLevel)
    {
        Kind = kind;
        Bounds = bounds;
        Points = points;
        Color = color;
        StrokeThickness = strokeThickness;
        Text = text;
        FontSize = fontSize;
        MaskLevel = maskLevel;
    }

    public static Annotation CreateRectangle(Rect bounds, Color color, double thickness, bool filled)
        => new(filled ? AnnotationKind.FilledRectangle : AnnotationKind.Rectangle,
            bounds, Array.Empty<Point>(), color, thickness, "", 0, 0);

    public static Annotation CreateEllipse(Rect bounds, Color color, double thickness, bool filled)
        => new(filled ? AnnotationKind.FilledEllipse : AnnotationKind.Ellipse,
            bounds, Array.Empty<Point>(), color, thickness, "", 0, 0);

    public static Annotation CreateLine(Point a, Point b, Color color, double thickness)
        => new(AnnotationKind.Line, BoundsOf(a, b), new[] { a, b }, color, thickness, "", 0, 0);

    public static Annotation CreateArrow(Point a, Point b, Color color, double thickness)
        => new(AnnotationKind.Arrow, BoundsOf(a, b), new[] { a, b }, color, thickness, "", 0, 0);

    public static Annotation CreateFreehand(IReadOnlyList<Point> points, Color color, double thickness)
        => new(AnnotationKind.Freehand, BoundsOf(points), points, color, thickness, "", 0, 0);

    public static Annotation CreateText(Rect bounds, string text, Color color, double fontSize)
        => new(AnnotationKind.Text, bounds, Array.Empty<Point>(), color, 0, text ?? "", fontSize, 0);

    public static Annotation CreateMosaic(Rect bounds, int blockSize)
        => new(AnnotationKind.Mosaic, bounds, Array.Empty<Point>(), Colors.Transparent, 0, "", 0, blockSize);

    public static Annotation CreateBlur(Rect bounds, int radius)
        => new(AnnotationKind.Blur, bounds, Array.Empty<Point>(), Colors.Transparent, 0, "", 0, radius);

    public static Annotation CreateMask(Rect bounds, Color color)
        => new(AnnotationKind.Mask, bounds, Array.Empty<Point>(), color, 0, "", 0, 0);

    private static Rect BoundsOf(Point a, Point b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private static Rect BoundsOf(IReadOnlyList<Point> points)
    {
        if (points is null || points.Count == 0) return Rect.Empty;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var p in points)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }
}

/// <summary>标注集合 + Undo/Redo 操作栈。
/// 集合本身用 List 顺序存储；对外只读，外部通过 Add / Undo / Redo / Clear 改集合。
/// 改集合时触发 Changed 事件，让 ScreenshotPreviewWindow 重绘 AnnotationCanvas</summary>
internal sealed class AnnotationStore
{
    private readonly List<Annotation> _items = new();
    private readonly Stack<Annotation> _redoStack = new();

    public IReadOnlyList<Annotation> Items => _items;

    public bool CanUndo => _items.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public event Action? Changed;

    public void Add(Annotation a)
    {
        if (a is null) return;
        _items.Add(a);
        // 任何新操作都清空 redo 栈：与编辑器通用习惯一致
        _redoStack.Clear();
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (_items.Count == 0) return;
        var last = _items[^1];
        _items.RemoveAt(_items.Count - 1);
        _redoStack.Push(last);
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        _items.Add(_redoStack.Pop());
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_items.Count == 0 && _redoStack.Count == 0) return;
        _items.Clear();
        _redoStack.Clear();
        Changed?.Invoke();
    }
}

/// <summary>当前标注工具状态：工具类型 + 颜色 + 粗细 + 文字字号。
/// 由工具栏 / 主窗共享读写；选项变更时不立刻产生新 Annotation —— 只有用户在画布上拖拽时
/// 才用当前 ToolOptions 创建新 Annotation。
/// 双向绑定：UI 改 ToolOptions，ToolOptions Changed 事件让 UI 同步勾选样式</summary>
internal sealed class AnnotationToolOptions
{
    private AnnotationKind _kind = AnnotationKind.Rectangle;
    private Color _color = Colors.Red;
    private double _strokeThickness = 2.0;
    private double _fontSize = 16.0;
    private int _mosaicBlockSize = 12;
    private int _blurRadius = 16;

    public event Action? Changed;

    public AnnotationKind Kind
    {
        get => _kind;
        set { if (_kind != value) { _kind = value; Changed?.Invoke(); } }
    }
    public Color Color
    {
        get => _color;
        set { if (_color != value) { _color = value; Changed?.Invoke(); } }
    }
    public double StrokeThickness
    {
        get => _strokeThickness;
        set { if (Math.Abs(_strokeThickness - value) > 0.01) { _strokeThickness = value; Changed?.Invoke(); } }
    }
    public double FontSize
    {
        get => _fontSize;
        set { if (Math.Abs(_fontSize - value) > 0.01) { _fontSize = value; Changed?.Invoke(); } }
    }
    public int MosaicBlockSize
    {
        get => _mosaicBlockSize;
        set { if (_mosaicBlockSize != value) { _mosaicBlockSize = Math.Max(2, value); Changed?.Invoke(); } }
    }
    public int BlurRadius
    {
        get => _blurRadius;
        set { if (_blurRadius != value) { _blurRadius = Math.Max(1, value); Changed?.Invoke(); } }
    }

    /// <summary>实心 / 空心模式：仅对矩形 / 椭圆有效，由工具栏单独切换。
    /// 与 Kind 共同决定最终落到 AnnotationKind 的 Filled 变体</summary>
    public bool Filled { get; set; }
}
