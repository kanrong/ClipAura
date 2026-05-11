using System.Runtime.InteropServices;
using PopClip.Core.Logging;
using PopClip.Hooks.Interop;

namespace PopClip.Uia.Clipboard;

/// <summary>剪贴板兜底链路：
/// 1) STA 上备份当前剪贴板
/// 2) 后台线程模拟 Ctrl+C
/// 3) 后台线程 + STA Get 轮询剪贴板文本变化
/// 4) STA 上恢复
/// SendInput 与 Thread.Sleep 留在后台，避免阻塞 UI 与 STA</summary>
public sealed class ClipboardFallback
{
    private readonly ILog _log;
    private readonly ClipboardAccess _clipboard;
    private static readonly object Gate = new();

    public ClipboardFallback(ILog log, ClipboardAccess clipboard)
    {
        _log = log;
        _clipboard = clipboard;
    }

    public string? CopySelectionViaCtrlC(TimeSpan timeout)
    {
        if (!Monitor.TryEnter(Gate, TimeSpan.FromMilliseconds(50)))
        {
            _log.Warn("clipboard fallback busy, skip");
            return null;
        }

        try
        {
            var snapshot = _clipboard.Capture();
            var originalText = _clipboard.GetText();

            try
            {
                SendCtrlC();

                var deadline = DateTime.UtcNow + timeout;
                string? newText = null;
                while (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(15);
                    var t = _clipboard.GetText();
                    if (t is not null && !string.Equals(t, originalText, StringComparison.Ordinal))
                    {
                        newText = t;
                        break;
                    }
                }
                return newText;
            }
            finally
            {
                _clipboard.Restore(snapshot);
            }
        }
        finally
        {
            Monitor.Exit(Gate);
        }
    }

    private static void SendCtrlC()
    {
        const ushort VK_CONTROL = 0x11;
        const ushort VK_C = 0x43;

        var inputs = new NativeMethods.INPUT[4];
        inputs[0].type = NativeMethods.INPUT_KEYBOARD;
        inputs[0].u.ki = new NativeMethods.KEYBDINPUT { wVk = VK_CONTROL };
        inputs[1].type = NativeMethods.INPUT_KEYBOARD;
        inputs[1].u.ki = new NativeMethods.KEYBDINPUT { wVk = VK_C };
        inputs[2].type = NativeMethods.INPUT_KEYBOARD;
        inputs[2].u.ki = new NativeMethods.KEYBDINPUT { wVk = VK_C, dwFlags = NativeMethods.KEYEVENTF_KEYUP };
        inputs[3].type = NativeMethods.INPUT_KEYBOARD;
        inputs[3].u.ki = new NativeMethods.KEYBDINPUT { wVk = VK_CONTROL, dwFlags = NativeMethods.KEYEVENTF_KEYUP };

        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
