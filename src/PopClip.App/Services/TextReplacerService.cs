using System.Windows.Automation;
using PopClip.Core.Actions;
using PopClip.Core.Logging;
using PopClip.Core.Model;
using PopClip.Uia;
using PopClip.Uia.Clipboard;

namespace PopClip.App.Services;

/// <summary>组合 UIA 与剪贴板回写两条路径。SessionManager 持有当前会话的 UIA Element 引用，
/// 通过 SetCurrentElement 在弹出前注入</summary>
internal sealed class TextReplacerService : ITextReplacer
{
    private readonly ILog _log;
    private readonly UiaTextReplacer _uia;
    private readonly ClipboardPaste _paste;
    private AutomationElement? _currentElement;

    public TextReplacerService(ILog log, UiaTextReplacer uia, ClipboardPaste paste)
    {
        _log = log;
        _uia = uia;
        _paste = paste;
    }

    public void SetCurrentElement(AutomationElement? element) => _currentElement = element;

    public Task<bool> TryReplaceAsync(SelectionContext context, string newText, CancellationToken ct)
    {
        if (_uia.TryReplace(context, _currentElement, newText))
        {
            return Task.FromResult(true);
        }
        var ok = _paste.PasteAsReplacement(context.Foreground.Hwnd, newText);
        return Task.FromResult(ok);
    }
}
