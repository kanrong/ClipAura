using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace PopClip.App.UI.Annotation;

/// <summary>把 Annotation 渲染成 WPF FrameworkElement。
/// 使用纯 WPF 原生形状 / 文字 / Path —— 不引入额外图像库。
///
/// 马赛克对源像素的依赖：
/// - Mosaic：以源 BitmapSource 的对应区域为输入，预先生成"块大小=blockSize"的低分辨率位图，
///   再以 NearestNeighbor 拉回原大小作为 ImageBrush 填充矩形。
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
        // 头部尺寸：明显尖锐的等腰三角形，底边 / 高 ≈ 0.80。
        // 旧实现的 headHalfW = 3 + t*2.5 让底边宽度接近等边三角形，箭头偏胖、
        // 在小图标上看更像"楔形涂鸦"而非"指示性箭头"；这里把 headHalfW 系数从 2.5 → 1.6
        // 让等腰三角形底边收窄，箭头方向感更强，与 Snipaste / Snip & Sketch 同款比例
        var headLen = 5 + a.StrokeThickness * 4;
        var headHalfW = 2 + a.StrokeThickness * 1.6;
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
        // 完成态文字只显示纯文字，不再叠黑色 / 任意背景色：
        // 截图标注语义里"文字"应像写在原图上的笔迹，背景会盖掉用户截图原内容、显得突兀。
        // 可读性问题在标注阶段交由用户改文字颜色 / 字号自行解决；如需类似"高亮便签"效果，
        // 可叠加一个矩形标注。
        //
        // 字体策略：与 ShowTextInputAt 浮层完全一致 —— 都取 Application.Resources["ToolbarFontFamily"]
        // （设置窗的"浮窗字体"）。这样用户在标注期间换字体设置，已落地的文字下一次 Repaint 也会跟着换，
        // 与"主题切换文字字体"路径同语义。FontWeight=Bold：截图上的标记字常常压在彩色背景上，
        // Bold 比 SemiBold 更"压得住"，与浮层输入态保持一致
        var tb = new TextBlock
        {
            Text = a.Text,
            Foreground = new SolidColorBrush(a.Color),
            FontFamily = ResolveAnnotationFontFamily(),
            FontSize = a.FontSize <= 0 ? 16 : a.FontSize,
            FontWeight = FontWeights.Regular,
            // 命中测试需放开：v2 文字工具支持"再次点击文本范围进入编辑"，
            // 该入口由 AnnotationCanvasController 用 VisualTreeHelper.HitTest 找到 TextBlock
            IsHitTestVisible = true,
        };
        Canvas.SetLeft(tb, a.Bounds.X);
        Canvas.SetTop(tb, a.Bounds.Y);
        return tb;
    }

    /// <summary>读取应用级 ToolbarFontFamily 资源作为标注字体；取不到时退回到中文 UI 字体回退链。
    /// 与 AnnotationCanvasController.ResolveAnnotationFontFamily 完全等价 ——
    /// 这里再写一份是因为 Renderer 是 static 工具类，不持有 controller 引用</summary>
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

    /// <summary>马赛克：把源图对应区域降采样为"块尺寸=blockSize"的低分辨率位图，再以 NearestNeighbor 拉回原大小。
    ///
    /// 算法演进：
    ///  v0：TransformedBitmap + ScaleTransform 缩小 → WPF 自带 Fant 滤波先平均再插值，效果像高斯模糊；
    ///  v1：CopyPixels + 块中心点单像素采样 + ImageBrush 拉回 → 色块颜色取决于块中心那 1 个像素，
    ///       文字 / 边界附近的色块容易闪烁噪杂；并且 RenderOptions.BitmapScalingMode 设在 Brush 上
    ///       生效不可靠，部分场景仍走线性插值，色块边界被柔化；
    ///  v2（当前）：每块取所有源像素的算术平均色 + Image 控件承载 + 强保证 NearestNeighbor / EdgeMode.Aliased，
    ///       与 FastStone Capture / Snipaste 的"经典像素化"语义一致 —— 色块过渡稳定、边界锋利。
    /// 该路径不修改源 BitmapSource，复制 / 保存时由 RenderTargetBitmap 合成最终像素</summary>
    private static FrameworkElement CreateMosaic(Annotation a, BitmapSource? source, double imageWidthDip, double imageHeightDip)
    {
        var bounds = a.Bounds;
        var shrunk = BuildMosaicBitmap(a, source, imageWidthDip, imageHeightDip);
        if (shrunk is null)
        {
            // 源缺失 / 尺寸非法：退到灰色填充，至少让用户能看到马赛克区域被标记
            var fallback = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(0, bounds.Width),
                Height = Math.Max(0, bounds.Height),
                Fill = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(fallback, bounds.X);
            Canvas.SetTop(fallback, bounds.Y);
            return fallback;
        }

        // Image 控件承载缩小位图：比 Rectangle + ImageBrush 更可靠 —— BitmapScalingMode 是 attached property，
        // 在 Image / Visual 上是强保证；EdgeMode.Aliased 关掉边缘反锯齿让色块边界锋利
        var img = new System.Windows.Controls.Image
        {
            Source = shrunk,
            Stretch = Stretch.Fill,
            Width = Math.Max(0, bounds.Width),
            Height = Math.Max(0, bounds.Height),
            IsHitTestVisible = false,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false,
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(img, EdgeMode.Aliased);
        Canvas.SetLeft(img, bounds.X);
        Canvas.SetTop(img, bounds.Y);
        return img;
    }

    /// <summary>把源图 a.Bounds 区域降采样成 smallW × smallH 的位图，每像素=对应块所有源像素颜色的算术平均。
    /// 返回 null 表示源图缺失 / 区域非法，调用方走兜底渲染。
    ///
    /// 关键实现细节：
    ///  - 块尺寸用"上取整"算 smallW/smallH，让右 / 下边不足 blockSize 的残块也能保留一行，
    ///    否则 pxW=10, blockSize=12 时 smallW=0，整条边像素被吞；
    ///  - 像素格式统一成 Bgra32，避免 16bpp / Indexed 等再走 FormatConvertedBitmap 路径；
    ///  - 颜色累加用 long 而不是 int，防止大块 (blockSize≥256) 时 sumChannel 溢出。</summary>
    private static BitmapSource? BuildMosaicBitmap(Annotation a, BitmapSource? source, double imageWidthDip, double imageHeightDip)
    {
        if (source is null || a.Bounds.Width < 1 || a.Bounds.Height < 1) return null;

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

            var blockSize = Math.Max(2, a.MaskLevel == 0 ? 12 : a.MaskLevel);
            var smallW = Math.Max(1, (pxW + blockSize - 1) / blockSize);
            var smallH = Math.Max(1, (pxH + blockSize - 1) / blockSize);

            var cropped = new CroppedBitmap(source, new Int32Rect(pxX, pxY, pxW, pxH));
            BitmapSource bgra = cropped.Format == PixelFormats.Bgra32
                ? cropped
                : new FormatConvertedBitmap(cropped, PixelFormats.Bgra32, null, 0);

            int srcStride = pxW * 4;
            var srcPixels = new byte[srcStride * pxH];
            bgra.CopyPixels(srcPixels, srcStride, 0);

            int dstStride = smallW * 4;
            var dstPixels = new byte[dstStride * smallH];

            for (int sy = 0; sy < smallH; sy++)
            {
                int y0 = sy * blockSize;
                int y1 = Math.Min(pxH, y0 + blockSize);
                for (int sx = 0; sx < smallW; sx++)
                {
                    int x0 = sx * blockSize;
                    int x1 = Math.Min(pxW, x0 + blockSize);

                    long sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                    int count = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        int srcRow = y * srcStride;
                        for (int x = x0; x < x1; x++)
                        {
                            int off = srcRow + x * 4;
                            sumB += srcPixels[off];
                            sumG += srcPixels[off + 1];
                            sumR += srcPixels[off + 2];
                            sumA += srcPixels[off + 3];
                            count++;
                        }
                    }

                    int dstOff = sy * dstStride + sx * 4;
                    if (count > 0)
                    {
                        dstPixels[dstOff]     = (byte)(sumB / count);
                        dstPixels[dstOff + 1] = (byte)(sumG / count);
                        dstPixels[dstOff + 2] = (byte)(sumR / count);
                        dstPixels[dstOff + 3] = (byte)(sumA / count);
                    }
                }
            }

            var shrunk = BitmapSource.Create(smallW, smallH, 96, 96, PixelFormats.Bgra32, null, dstPixels, dstStride);
            shrunk.Freeze();
            return shrunk;
        }
        catch
        {
            return null;
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
