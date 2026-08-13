using System.ComponentModel;
using System.Windows.Interop;
using PopClip.App.Config;
using PopClip.Core.Logging;
using PopClip.Hooks.Interop;

namespace PopClip.App.Hosting;

internal sealed class HotKeyManager : IDisposable
{
    private const int PauseId = 101;
    private const int ToolbarId = 102;
    private const int OcrId = 103;
    private const int ScreenshotId = 104;
    private const int ScreenshotDelayId = 105;
    private const int ClipboardHistoryId = 106;

    private readonly ILog _log;
    private bool _listening;

    public event Action? PauseRequested;
    public event Action? ToolbarRequested;
    public event Action? OcrRequested;
    public event Action? ScreenshotRequested;
    public event Action? ScreenshotDelayRequested;
    public event Action? ClipboardHistoryRequested;

    public HotKeyManager(ILog log) => _log = log;

    public HotKeyApplyResult Apply(AppSettings settings)
    {
        EnsureListening();
        UnregisterAll();
        return new HotKeyApplyResult(
            settings.EnablePauseHotKey
                ? Register(PauseId, settings.PauseHotKey)
                : HotKeyRegistrationStatus.Disabled("未启用"),
            settings.EnableToolbarHotKey
                ? Register(ToolbarId, settings.ToolbarHotKey)
                : HotKeyRegistrationStatus.Disabled("未启用"),
            settings.EnableOcrHotKey
                ? Register(OcrId, settings.OcrHotKey)
                : HotKeyRegistrationStatus.Disabled("未启用"),
            settings.EnableScreenshotHotKey
                ? Register(ScreenshotId, settings.ScreenshotHotKey)
                : HotKeyRegistrationStatus.Disabled("未启用"),
            settings.EnableScreenshotDelayHotKey
                ? Register(ScreenshotDelayId, settings.ScreenshotDelayHotKey)
                : HotKeyRegistrationStatus.Disabled("未启用"),
            settings.EnableClipboardHistoryHotKey
                ? Register(ClipboardHistoryId, settings.ClipboardHistoryHotKey)
                : HotKeyRegistrationStatus.Disabled("未启用"));
    }

    private void EnsureListening()
    {
        if (_listening) return;
        ComponentDispatcher.ThreadFilterMessage += OnThreadMessage;
        _listening = true;
    }

    private HotKeyRegistrationStatus Register(int id, string text)
    {
        if (!TryParse(text, out var modifiers, out var key))
        {
            _log.Warn("hotkey parse failed", ("hotkey", text));
            return HotKeyRegistrationStatus.Invalid("格式错误，请使用 Ctrl+Alt+P 或 PrintScreen 这种写法");
        }

        modifiers |= NativeMethods.MOD_NOREPEAT;
        if (!NativeMethods.RegisterHotKey(0, id, modifiers, key))
        {
            var err = new Win32Exception().Message;
            _log.Warn("hotkey register failed",
                ("hotkey", text),
                ("err", err));
            return HotKeyRegistrationStatus.Failed($"注册失败，可能与其他程序冲突：{err}");
        }

        return HotKeyRegistrationStatus.Registered();
    }

    private void OnThreadMessage(ref MSG msg, ref bool handled)
    {
        if (msg.message != NativeMethods.WM_HOTKEY) return;
        var id = msg.wParam.ToInt32();
        if (id == PauseId)
        {
            PauseRequested?.Invoke();
            handled = true;
        }
        else if (id == ToolbarId)
        {
            ToolbarRequested?.Invoke();
            handled = true;
        }
        else if (id == OcrId)
        {
            OcrRequested?.Invoke();
            handled = true;
        }
        else if (id == ScreenshotId)
        {
            ScreenshotRequested?.Invoke();
            handled = true;
        }
        else if (id == ScreenshotDelayId)
        {
            ScreenshotDelayRequested?.Invoke();
            handled = true;
        }
        else if (id == ClipboardHistoryId)
        {
            ClipboardHistoryRequested?.Invoke();
            handled = true;
        }
    }

