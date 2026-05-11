using PopClip.Core.Actions;
using PopClip.Core.Logging;
using PopClip.Uia.Clipboard;

namespace PopClip.App.Services;

/// <summary>对外暴露给 IAction 的剪贴板写入器，内部委托给 STA ClipboardAccess</summary>
internal sealed class ClipboardWriter : IClipboardWriter
{
    private readonly ILog _log;
    private readonly ClipboardAccess _clipboard;

    public ClipboardWriter(ILog log, ClipboardAccess clipboard)
    {
        _log = log;
        _clipboard = clipboard;
    }

    public void SetText(string text)
    {
        try { _clipboard.SetText(text); }
        catch (Exception ex) { _log.Warn("clipboard writer failed", ("err", ex.Message)); }
    }
}
