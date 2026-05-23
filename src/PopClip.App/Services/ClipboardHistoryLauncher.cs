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

    public void Open(SelectionContext? anchorContext = null)
    {
        WpfApplication.Current?.Dispatcher.Invoke(() =>
        {
            if (_current is { IsVisible: true })
            {
                _current.Activate();
                return;
            }
            _current = new ClipboardHistoryWindow(_history, _writer, _replacer, anchorContext, _paste, _clipboardAccess);
            _current.Closed += (_, _) => _current = null;
            _current.Show();
            _current.Activate();
        });
    }
}
