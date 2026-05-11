using PopClip.Core.Logging;
using PopClip.Core.Model;
using PopClip.Uia.Clipboard;

namespace PopClip.Uia;

/// <summary>对外统一的"读取当前选中文本"入口，串联 UIA + 剪贴板兜底</summary>
public sealed class TextAcquisitionService
{
    private readonly ILog _log;
    private readonly UiaTextAcquirer _uia;
    private readonly ClipboardFallback _clipboard;

    public TextAcquisitionService(ILog log, UiaTextAcquirer uia, ClipboardFallback clipboard)
    {
        _log = log;
        _uia = uia;
        _clipboard = clipboard;
    }

    public AcquisitionOutcome? Acquire(ForegroundWindowInfo foreground, SelectionRect mouseHintRect)
    {
        // 先 UIA：成功就直接用
        var uiaResult = _uia.TryAcquire();
        if (uiaResult is not null)
        {
            var ctx = new SelectionContext(
                uiaResult.Text,
                uiaResult.Source,
                foreground,
                uiaResult.Rect,
                uiaResult.IsEditable,
                DateTime.UtcNow);
            return new AcquisitionOutcome(ctx, uiaResult.Element);
        }

        // 剪贴板兜底
        var text = _clipboard.CopySelectionViaCtrlC(TimeSpan.FromMilliseconds(220));
        if (!string.IsNullOrEmpty(text))
        {
            var ctx = new SelectionContext(
                text,
                AcquisitionSource.ClipboardFallback,
                foreground,
                mouseHintRect,
                IsLikelyEditable: false, // 兜底路径无法可靠判断
                DateTime.UtcNow);
            return new AcquisitionOutcome(ctx, null);
        }

        _log.Debug("acquisition exhausted all paths",
            ("foreground", foreground.ProcessName),
            ("class", foreground.WindowClassName));
        return null;
    }
}

public sealed record AcquisitionOutcome(SelectionContext Context, System.Windows.Automation.AutomationElement? Element);
