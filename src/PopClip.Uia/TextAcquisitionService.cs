using System.Text;
using PopClip.Core.Logging;
using PopClip.Core.Model;
using PopClip.Core.Session;
using PopClip.Hooks.Interop;
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

    public AcquisitionAttempt Acquire(
        ForegroundWindowInfo foreground,
        SelectionRect mouseHintRect,
        SelectionTrigger trigger,
        bool isLikelyWindowDrag = false)
    {
        // 先 UIA：成功就直接用
        var uiaResult = _uia.TryAcquire();
        var focusedWindowClassName = TryGetFocusedWindowClassName(foreground.Hwnd);
        var focusedControlTypeName = _uia.LastFocusedControlTypeName;
        if (uiaResult is not null)
        {
            var ctx = new SelectionContext(
                uiaResult.Text,
                uiaResult.Source,
                foreground,
                uiaResult.Rect,
                uiaResult.IsEditable,
                DateTime.UtcNow);
            return AcquisitionAttempt.Success(
                new AcquisitionOutcome(ctx, uiaResult.Element, focusedWindowClassName, focusedControlTypeName));
        }
        if (_uia.LastFocusedElementWasPassword)
        {
            _log.Info("clipboard fallback skipped: password element",
                ("focusedClass", focusedWindowClassName),
                ("controlType", focusedControlTypeName));
            return AcquisitionAttempt.Skipped;
        }
        if (trigger == SelectionTrigger.MouseDoubleClick
            && _uia.LastFocusedElementRejectsClipboardFallbackOnDoubleClick)
        {
            _log.Info("clipboard fallback skipped: double-click on action control",
                ("focusedClass", focusedWindowClassName),
                ("controlType", focusedControlTypeName),
                ("foreground", foreground.ProcessName));
            return AcquisitionAttempt.Skipped;
        }
        // 拖动期间顶层窗口位置/大小变化 → 这是拖窗体，不是选文本。
        // 直接跳过兜底，避免向前台进程合成 Ctrl+C（自绘编辑器 Zed/VSCode 等无选区时会复制整行污染剪贴板）
        if (isLikelyWindowDrag)
        {
            _log.Info("clipboard fallback skipped: window drag detected",
                ("foreground", foreground.ProcessName),
                ("class", foreground.WindowClassName),
                ("focusedClass", focusedWindowClassName),
                ("controlType", focusedControlTypeName));
            return AcquisitionAttempt.Skipped;
        }
        // 拖动 ListItem/TreeItem/TabItem 等"项目类"控件 = OLE 拖放（拖文件/节点/标签页），
        // 不存在文本选区可言。典型场景：explorer.exe 拖文件、Chrome 拖标签页
        if (trigger == SelectionTrigger.MouseDrag
            && _uia.LastFocusedElementRejectsClipboardFallbackOnDrag)
        {
            _log.Info("clipboard fallback skipped: drag on item control",
                ("focusedClass", focusedWindowClassName),
                ("controlType", focusedControlTypeName),
                ("foreground", foreground.ProcessName));
            return AcquisitionAttempt.Skipped;
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
            return AcquisitionAttempt.Success(
                new AcquisitionOutcome(ctx, null, focusedWindowClassName, focusedControlTypeName));
        }

        _log.Debug("acquisition exhausted all paths",
            ("foreground", foreground.ProcessName),
            ("class", foreground.WindowClassName),
            ("focusedClass", focusedWindowClassName),
            ("controlType", focusedControlTypeName));
        return AcquisitionAttempt.Failed;
    }

    private static string TryGetFocusedWindowClassName(nint foregroundHwnd)
    {
        if (foregroundHwnd == 0) return "";

        try
        {
            var threadId = NativeMethods.GetWindowThreadProcessId(foregroundHwnd, out _);
            if (threadId == 0) return "";

            var info = new NativeMethods.GUITHREADINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GUITHREADINFO>(),
            };
            if (!NativeMethods.GetGUIThreadInfo(threadId, ref info) || info.hwndFocus == 0)
            {
                return "";
            }

            var clsBuf = new StringBuilder(256);
            return NativeMethods.GetClassName(info.hwndFocus, clsBuf, clsBuf.Capacity) > 0
                ? clsBuf.ToString()
                : "";
        }
        catch
        {
            return "";
        }
    }
}

public sealed record AcquisitionAttempt(AcquisitionOutcome? Outcome, bool WasSkipped)
{
    public static AcquisitionAttempt Success(AcquisitionOutcome outcome) => new(outcome, WasSkipped: false);
    public static AcquisitionAttempt Skipped { get; } = new(null, WasSkipped: true);
    public static AcquisitionAttempt Failed { get; } = new(null, WasSkipped: false);
}

public sealed record AcquisitionOutcome(
    SelectionContext Context,
    System.Windows.Automation.AutomationElement? Element,
    string FocusedWindowClassName,
    string FocusedControlTypeName);