    private static bool TryParse(string text, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= NativeMethods.MOD_CONTROL;
                continue;
            }
            if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= NativeMethods.MOD_ALT;
                continue;
            }
            if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= NativeMethods.MOD_SHIFT;
                continue;
            }
            if (part.Equals("Win", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= NativeMethods.MOD_WIN;
                continue;
            }

            key = ParseKey(part);
        }

        // PrintScreen / F1 这类专用键允许不带修饰符；字母数字必须带 Ctrl/Alt/Shift/Win，
        // 否则会变成全局劫持键盘。
        return key != 0 && (modifiers != 0 || IsStandaloneKey(key));
    }

    private static bool IsStandaloneKey(uint key)
        => key == NativeMethods.VK_SNAPSHOT
           || key is >= 0x70 and <= 0x87; // F1-F24

    private static uint ParseKey(string key)
    {
        if (key.Length == 1)
        {
            var ch = char.ToUpperInvariant(key[0]);
            if (ch is >= 'A' and <= 'Z') return ch;
            if (ch is >= '0' and <= '9') return ch;
        }

        if (key.Equals("Space", StringComparison.OrdinalIgnoreCase)) return NativeMethods.VK_SPACE;
        if (key.Equals("Enter", StringComparison.OrdinalIgnoreCase)) return NativeMethods.VK_RETURN;
        if (key.Equals("Esc", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Escape", StringComparison.OrdinalIgnoreCase)) return NativeMethods.VK_ESCAPE;
        if (key.Equals("PrintScreen", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Print", StringComparison.OrdinalIgnoreCase)
            || key.Equals("PrtSc", StringComparison.OrdinalIgnoreCase)
            || key.Equals("PrtScn", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Snapshot", StringComparison.OrdinalIgnoreCase))
        {
            return NativeMethods.VK_SNAPSHOT;
        }
        if (key.StartsWith("F", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(key[1..], out var f)
            && f is >= 1 and <= 24)
        {
            return (uint)(0x70 + f - 1);
        }

        return 0;
    }

    private static void UnregisterAll()
    {
        NativeMethods.UnregisterHotKey(0, PauseId);
        NativeMethods.UnregisterHotKey(0, ToolbarId);
        NativeMethods.UnregisterHotKey(0, OcrId);
        NativeMethods.UnregisterHotKey(0, ScreenshotId);
        NativeMethods.UnregisterHotKey(0, ScreenshotDelayId);
        NativeMethods.UnregisterHotKey(0, ClipboardHistoryId);
    }

    public void Dispose()
    {
        UnregisterAll();
        if (_listening)
        {
            ComponentDispatcher.ThreadFilterMessage -= OnThreadMessage;
            _listening = false;
        }
    }
}

public sealed record HotKeyApplyResult(
    HotKeyRegistrationStatus Pause,
    HotKeyRegistrationStatus Toolbar,
    HotKeyRegistrationStatus Ocr,
    HotKeyRegistrationStatus Screenshot,
    HotKeyRegistrationStatus ScreenshotDelay,
    HotKeyRegistrationStatus ClipboardHistory);

public sealed record HotKeyRegistrationStatus(HotKeyRegistrationState State, string Message)
{
    public static HotKeyRegistrationStatus Registered() => new(HotKeyRegistrationState.Registered, "已注册");
    public static HotKeyRegistrationStatus Disabled(string message = "未启用") => new(HotKeyRegistrationState.Disabled, message);
    public static HotKeyRegistrationStatus Invalid(string message) => new(HotKeyRegistrationState.Invalid, message);
    public static HotKeyRegistrationStatus Failed(string message) => new(HotKeyRegistrationState.Failed, message);
}

public enum HotKeyRegistrationState
{
    Registered,
    Disabled,
    Invalid,
    Failed,
}
