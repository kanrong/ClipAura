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

    private readonly ILog _log;
    private bool _listening;

    public event Action? PauseRequested;
    public event Action? ToolbarRequested;
    public event Action? OcrRequested;
    public event Action? ScreenshotRequested;

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
            return HotKeyRegistrationStatus.Invalid("格式错误，请使用 Ctrl+Alt+P 这种写法");
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

        return modifiers != 0 && key != 0;
    }

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
    HotKeyRegistrationStatus Screenshot);

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
