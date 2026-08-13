using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using PopClip.App.Config;
using PopClip.App.Ocr;
using PopClip.App.Services;
using PopClip.App.UI;
using PopClip.Core.Logging;
using PopClip.Core.Model;
using PopClip.Hooks;
using PopClip.Hooks.Interop;
using PopClip.Hooks.Window;
using PopClip.Ocr.Layout;
using PopClip.Uia.Clipboard;
using WpfApplication = System.Windows.Application;

namespace PopClip.App.Hosting;

internal sealed partial class OcrCaptureCoordinator
{

    /// <summary>WinRectangle → SelectionRect 的简短转换。SelectionRect 用 (Left, Top, Right, Bottom)
    /// 而 WinRectangle 用 (X, Y, Width, Height)，几个换算点重复出现，抽个静态方法保持简洁</summary>
    private static SelectionRect ToSelectionRect(Rectangle rect)
        => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    /// <summary>把冻结的 GDI 位图做成 WPF 可显示、可跨线程 Freeze 的 BitmapSource。
    /// 选区窗用 Stretch=Fill 铺满 virtual screen，这里 DPI 固定 96 即可。</summary>
    private static BitmapSource CreateFrozenPreview(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var source = BitmapSource.Create(
                bitmap.Width,
                bitmap.Height,
                96, 96,
                System.Windows.Media.PixelFormats.Bgra32,
                null,
                data.Scan0,
                stride * bitmap.Height,
                stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static OcrImage CreateOcrImage(Bitmap bitmap, Func<byte[]>? pngFactory = null)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        using var normalized = bitmap.PixelFormat == PixelFormat.Format32bppArgb
            ? null
            : bitmap.Clone(rect, PixelFormat.Format32bppArgb);
        var source = normalized ?? bitmap;
        var data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            // 关键：把 width / height 抽到局部 int 变量再交给 lambda 捕获。
            // pngFactory 改成 Lazy<byte[]> 延迟执行后，闭包里若直接写 source.Width，
            // 实际属性访问会推迟到 GetPngBytes 第一次调用时才发生 —— 那时 bitmap / source 已经被
            // 外层 CaptureRegion 的 using 释放，访问被 Dispose 的 Bitmap 会抛
            // ArgumentException("Parameter is not valid.")，让"截图复制 / 预览 / OCR 写历史"全部炸掉
            int width = source.Width;
            int height = source.Height;
            var stride = Math.Abs(data.Stride);
            var pixels = new byte[stride * height];
            if (data.Stride > 0)
            {
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            }
            else
            {
                for (var y = 0; y < height; y++)
                {
                    var row = IntPtr.Add(data.Scan0, data.Stride * y);
                    Marshal.Copy(row, pixels, y * stride, stride);
                }
            }
            return new OcrImage(
                width,
                height,
                OcrImagePixelFormat.Bgra32,
                pixels,
                stride,
                pngFactory ?? (() => EncodePngFromBgra32(width, height, pixels, stride)));
        }
        finally
        {
            source.UnlockBits(data);
        }
    }

    private static byte[] EncodePngFromBgra32(int width, int height, byte[] pixels, int stride)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var destStride = Math.Abs(data.Stride);
            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
            {
                var dest = data.Stride > 0
                    ? IntPtr.Add(data.Scan0, y * data.Stride)
                    : IntPtr.Add(data.Scan0, (height - 1 - y) * destStride);
                Marshal.Copy(pixels, y * stride, dest, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static SelectionRect CreateClipboardImageDisplayRect(OcrImage image, SelectionRect fallbackAnchor)
    {
        MonitorMetrics monitor;
        if (NativeMethods.GetCursorPos(out var pt))
        {
            monitor = MonitorQuery.FromPoint(pt.X, pt.Y);
        }
        else
        {
            monitor = MonitorQuery.FromRect(
                fallbackAnchor.Left, fallbackAnchor.Top,
                fallbackAnchor.Right, fallbackAnchor.Bottom);
        }

        var workWidth = Math.Max(1, monitor.WorkWidth);
        var workHeight = Math.Max(1, monitor.WorkHeight);
        var marginX = Math.Max(24, (int)Math.Round(workWidth * 0.08));
        var marginY = Math.Max(24, (int)Math.Round(workHeight * 0.08));
        var maxWidth = Math.Min(workWidth, Math.Max(120, workWidth - marginX * 2));
        var maxHeight = Math.Min(workHeight, Math.Max(80, workHeight - marginY * 2));
        var scale = Math.Min(1.0, Math.Min(maxWidth / (double)image.Width, maxHeight / (double)image.Height));
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));

        var left = monitor.WorkLeft + (workWidth - width) / 2;
        var top = monitor.WorkTop + (workHeight - height) / 2;
        left = Math.Clamp(left, monitor.WorkLeft, monitor.WorkRight - width);
        top = Math.Clamp(top, monitor.WorkTop, monitor.WorkBottom - height);
        return new SelectionRect(left, top, left + width, top + height);
    }

    /// <summary>给 toast 用的内容预览：去掉换行、压缩空白、最多 36 字符 + 省略号。
    /// toast 单行展示，太长会被截断且失去可读性；36 字符在 13px 字号下大约 200px 宽，
    /// 与浮窗常见宽度相近</summary>
    private static string BuildPreview(string text)
    {
        var compact = string.Join(' ', text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        if (compact.Length <= 36) return compact;
        return compact[..36] + "…";
    }

    /// <summary>把"截图框 + toast 显示"打包到 UI 线程一次性完成。
    /// 必须在 UI 线程做：Application.MainWindow / FloatingToolbar 都是 DispatcherObject，
    /// 跨线程访问会抛 InvalidOperationException（RunCaptureAsync 跑在后台线程，必须显式切回 UI 线程）。
    ///
    /// DPI 走 MonitorQuery 取 anchor 截图所在 monitor 的真实 effective DPI，
    /// 而不是浮窗当前 PresentationSource 的 DPI —— 副屏框选 + 浮窗在主屏时两者不同，
    /// 后者会让 toast 按主屏 DIP 解读，副屏框选时 toast 飘到错位置</summary>
    private void ShowAnchoredToast(string text, SelectionRect anchorRect, bool isError, int durationMs)
    {
        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            var monitor = MonitorQuery.FromRect(
                anchorRect.Left, anchorRect.Top, anchorRect.Right, anchorRect.Bottom);
            double dpiScaleX = Math.Max(96, monitor.DpiX) / 96.0;
            double dpiScaleY = Math.Max(96, monitor.DpiY) / 96.0;
            double centerDip = (anchorRect.Left + anchorRect.Width / 2.0) / dpiScaleX;
            double topDip = (anchorRect.Bottom + 8) / dpiScaleY;
            _toolbar.ShowToastAt(text, centerDip, topDip, isError: isError, durationMs: durationMs);
        });
    }
}
