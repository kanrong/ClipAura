using System.Drawing;
using System.Windows;
using PopClip.App.Services;
using PopClip.App.UI;
using PopClip.Core.Actions;
using PopClip.Core.Logging;
using PopClip.Core.Model;
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
    private readonly FloatingToolbar _toolbar;
    private readonly IInlineBubblePresenter? _bubble;
    private OcrSelectionWindow? _currentWindow;

    public OcrCaptureCoordinator(
        ILog log,
        OcrService ocr,
        SelectionSessionManager session,
        ClipboardWriter clipboard,
        FloatingToolbar toolbar,
        IInlineBubblePresenter? bubble = null)
    {
        _log = log;
        _ocr = ocr;
        _session = session;
        _clipboard = clipboard;
        _toolbar = toolbar;
        _bubble = bubble;
    }

    public void Trigger()
    {
        if (!_ocr.IsAvailable)
        {
            WpfApplication.Current.Dispatcher.Invoke(() =>
            {
                MessageBox.Show(
                    "未检测到可用的 OCR 引擎。\n\n请在 Windows 设置 → 时间和语言 → 语言 → 添加"
                    + "可识别 OCR 的语言包（如简体中文、英文）。",
                    "OCR 不可用",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            });
            return;
        }

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

    private async Task RunCaptureAsync(Rectangle physical)
    {
        if (physical.Width <= 0 || physical.Height <= 0) return;
        try
        {
            using var bitmap = new Bitmap(physical.Width, physical.Height);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(physical.Left, physical.Top, 0, 0, bitmap.Size);
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var text = await _ocr.RecognizeAsync(bitmap, cts.Token).ConfigureAwait(false);
            _log.Info("ocr recognized", ("len", text.Length), ("rect", $"{physical.Width}x{physical.Height}"));

            var trimmed = text.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                WpfApplication.Current.Dispatcher.Invoke(() =>
                    _toolbar.ShowInlineToast("OCR 未识别到文本", isError: true, durationMs: 3500));
                return;
            }

            // 主动写入剪贴板：浮窗 timeout 关掉之后，识别结果仍可 Ctrl+V 兜底
            _clipboard.SetText(trimmed);

            // 浮窗 anchor = 截图框的物理像素矩形；浮窗会出现在它的下方，跟正常选区体验一致
            var anchorRect = new SelectionRect(physical.Left, physical.Top, physical.Right, physical.Bottom);
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

    /// <summary>给 toast 用的内容预览：去掉换行、压缩空白、最多 36 字符 + 省略号。
    /// toast 单行展示，太长会被截断且失去可读性；36 字符在 13px 字号下大约 200px 宽，
    /// 与浮窗常见宽度相近</summary>
    private static string BuildPreview(string text)
    {
        var compact = string.Join(' ', text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        if (compact.Length <= 36) return compact;
        return compact[..36] + "…";
    }
}
