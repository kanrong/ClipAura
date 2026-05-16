using PopClip.Core.Actions;
using PopClip.Core.Logging;

namespace PopClip.App.Services;

internal sealed class ActionHost : IActionHost
{
    public ITextReplacer Replacer { get; }
    public IUrlLauncher UrlLauncher { get; }
    public IClipboardWriter Clipboard { get; }
    public INotificationSink Notifier { get; }
    public ISettingsProvider Settings { get; }
    public IAiTextService Ai { get; }
    public IPasteService Paste { get; }
    public IClipboardHistoryLauncher? ClipboardHistory { get; }
    public ILog Log { get; }
    public IResultDialogPresenter? ResultDialog { get; }
    public IInlineBubblePresenter? Bubble { get; }
    /// <summary>单例 ActionHost 永远没有 Descriptor 上下文；
    /// SelectionSessionManager.RunAction 在调用动作前会用 ScopedActionHost 包装注入</summary>
    public ActionDescriptor? Descriptor => null;

    public ActionHost(
        ILog log,
        ITextReplacer replacer,
        IUrlLauncher urlLauncher,
        IClipboardWriter clipboard,
        INotificationSink notifier,
        ISettingsProvider settings,
        IAiTextService ai,
        IPasteService paste,
        IClipboardHistoryLauncher? clipboardHistory = null,
        IResultDialogPresenter? resultDialog = null,
        IInlineBubblePresenter? bubble = null)
    {
        Log = log;
        Replacer = replacer;
        UrlLauncher = urlLauncher;
        Clipboard = clipboard;
        Notifier = notifier;
        Settings = settings;
        Ai = ai;
        Paste = paste;
        ClipboardHistory = clipboardHistory;
        ResultDialog = resultDialog;
        Bubble = bubble;
    }
}

/// <summary>把内层 IActionHost 全部能力按引用转发，并在自己身上挂一个 Descriptor 上下文。
/// 让智能动作的 RunAsync 可以读 host.Descriptor.OutputMode 决定输出走 Copy / Bubble / CopyAndBubble / Dialog。
/// 每次 RunAction 都会构造一个新实例，杜绝跨动作状态污染</summary>
internal sealed class ScopedActionHost : IActionHost
{
    private readonly IActionHost _inner;

    public ScopedActionHost(IActionHost inner, ActionDescriptor? descriptor)
    {
        _inner = inner;
        Descriptor = descriptor;
    }

    public ITextReplacer Replacer => _inner.Replacer;
    public IUrlLauncher UrlLauncher => _inner.UrlLauncher;
    public IClipboardWriter Clipboard => _inner.Clipboard;
    public INotificationSink Notifier => _inner.Notifier;
    public ISettingsProvider Settings => _inner.Settings;
    public IAiTextService Ai => _inner.Ai;
    public IPasteService Paste => _inner.Paste;
    public IClipboardHistoryLauncher? ClipboardHistory => _inner.ClipboardHistory;
    public ILog Log => _inner.Log;
    public IResultDialogPresenter? ResultDialog => _inner.ResultDialog;
    public IInlineBubblePresenter? Bubble => _inner.Bubble;
    public ActionDescriptor? Descriptor { get; }
}
