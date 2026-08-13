using System.Diagnostics;
using System.IO;
using PopClip.Core.Actions;
using PopClip.Core.Logging;
using PopClip.Core.Model;
using PopClip.Hooks;

namespace PopClip.App.Services;

internal sealed class UrlLauncher : IUrlLauncher
{
    private readonly ILog _log;

    public UrlLauncher(ILog log) => _log = log;

    public void Open(string url) => Open(url, source: null);

    public void Open(string url, ForegroundWindowInfo? source)
    {
        if (source is not null && TryOpenInForegroundBrowser(url, source)) return;
        OpenWithShell(url);
    }

    private void OpenWithShell(string url)
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

    /// <summary>前台是浏览器时，用那个 exe 开新标签。不是浏览器或启动失败则返回 false，由调用方走系统默认。</summary>
    private bool TryOpenInForegroundBrowser(string url, ForegroundWindowInfo source)
    {
        if (!BrowserProcess.TryGetNewTabArgs(source.ProcessName, url, out var args)) return false;

        var exe = ForegroundWatcher.TryGetProcessImagePath(source.ProcessId);
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            _log.Debug("browser exe not found, fallback to default",
                ("proc", source.ProcessName), ("pid", source.ProcessId));
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
            });
            _log.Info("opened url in foreground browser",
                ("proc", source.ProcessName), ("url", url));
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("open in foreground browser failed, fallback to default",
                ("proc", source.ProcessName), ("err", ex.Message));
            return false;
        }
    }
}

/// <summary>按进程名识别浏览器，并给出该引擎打开新标签的命令行。</summary>
internal static class BrowserProcess
{
    private static readonly HashSet<string> Chromium = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "vivaldi", "chromium", "arc",
        "opera", "opera_gx",
        "thorium", "iridium", "ungoogled-chromium",
        "qqbrowser", "360se", "360chrome", "360chromex",
        "sogouexplorer", "liebao", "maxthon",
        "whale", "yandex", "slimjet", "centbrowser", "dragon",
        "ucbrowser", "2345explorer", "theworld",
    };

    private static readonly HashSet<string> Firefox = new(StringComparer.OrdinalIgnoreCase)
    {
        "firefox", "waterfox", "librewolf", "floorp", "zen", "palemoon", "seamonkey",
    };

    private static readonly HashSet<string> InternetExplorer = new(StringComparer.OrdinalIgnoreCase)
    {
        "iexplore",
    };

    public static bool TryGetNewTabArgs(string processName, string url, out string args)
    {
        args = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        var stem = StripExe(processName);
        if (stem.Length == 0) return false;
        // WebView2 / crashpad 等不是用户在用的浏览器窗口
        if (stem.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase)
            || stem.Contains("crashpad", StringComparison.OrdinalIgnoreCase)
            || stem.Contains("crashhandler", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var quoted = "\"" + uri.AbsoluteUri + "\"";
        if (Chromium.Contains(stem))
        {
            args = "--new-tab " + quoted;
            return true;
        }
        if (Firefox.Contains(stem))
        {
            args = "-new-tab " + quoted;
            return true;
        }
        if (InternetExplorer.Contains(stem))
        {
            args = quoted;
            return true;
        }
        return false;
    }

    private static string StripExe(string processName)
    {
        if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return processName[..^4];
        return processName;
    }
}
