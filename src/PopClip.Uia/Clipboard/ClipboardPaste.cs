using System.Runtime.InteropServices;
using PopClip.Core.Logging;
using PopClip.Hooks.Interop;

namespace PopClip.Uia.Clipboard;

/// <summary>剪贴板回写：STA 备份 → STA 写文本 → 后台 SetForegroundWindow + Ctrl+V → STA 恢复</summary>
public sealed class ClipboardPaste
{
    private readonly ILog _log;
    private readonly ClipboardAccess _clipboard;

    public ClipboardPaste(ILog log, ClipboardAccess clipboard)
    {
        _log = log;
        _clipboard = clipboard;
    }

    /// <summary>把当前剪贴板内容直接粘贴到目标窗口，不修改剪贴板。
    /// 用于"用户主动想粘贴"的场景（Shift+点击空白光标位置）</summary>
    public bool PasteCurrent(nint targetHwnd)
    {
        try
        {
            if (targetHwnd != 0)
            {
                NativeMethods.SetForegroundWindow(targetHwnd);
                Thread.Sleep(40);
            }
            SendCtrlV();
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("paste current failed", ("err", ex.Message));
            return false;
        }
    }

    public bool PasteAsReplacement(nint targetHwnd, string newText)
    {
        var snapshot = _clipboard.Capture();
        try
        {
            try
            {
                _clipboard.SetText(newText);
            }
            catch (Exception ex)
            {
                _log.Warn("clipboard set text failed", ("err", ex.Message));
                return false;
            }

            if (targetHwnd != 0)
            {
                NativeMethods.SetForegroundWindow(targetHwnd);
                Thread.Sleep(40);
            }

            SendCtrlV();

            // 给目标应用消化 Ctrl+V 的时间再恢复剪贴板
            Thread.Sleep(120);
            return true;
        }
        finally
        {
            _clipboard.Restore(snapshot);
        }
    }

    internal static void SendCtrlV()
    {
        const ushort VK_CONTROL = 0x11;
        const ushort VK_V = 0x56;

        var inputs = new NativeMethods.INPUT[4];
        inputs[0].type = NativeMethods.INPUT_KEYBOARD;
        inputs[0].u.ki = new NativeMethods.KEYBDINPUT { wVk = VK_CONTROL };
        inputs[1].type = NativeMethods.INPUT_KEYBOARD;
        inputs[1].u.ki = new NativeMethods.KEYBDINPUT { wVk = VK_V };
        inputs[2].type = NativeMethods.INPUT_KEYBOARD;
        inputs[2].u.ki = new NativeMethods.KEYBDINPUT { wVk = VK_V, dwFlags = NativeMethods.KEYEVENTF_KEYUP };
        inputs[3].type = NativeMethods.INPUT_KEYBOARD;
        inputs[3].u.ki = new NativeMethods.KEYBDINPUT { wVk = VK_CONTROL, dwFlags = NativeMethods.KEYEVENTF_KEYUP };

        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
