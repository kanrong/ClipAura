using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace PopClip.App.UI.Annotation;

/// <summary>把 Annotation 渲染成 WPF FrameworkElement。
/// 使用纯 WPF 原生形状 / 文字 / Path —— 不引入额外图像库。
///
/// 马赛克 / 高斯模糊对源像素的依赖：
/// - Mosaic：以源 BitmapSource 的对应区域为输入，预先生成"块大小=blockSize"的低分辨率位图，
///   再以 NearestNeighbor 拉回原大小作为 ImageBrush 填充矩形。
/// - Blur：用 CroppedBitmap 截取源像素的对应区域，作为 Image 控件 + BlurEffect 显示。
///
/// 设计动机：v1 方案不在像素层面回写源图，所有标注层都浮在 PreviewImage 之上，
/// 复制 / 保存时再用 RenderTargetBitmap 把 PreviewImage + AnnotationCanvas 合成出最终 PNG。
/// 这样 Undo/Redo 能精确撤销马赛克回到原图，无需保存"原图副本 + diff"两份数据</summary>
internal static class AnnotationRenderer
{
    /// <summary>把单个 Annotation 渲染为 FrameworkElement，定位到 Canvas 上。
    /// 返回的元素已经设好 Canvas.Left / Canvas.Top，调用方只需 Children.Add。
    /// sourceImage 是 PreviewImage.Source 的引用 —— 用于马赛克 / 模糊采样源像素</summary>
    public static FrameworkElement Render(Annotation a, BitmapSource? sourceImage, double imageWidthDip, double imageHeightDip)
    {
        return a.Kind switch
        {
            AnnotationKind.Rectangle => CreateRect(a, filled: false),
            AnnotationKind.FilledRectangle => CreateRect(a, filled: true),
            AnnotationKind.Ellipse => CreateEllipse(a, filled: false),
            AnnotationKind.FilledEllipse => CreateEllipse(a, filled: true),
            AnnotationKind.Line => CreateLine(a),
            AnnotationKind.Arrow => CreateArrow(a),
            AnnotationKind.Freehand => CreateFreehand(a),
            AnnotationKind.Text => CreateText(a),
            AnnotationKind.Mosaic => CreateMosaic(a, sourceImage, imageWidthDip, imageHeightDip),
            AnnotationKind.Blur => CreateBlur(a, sourceImage, imageWidthDip, imageHeightDip),
            AnnotationKind.Mask => CreateMask(a),
            _ => new System.Windows.Controls.Border(),
        };
    }

    private static FrameworkElement CreateRect(Annotation a, bool filled)
    {
        var r = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(0, a.Bounds.Width),
            Height = Math.Max(0, a.Bounds.Height),
            Stroke = new SolidColorBrush(a.Color),
            StrokeThickness = a.StrokeThickness,
            Fill = filled ? new SolidColorBrush(a.Color) : System.Windows.Media.Brushes.Transparent,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(r, a.Bounds.X);
        Canvas.SetTop(r, a.Bounds.Y);
        return r;
    }

