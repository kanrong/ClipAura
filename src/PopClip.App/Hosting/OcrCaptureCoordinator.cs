using System.Drawing;
using System.IO;
using System.Windows;
using PopClip.App.Services;
using PopClip.App.UI;
using PopClip.Core.Actions;
using PopClip.Core.Logging;
using PopClip.Core.Model;
using PopClip.Uia.Clipboard;
using WpfApplication = System.Windows.Application;

namespace PopClip.App.Hosting;

/// <summary>把"全局热键 → 选区窗 → 截屏 → OCR → 浮窗"串成一条独立链路。
/// 不进入选区状态机，是手动触发的第三采集路径。
///
/// 同一时刻只允许有一个截图会话：用户在窗口已经打开时再次按热键不会叠开第二个窗口</summary>
internal sealed class OcrCaptureCoordinator
{
    private readonly ILog _log;
    private readonly OcrService _ocr;
    private readonly SelectionSessionManager _session;
    private readonly ClipboardWriter _clipboard;
    private readonly ClipboardAccess _clipboardAccess;
    private readonly FloatingToolbar _toolbar;
    private readonly IInlineBubblePresenter? _bubble;
    private OcrSelectionWindow? _currentWindow;

    public OcrCaptureCoordinator(
        ILog log,
        OcrService ocr,
        SelectionSessionManager session,
        ClipboardWriter clipboard,
        ClipboardAccess clipboardAccess,
        FloatingToolbar toolbar,
        IInlineBubblePresenter? bubble = null)
    {
        _log = log;
        _ocr = ocr;
        _session = session;
        _clipboard = clipboard;
        _clipboardAccess = clipboardAccess;
        _toolbar = toolbar;
        _bubble = bubble;
    }

