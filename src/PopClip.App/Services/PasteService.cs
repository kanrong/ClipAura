using PopClip.Core.Actions;
using PopClip.Core.Logging;
using PopClip.Core.Model;
using PopClip.Uia.Clipboard;

namespace PopClip.App.Services;

/// <summary>把"剪贴板有无文本"轻量探测 + "模拟 Ctrl+C / Ctrl+V" 两类键盘动作打包给 IActionHost。
/// 内置复制动作走 CopyAsync 让源应用主动写入多格式数据；
/// 内置粘贴动作走 PasteAsync 让目标应用按自身策略读取剪贴板，
/// 这样可以避免我们自己 Clipboard.SetText 把剪贴板降级为纯文本</summary>
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

    public Task<bool> CopyAsync(SelectionContext context, CancellationToken ct)
    {
        var hwnd = context.Foreground.Hwnd;
        return Task.Run(() =>
        {
            try { return _paste.CopyCurrent(hwnd); }
            catch (Exception ex)
            {
                _log.Warn("paste service CopyAsync failed", ("err", ex.Message));
                return false;
            }
        }, ct);
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
