using System.Windows;
using PopClip.App.UI;
using PopClip.Core.Actions;
using PopClip.Core.Model;
using PopClip.Uia.Clipboard;
using WpfApplication = System.Windows.Application;

namespace PopClip.App.Services;

/// <summary>把 IClipboardHistoryLauncher 实现派发到 UI 线程，在 WPF FluentWindow 中显示历史面板</summary>
internal sealed class ClipboardHistoryLauncher : IClipboardHistoryLauncher
{
    private readonly ClipboardHistoryService _history;
    private readonly IClipboardWriter _writer;
    private readonly ITextReplacer _replacer;
    private readonly ClipboardPaste _paste;
    private readonly ClipboardAccess _clipboardAccess;
    private ClipboardHistoryWindow? _current;

    public ClipboardHistoryLauncher(
        ClipboardHistoryService history,
        IClipboardWriter writer,
        ITextReplacer replacer,
        ClipboardPaste paste,
        ClipboardAccess clipboardAccess)
    {
        _history = history;
        _writer = writer;
        _replacer = replacer;
        _paste = paste;
        _clipboardAccess = clipboardAccess;
    }

    public void Open(SelectionContext? anchorContext = null) => Open(anchorContext, pasteTargetProvider: null);

    /// <summary>带"粘贴目标窗口取值器"的打开重载。托盘/快捷键场景没有选区上下文，
    /// 由调用方传入一个回调，在用户点击"粘贴"的那一刻实时返回目标前台窗口句柄
    /// （而非打开时定格），以支持历史窗口常驻、用户来回切换后仍粘到正确窗口</summary>
    public void Open(SelectionContext? anchorContext, Func<nint>? pasteTargetProvider)
    {
        WpfApplication.Current?.Dispatcher.Invoke(() =>
        {
            if (_current is { IsVisible: true })
            {
                // 钉住时用户已经摆好位置，热键只唤回焦点，不跟着光标搬家
                if (!_current.IsPinned) _current.RepositionToCursor();
                _current.Activate();
                _current.FocusSearch();
                return;
            }
            _current = new ClipboardHistoryWindow(_history, _writer, _replacer, anchorContext, _paste, _clipboardAccess, pasteTargetProvider);
            _current.Closed += (_, _) => _current = null;
            _current.Show();
            _current.RepositionToCursor();
            _current.Activate();
        });
    }
}
