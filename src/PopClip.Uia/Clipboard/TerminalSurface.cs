using PopClip.Core.Model;

namespace PopClip.Uia.Clipboard;

/// <summary>判断当前前台是否属于"Ctrl+C = 中断"的终端面。
/// 纯终端按进程 / 窗口类识别；Cursor / VS Code 等混合 IDE 在 UIA 已失败时
/// 也按终端面处理（编辑器划词通常 UIA 能成功，失败的多半是集成终端）。</summary>
internal static class TerminalSurface
{
    public static bool AvoidsCtrlCFallback(ForegroundWindowInfo foreground, string focusedWindowClassName)
        => IsDedicatedTerminal(foreground.ProcessName, foreground.WindowClassName, focusedWindowClassName)
           || IsHybridIde(foreground.ProcessName);

    public static bool IsDedicatedTerminal(string processName, string windowClassName, string focusedWindowClassName)
    {
        if (MatchProcess(processName,
                "WindowsTerminal", "OpenConsole", "conhost",
                "wezterm-gui", "wezterm",
                "alacritty", "kitty", "mintty",
                "Tabby", "Hyper", "Terminus",
                "Warp", "warp-terminal",
                "zap", "zap-oss", "OpenWarp",
                "FluentTerminal", "FluentTerminal.App",
                "putty", "puttytel",
                "cmd", "powershell", "pwsh"))
        {
            return true;
        }

        return MatchClass(windowClassName, focusedWindowClassName,
            "ConsoleWindowClass",
            "CASCADIA_HOSTING_WINDOW_CLASS",
            "PseudoConsoleWindow",
            "mintty");
    }

    public static bool IsHybridIde(string processName)
        => MatchProcess(processName,
            "Cursor", "Code", "Code - Insiders",
            "Windsurf", "VSCodium", "Trae",
            "Devenv");

    private static bool MatchProcess(string processName, params string[] stems)
    {
        var stem = StripExe(processName);
        if (stem.Length == 0) return false;
        foreach (var candidate in stems)
        {
            if (string.Equals(stem, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool MatchClass(string windowClassName, string focusedWindowClassName, params string[] classes)
    {
        foreach (var candidate in classes)
        {
            if (EqualsClass(windowClassName, candidate) || EqualsClass(focusedWindowClassName, candidate))
                return true;
        }
        return false;
    }

    private static bool EqualsClass(string actual, string expected)
        => !string.IsNullOrEmpty(actual)
           && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static string StripExe(string processName)
    {
        if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return processName[..^4];
        return processName;
    }
}
