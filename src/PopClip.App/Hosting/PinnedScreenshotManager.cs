using System.Drawing;
using PopClip.App.Services;
using PopClip.App.UI;
using PopClip.Core.Logging;

namespace PopClip.App.Hosting;

/// <summary>钉图窗口的全局管理器：把同一刻可能存在的多个 PinnedScreenshotWindow
/// 集中托管，方便"全部关闭"或后续做"会话恢复"等功能。
///
/// 设计原则：
/// 1. 每个钉图实例独立 hwnd / 状态，关一个不影响其他；
/// 2. 重启不恢复（与计划一致），保持生命周期简单；
/// 3. 双击图像 → OCR 的回调由调用方注入（OcrCaptureCoordinator），
///    管理器不直接依赖 OCR 链路，避免循环依赖。</summary>
internal sealed class PinnedScreenshotManager
{
    private readonly ILog _log;
    private readonly ClipboardWriter _clipboard;
    private readonly ClipboardHistoryService? _clipHistory;
    private readonly List<PinnedScreenshotWindow> _windows = new();
    private readonly object _gate = new();

    /// <summary>把已截好的截图钉到屏幕。double-click 触发的 OCR 回调由 host 注入。
    /// pinAtAnchor=true 时窗口落在 anchor 物理像素位置（与原截图精确重合），
    /// 用于"截图预览 → 钉图"切换时位置无缝；
    /// false 时窗口由 OS / WPF 决定初始位置，用于"剪贴板 → 钉图"等无 anchor 场景</summary>
    public Action<PinnedScreenshotWindow, Rectangle>? DoubleClickOcrHandler { get; set; }

    public PinnedScreenshotManager(ILog log, ClipboardWriter clipboard, ClipboardHistoryService? clipHistory = null)
    {
        _log = log;
        _clipboard = clipboard;
        _clipHistory = clipHistory;
    }

    public PinnedScreenshotWindow Pin(byte[] pngBytes, uint anchorDpiX, uint anchorDpiY, Rectangle anchorPhysical)
    {
        var window = new PinnedScreenshotWindow(
            _log,
            pngBytes,
            anchorDpiX,
            anchorDpiY,
            _clipboard,
            onClosed: OnWindowClosed,
            onOcrRequested: DoubleClickOcrHandler);

        lock (_gate) { _windows.Add(window); }
        window.ShowAtAnchor(anchorPhysical);
        // 钉图入库：用户能在剪贴板历史窗"图片"标签页里回看曾经钉过的截图。
        // try-catch 是为了避免历史库异常波及 Pin 主流程
        try { _clipHistory?.AppendImage(pngBytes, sourceProc: null); }
        catch (Exception ex) { _log.Warn("pin append image history failed", ("err", ex.Message)); }
        return window;
    }

    private void OnWindowClosed(PinnedScreenshotWindow window)
    {
        lock (_gate)
        {
            _windows.Remove(window);
        }
    }

    public void CloseAll()
    {
        // 取快照防止 Closed 回调里改集合再 lock 自死
        List<PinnedScreenshotWindow> snapshot;
        lock (_gate) { snapshot = new List<PinnedScreenshotWindow>(_windows); }
        foreach (var w in snapshot)
        {
            try { w.Close(); } catch { }
        }
    }

    public IReadOnlyList<PinnedScreenshotWindow> ActiveWindows
    {
        get
        {
            lock (_gate) { return new List<PinnedScreenshotWindow>(_windows); }
        }
    }
}