    private static FrameworkElement CreateEllipse(Annotation a, bool filled)
    {
        var e = new System.Windows.Shapes.Ellipse
        {
            Width = Math.Max(0, a.Bounds.Width),
            Height = Math.Max(0, a.Bounds.Height),
            Stroke = new SolidColorBrush(a.Color),
            StrokeThickness = a.StrokeThickness,
            Fill = filled ? new SolidColorBrush(a.Color) : System.Windows.Media.Brushes.Transparent,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(e, a.Bounds.X);
        Canvas.SetTop(e, a.Bounds.Y);
        return e;
    }

    private static FrameworkElement CreateLine(Annotation a)
    {
        var p = a.Points;
        var line = new System.Windows.Shapes.Line
        {
            X1 = p[0].X,
            Y1 = p[0].Y,
            X2 = p[1].X,
            Y2 = p[1].Y,
            Stroke = new SolidColorBrush(a.Color),
            StrokeThickness = a.StrokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };
        return line;
    }

    private static FrameworkElement CreateArrow(Annotation a)
    {
        // 箭头 = 主线 + 三角箭头头。用 Path 组合 Line + Polygon。
        // 头部三角形大小随 StrokeThickness 缩放，保证不同粗细下视觉协调
        var p = a.Points;
        var start = p[0];
        var end = p[1];
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.5)
        {
            // 退化为点，避免除零；返回一个空容器
            return new System.Windows.Shapes.Line { X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y, Stroke = new SolidColorBrush(a.Color), StrokeThickness = a.StrokeThickness, IsHitTestVisible = false };
        }
        var ux = dx / len;
        var uy = dy / len;
        // 头部尺寸：粗细 1px → 头长 8 DIP；粗细 2px → 14；粗细 4px → 22
        var headLen = 5 + a.StrokeThickness * 4;
        var headHalfW = 3 + a.StrokeThickness * 2.5;
        var px = -uy;
        var py = ux;
        var headBaseX = end.X - ux * headLen;
        var headBaseY = end.Y - uy * headLen;
        var leftX = headBaseX + px * headHalfW;
        var leftY = headBaseY + py * headHalfW;
        var rightX = headBaseX - px * headHalfW;
        var rightY = headBaseY - py * headHalfW;

        var canvas = new Canvas { IsHitTestVisible = false };
        canvas.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = start.X, Y1 = start.Y,
            X2 = headBaseX, Y2 = headBaseY,
            Stroke = new SolidColorBrush(a.Color),
            StrokeThickness = a.StrokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });
        var head = new Polygon
        {
            Points = new PointCollection(new[]
            {
                new Point(end.X, end.Y),
                new Point(leftX, leftY),
                new Point(rightX, rightY),
            }),
            Fill = new SolidColorBrush(a.Color),
            Stroke = new SolidColorBrush(a.Color),
            StrokeThickness = a.StrokeThickness * 0.5,
            StrokeLineJoin = PenLineJoin.Round,
        };
        canvas.Children.Add(head);
        return canvas;
    }

    private static FrameworkElement CreateFreehand(Annotation a)
    {
        // 用 Polyline 渲染折线；StrokeLineJoin=Round 让拐角圆滑
        var line = new Polyline
        {
            Stroke = new SolidColorBrush(a.Color),
            StrokeThickness = a.StrokeThickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };
        var coll = new PointCollection();
        foreach (var p in a.Points) coll.Add(p);
        line.Points = coll;
        return line;
    }

    private static FrameworkElement CreateText(Annotation a)
    {
        var border = new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            IsHitTestVisible = false,
        };
        var tb = new TextBlock
        {
            Text = a.Text,
            Foreground = new SolidColorBrush(a.Color),
            FontSize = a.FontSize <= 0 ? 16 : a.FontSize,
            FontWeight = FontWeights.SemiBold,
        };
        border.Child = tb;
        Canvas.SetLeft(border, a.Bounds.X);
        Canvas.SetTop(border, a.Bounds.Y);
        return border;
    }

    /// <summary>马赛克：把源图对应区域用 8x8 / 12x12 / 16x16 像素块降采样后再以 NearestNeighbor 拉回原大小。
    /// 实现细节：CroppedBitmap 截取源像素 → TransformedBitmap 缩小 → ImageBrush 填充矩形。
    /// 该路径不修改源 BitmapSource，复制 / 保存时由 RenderTargetBitmap 合成最终像素</summary>
    private static FrameworkElement CreateMosaic(Annotation a, BitmapSource? source, double imageWidthDip, double imageHeightDip)
    {
        var rect = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(0, a.Bounds.Width),
            Height = Math.Max(0, a.Bounds.Height),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(rect, a.Bounds.X);
        Canvas.SetTop(rect, a.Bounds.Y);

        var brush = BuildMosaicBrush(a, source, imageWidthDip, imageHeightDip);
        rect.Fill = brush;
        return rect;
    }

    private static System.Windows.Media.Brush BuildMosaicBrush(Annotation a, BitmapSource? source, double imageWidthDip, double imageHeightDip)
    {
        // 兜底：源图缺失或区域非法时退到灰色填充，保持视觉一致避免穿帮
        if (source is null || a.Bounds.Width < 1 || a.Bounds.Height < 1)
        {
            return new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        }

        try
        {
            // DIP → 源像素：sourceImage 在屏幕上的 DIP 尺寸 = imageWidthDip / Height；
            // 像素 = DIP * (PixelWidth / DIP)
            var pxX = (int)Math.Round(a.Bounds.X * source.PixelWidth / Math.Max(1, imageWidthDip));
            var pxY = (int)Math.Round(a.Bounds.Y * source.PixelHeight / Math.Max(1, imageHeightDip));
            var pxW = (int)Math.Round(a.Bounds.Width * source.PixelWidth / Math.Max(1, imageWidthDip));
            var pxH = (int)Math.Round(a.Bounds.Height * source.PixelHeight / Math.Max(1, imageHeightDip));
            pxX = Math.Clamp(pxX, 0, source.PixelWidth - 1);
            pxY = Math.Clamp(pxY, 0, source.PixelHeight - 1);
            pxW = Math.Clamp(pxW, 1, source.PixelWidth - pxX);
            pxH = Math.Clamp(pxH, 1, source.PixelHeight - pxY);

            var blockSize = Math.Max(2, a.MaskLevel == 0 ? 12 : a.MaskLevel);
            // 缩小到 width/blockSize × height/blockSize；至少 1×1
            var smallW = Math.Max(1, pxW / blockSize);
            var smallH = Math.Max(1, pxH / blockSize);

            var cropped = new CroppedBitmap(source, new Int32Rect(pxX, pxY, pxW, pxH));
            var scale = new ScaleTransform((double)smallW / pxW, (double)smallH / pxH);
            var shrunk = new TransformedBitmap(cropped, scale);
            shrunk.Freeze();

            var brush = new ImageBrush(shrunk)
            {
                Stretch = Stretch.Fill,
                TileMode = TileMode.None,
            };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        }
    }

    /// <summary>高斯模糊：用 CroppedBitmap 截取源像素 → Image 控件 + BlurEffect 显示。
    /// 把 Image 装在 Border 内 + ClipToBounds=True，保证 BlurEffect 模糊像素仅在 Bounds 内可见</summary>
    private static FrameworkElement CreateBlur(Annotation a, BitmapSource? source, double imageWidthDip, double imageHeightDip)
    {
        if (source is null || a.Bounds.Width < 1 || a.Bounds.Height < 1)
        {
            return CreateMask(Annotation.CreateMask(a.Bounds, Color.FromArgb(0xC0, 0x80, 0x80, 0x80)));
        }
        try
        {
            var pxX = (int)Math.Round(a.Bounds.X * source.PixelWidth / Math.Max(1, imageWidthDip));
            var pxY = (int)Math.Round(a.Bounds.Y * source.PixelHeight / Math.Max(1, imageHeightDip));
            var pxW = (int)Math.Round(a.Bounds.Width * source.PixelWidth / Math.Max(1, imageWidthDip));
            var pxH = (int)Math.Round(a.Bounds.Height * source.PixelHeight / Math.Max(1, imageHeightDip));
            pxX = Math.Clamp(pxX, 0, source.PixelWidth - 1);
            pxY = Math.Clamp(pxY, 0, source.PixelHeight - 1);
            pxW = Math.Clamp(pxW, 1, source.PixelWidth - pxX);
            pxH = Math.Clamp(pxH, 1, source.PixelHeight - pxY);
            var cropped = new CroppedBitmap(source, new Int32Rect(pxX, pxY, pxW, pxH));
            cropped.Freeze();

            var img = new System.Windows.Controls.Image
            {
                Source = cropped,
                Stretch = Stretch.Fill,
                Width = a.Bounds.Width,
                Height = a.Bounds.Height,
                Effect = new BlurEffect { Radius = a.MaskLevel == 0 ? 16 : a.MaskLevel, KernelType = KernelType.Gaussian },
                IsHitTestVisible = false,
            };
            var border = new System.Windows.Controls.Border
            {
                Width = a.Bounds.Width,
                Height = a.Bounds.Height,
                ClipToBounds = true,
                Child = img,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(border, a.Bounds.X);
            Canvas.SetTop(border, a.Bounds.Y);
            return border;
        }
        catch
        {
            return CreateMask(Annotation.CreateMask(a.Bounds, Color.FromArgb(0xC0, 0x80, 0x80, 0x80)));
        }
    }

    private static FrameworkElement CreateMask(Annotation a)
    {
        var rect = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(0, a.Bounds.Width),
            Height = Math.Max(0, a.Bounds.Height),
            Fill = new SolidColorBrush(a.Color),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(rect, a.Bounds.X);
        Canvas.SetTop(rect, a.Bounds.Y);
        return rect;
    }
}
