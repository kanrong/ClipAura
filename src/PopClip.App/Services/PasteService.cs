using PopClip.Core.Actions;
using PopClip.Core.Logging;
using PopClip.Core.Model;
using PopClip.Uia.Clipboard;

namespace PopClip.App.Services;

/// <summary>把"剪贴板有无文本"轻量探测与"模拟 Ctrl+V 粘贴"两类能力合并暴露给 IActionHost。
/// 内置粘贴动作通过它实现：CanRun 用 HasClipboardText 判定可见性，
/// RunAsync 用 PasteAsync 触发实际粘贴</summary>
internal sealed class PasteService : IPasteService
{
    private readonly ILog _log;
    private readonly ClipboardAccess _clipboard;
    private readonly ClipboardPaste _paste;

    public PasteService(ILog log, ClipboardAccess clipboard, ClipboardPaste paste)
    {
        _log = log;
        _clipboard = clipboard;
        _paste = paste;
    }

    /// <summary>仅判定剪贴板是否包含文本，不复制内容。
    /// 走 ClipboardAccess.HasText（内部 STA 上 ContainsText），避免每次浮窗弹出
    /// 都把潜在的大段剪贴板正文搬到本进程，控制 CanRun 调用的最坏延时</summary>
    public bool HasClipboardText
    {
        get
        {
            try { return _clipboard.HasText(); }
            catch (Exception ex)
            {
                _log.Warn("paste service HasClipboardText failed", ("err", ex.Message));
                return false;
            }
        }
    }

    public Task<bool> PasteAsync(SelectionContext context, CancellationToken ct)
    {
        var hwnd = context.Foreground.Hwnd;
        return Task.Run(() =>
        {
            try { return _paste.PasteCurrent(hwnd); }
            catch (Exception ex)
            {
                _log.Warn("paste service PasteAsync failed", ("err", ex.Message));
                return false;
            }
        }, ct);
    }
}
