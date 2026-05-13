using PopClip.Core.Actions;
using PopClip.Core.Logging;
using PopClip.Uia.Clipboard;

namespace PopClip.App.Services;

/// <summary>对外暴露给 IAction 的剪贴板写入器，内部委托给 STA ClipboardAccess。
/// 写入前会顺手通知 ClipboardHistoryService，避免我们自己写出去的内容被监听器又记回来</summary>
internal sealed class ClipboardWriter : IClipboardWriter
{
    private readonly ILog _log;
    private readonly ClipboardAccess _clipboard;
    private ClipboardHistoryService? _history;

    public ClipboardWriter(ILog log, ClipboardAccess clipboard)
    {
        _log = log;
        _clipboard = clipboard;
    }

    /// <summary>Host 装配完成后注入；history service 启动后即可联动去重</summary>
    public void AttachHistory(ClipboardHistoryService history) => _history = history;

    public void SetText(string text)
    {
        try
        {
            _history?.NoteSelfWritten(text);
            _clipboard.SetText(text);
        }
        catch (Exception ex) { _log.Warn("clipboard writer failed", ("err", ex.Message)); }
    }
}
