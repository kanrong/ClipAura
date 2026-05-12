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

    private readonly ILog _log;
    private bool _listening;

    public event Action? PauseRequested;
    public event Action? ToolbarRequested;

    public HotKeyManager(ILog log) => _log = log;

    public void Apply(AppSettings settings)
    {
        EnsureListening();
        UnregisterAll();
        Register(PauseId, settings.PauseHotKey);
        Register(ToolbarId, settings.ToolbarHotKey);
    }

    private void EnsureListening()
    {
        if (_listening) return;
        ComponentDispatcher.ThreadFilterMessage += OnThreadMessage;
        _listening = true;
    }

    private void Register(int id, string text)
    {
        if (!TryParse(text, out var modifiers, out var key))
        {
            _log.Warn("hotkey parse failed", ("hotkey", text));
            return;
        }

        modifiers |= NativeMethods.MOD_NOREPEAT;
        if (!NativeMethods.RegisterHotKey(0, id, modifiers, key))
        {
            _log.Warn("hotkey register failed",
                ("hotkey", text),
                ("err", new Win32Exception().Message));
        }
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