    public void Trigger()
    {
        if (!EnsureAvailableOrNotify()) return;

        // 提前预热引擎：用户从按热键到松开拖框通常 1~2 秒，ONNX session 冷启动 ~500 ms-1 秒，
        // 在蒙层弹出的同时后台加载模型可以把感知延迟压到接近 0
        _ocr.PrewarmInBackground();

        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            if (_currentWindow is not null)
            {
                // 已有截图会话在进行中：把它前置并 return，避免叠开多个蒙层
                try { _currentWindow.Activate(); } catch { }
                return;
            }

            var window = new OcrSelectionWindow(_log);
            _currentWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_currentWindow, window)) _currentWindow = null;
            };
            window.Cancelled += () => _log.Info("ocr capture cancelled");
            window.RegionSelected += physical => _ = RunCaptureAsync(physical);
            window.Show();
        });
    }

    public void TriggerClipboardImage(SelectionRect anchorRect)
    {
        if (!EnsureAvailableOrNotify()) return;
        _ocr.PrewarmInBackground();
        _ = Task.Run(() => RunClipboardImageAsync(anchorRect));
    }

    private async Task RunCaptureAsync(Rectangle physical)
    {
        if (physical.Width <= 0 || physical.Height <= 0) return;
        try
        {
            // 关键缓冲：选区窗 Hide/Close 后 DWM 需要 1~2 帧合成才会从屏幕移除蒙层（@60Hz 约 32 ms/帧），
            // 此时立刻 CopyFromScreen 会截到半透明黑色蒙层覆盖的内容，导致 OCR 输出乱码。
            // 80 ms ≈ 5 帧，足够覆盖普通显示刷新率甚至 30Hz 远程会话
            await Task.Delay(80).ConfigureAwait(false);

            using var bitmap = new Bitmap(physical.Width, physical.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(physical.Left, physical.Top, 0, 0, bitmap.Size);
            }

            var anchorRect = new SelectionRect(physical.Left, physical.Top, physical.Right, physical.Bottom);
            await RecognizeBitmapAsync(bitmap, anchorRect, $"{physical.Width}x{physical.Height}").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.Warn("ocr capture timed out");
            WpfApplication.Current.Dispatcher.Invoke(() =>
                MessageBox.Show("OCR 超时，请缩小截图区域后再试。", "OCR 超时", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
        catch (Exception ex)
        {
            _log.Error("ocr capture failed", ex);
            WpfApplication.Current.Dispatcher.Invoke(() =>
                MessageBox.Show("OCR 失败：" + ex.Message, "OCR 错误", MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }

    private async Task RunClipboardImageAsync(SelectionRect anchorRect)
    {
        try
        {
            var pngBytes = _clipboardAccess.GetImagePngBytes();
            if (pngBytes is null || pngBytes.Length == 0)
            {
                ShowAnchoredToast("剪贴板中没有图片", anchorRect, isError: true, durationMs: 2500);
                return;
            }

            using var ms = new MemoryStream(pngBytes);
            using var decoded = new Bitmap(ms);
            using var bitmap = new Bitmap(decoded);
            await RecognizeBitmapAsync(bitmap, anchorRect, "clipboard-image").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.Warn("ocr clipboard image timed out");
            WpfApplication.Current.Dispatcher.Invoke(() =>
                MessageBox.Show("OCR 超时，请裁小剪贴板图片后再试。", "OCR 超时", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
        catch (Exception ex)
        {
            _log.Error("ocr clipboard image failed", ex);
            WpfApplication.Current.Dispatcher.Invoke(() =>
                MessageBox.Show("OCR 失败：" + ex.Message, "OCR 错误", MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }

    private async Task RecognizeBitmapAsync(Bitmap bitmap, SelectionRect anchorRect, string source)
    {
        // 引擎未就绪时识别可能耗时 1~3 秒（含 ONNX 三个 session 冷启动），先给个轻量 toast
        // 让用户感知到正在工作；已就绪时跳过 toast 避免噪音。
        // 用 ShowToastAt 而不是 ShowInlineToast：识别失败 / 加载中场景浮窗本身不会显示，
        // ShowInlineToast 拿浮窗的 Left/Top 锚定会落到屏幕外，用户看不到
        if (!_ocr.IsEngineReady)
        {
            ShowAnchoredToast("OCR 识别中…", anchorRect, isError: false, durationMs: 1500);
        }

        // 超时给 30 秒：冷启动 + 大图识别极端情况下也够用，避免误杀正常请求
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var text = await _ocr.RecognizeAsync(bitmap, cts.Token).ConfigureAwait(false);
        _log.Info("ocr recognized", ("len", text.Length), ("source", source));

        var trimmed = text.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            // 浮窗这条路径不会显示，必须用 ShowToastAt 直接给屏幕坐标，
            // 否则 ShowInlineToast 会以浮窗左上角（默认 0,0 或上一次位置）为锚 → toast 飘到屏幕外看不见
            ShowAnchoredToast("OCR 未识别到文本", anchorRect, isError: true, durationMs: 3500);
            return;
        }

        // 主动写入剪贴板：浮窗 timeout 关掉之后，识别结果仍可 Ctrl+V 兜底
        _clipboard.SetText(trimmed);

        _session.ShowToolbarForExternalText(trimmed, anchorRect, AcquisitionSource.Ocr);

        // OCR 默认走 CopyAndBubble：剪贴板（上面）+ 气泡（下面，含完整识别文本可滚动）。
        // 气泡比单行 toast 优势：
        // 1) 支持多行，长文本 OCR 不会被截断；
        // 2) 用户能即时看到识别质量并手动复制 / 替换；
        // 3) 浮窗 timeout 关闭后气泡仍然存在，给用户充足时间处理结果
        if (_bubble is not null)
        {
            _ = WpfApplication.Current.Dispatcher.BeginInvoke(new Action(() =>
                _bubble.ShowStatic($"OCR · {trimmed.Length} 字", trimmed, canReplace: false)));
        }
        else
        {
            // 兜底：没有 bubble 时还原老行为，发一个简短 toast
            var preview = BuildPreview(trimmed);
            _ = WpfApplication.Current.Dispatcher.BeginInvoke(new Action(() =>
                _toolbar.ShowInlineToast(
                    $"OCR 已识别 {trimmed.Length} 字 · 已复制：{preview}",
                    copyText: trimmed,
                    durationMs: 5000)));
        }
    }

    private bool EnsureAvailableOrNotify()
    {
        if (_ocr.IsAvailable) return true;

        // 模型与 native runtime (ONNX Runtime + SkiaSharp) 随程序一起分发，IsAvailable=false
        // 只可能是初始化阶段加载 native lib 失败（VC++ 运行库缺失 / 安全软件拦截 / 模型文件被误删）。
        // 提示用户检查日志而不是去配置语言包
        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            MessageBox.Show(
                "OCR 引擎初始化失败。\n\n请确认：\n"
                + "• 已安装 Visual C++ 2019/2022 Redistributable (x64)；\n"
                + "• 安全软件未拦截 onnxruntime / libSkiaSharp 相关 DLL；\n"
                + "• 程序目录下 models\\v5\\ 中的 4 个模型文件未被误删。\n\n"
                + "详细错误请查看应用日志。",
                "OCR 不可用",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        });
        return false;
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
    /// 必须在 UI 线程做：Application.MainWindow / PresentationSource / FloatingToolbar 全是 DispatcherObject，
    /// 跨线程访问会抛 InvalidOperationException（RunCaptureAsync 跑在后台线程，必须显式切回 UI 线程）。
    /// DPI 取自浮窗 (FloatingToolbar 自己就是 Window) —— 这一步必须 UI 线程内做，
    /// 多显异构 DPI 时副屏锚点可能略偏移几像素，但 toast 视觉宽容度足够，不影响可读性。</summary>
    private void ShowAnchoredToast(string text, SelectionRect anchorRect, bool isError, int durationMs)
    {
        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            double dpiX = 1.0, dpiY = 1.0;
            var src = PresentationSource.FromVisual(_toolbar);
            if (src?.CompositionTarget is not null)
            {
                dpiX = src.CompositionTarget.TransformToDevice.M11;
                dpiY = src.CompositionTarget.TransformToDevice.M22;
            }
            double centerDip = (anchorRect.Left + anchorRect.Width / 2.0) / dpiX;
            double topDip = (anchorRect.Bottom + 8) / dpiY;
            _toolbar.ShowToastAt(text, centerDip, topDip, isError: isError, durationMs: durationMs);
        });
    }
}
