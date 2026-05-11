using PopClip.Core.Actions;
using PopClip.Core.Logging;

namespace PopClip.App.Services;

internal sealed class ActionHost : IActionHost
{
    public ITextReplacer Replacer { get; }
    public IUrlLauncher UrlLauncher { get; }
    public IClipboardWriter Clipboard { get; }
    public ILog Log { get; }

    public ActionHost(ILog log, ITextReplacer replacer, IUrlLauncher urlLauncher, IClipboardWriter clipboard)
    {
        Log = log;
        Replacer = replacer;
        UrlLauncher = urlLauncher;
        Clipboard = clipboard;
    }
}
