using System.Diagnostics;
using PopClip.Core.Actions;
using PopClip.Core.Logging;

namespace PopClip.App.Services;

internal sealed class UrlLauncher : IUrlLauncher
{
    private readonly ILog _log;

    public UrlLauncher(ILog log) => _log = log;

    public void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn("open url failed", ("url", url), ("err", ex.Message));
        }
    }
}
