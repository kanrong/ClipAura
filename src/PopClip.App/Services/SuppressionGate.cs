using PopClip.App.Config;
using PopClip.Core.Logging;
using PopClip.Core.Model;
using PopClip.Hooks.Interop;

namespace PopClip.App.Services;

/// <summary>会话弹出前的最后一道闸门：黑白名单、全屏抑制、IME 期间抑制</summary>
internal sealed class SuppressionGate
{
    private readonly ILog _log;
    private readonly AppSettings _settings;

    public SuppressionGate(ILog log, AppSettings settings)
    {
        _log = log;
        _settings = settings;
    }

    public bool ShouldSuppress(ForegroundWindowInfo foreground, out string reason)
    {
        if (_settings.SuppressOnFullScreen && IsFullScreenAppRunning())
        {
            reason = "fullscreen";
            return true;
        }

        if (MatchClassName(foreground.WindowClassName))
        {
            reason = "classname-filter";
            return true;
        }

        if (MatchProcess(foreground.ProcessName))
        {
            reason = "process-filter";
            return true;
        }

        reason = "";
        return false;
    }

    private static bool IsFullScreenAppRunning()
    {
        if (NativeMethods.SHQueryUserNotificationState(out var state) != 0) return false;
        return state == NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
            || state == NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE
            || state == NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY;
    }

    private bool MatchProcess(string processName)
    {
        if (_settings.ProcessFilter.Count == 0) return !_settings.BlacklistMode ? true : false;
        var hit = _settings.ProcessFilter.Any(p =>
            string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));
        return _settings.BlacklistMode ? hit : !hit;
    }

    private bool MatchClassName(string className)
    {
        if (string.IsNullOrEmpty(className)) return false;
        return _settings.ClassNameFilter.Any(p =>
            string.Equals(p, className, StringComparison.OrdinalIgnoreCase));
    }
}
