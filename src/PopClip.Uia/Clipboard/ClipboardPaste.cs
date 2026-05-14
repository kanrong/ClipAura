using System.Runtime.InteropServices;
using PopClip.Core.Logging;
using PopClip.Hooks.Interop;

namespace PopClip.Uia.Clipboard;

/// <summary>剪贴板键盘模拟器：把"复制 / 粘贴"两类动作交回系统去做，
/// 这样源/目标应用主动写入的多格式数据（CF_HTML / CF_RTF / 图片 / 文件清单 等）
/// 能原样进入或离开剪贴板，避免我们自己 SetText 把剪贴板降级为纯文本</summary>
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

    /// <summary>把目标窗口的当前选区拷贝到剪贴板（等价于用户按 Ctrl+C）。
    /// 关键差别在于"不要"自己用 Clipboard.SetText —— 那样只会写入 CF_UNICODETEXT 单一格式，
    /// HTML/RTF/图片 等会全部丢失，进而在 Word/Outlook 等富文本编辑器粘贴时表现为
    /// "格式丢失 / 个别符号方块乱码"。
    /// 流程：先把焦点切回目标窗口（浮窗是 NoActivate 不抢键盘焦点，但仍然显式 set 一次以防意外），
    /// 等 40ms 让目标窗口完成激活，再发系统 Ctrl+C，让源应用按它自己的方式把多格式数据写到剪贴板</summary>
    public bool CopyCurrent(nint targetHwnd)
    {
        try
        {
            if (targetHwnd != 0)
            {
                NativeMethods.SetForegroundWindow(targetHwnd);
                Thread.Sleep(40);
            }
            SendCtrlC();
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("copy current failed", ("err", ex.Message));
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

    internal static void SendCtrlV() => SendCtrlKey(0x56);

    internal static void SendCtrlC() => SendCtrlKey(0x43);

    /// <summary>合成一次 Ctrl + 单字母 的按下/抬起 INPUT 序列。
    /// 与单独维护 Ctrl+C / Ctrl+V 两份完全一致的代码相比，提炼成共享路径避免出现两边 KEYEVENTF/顺序不一致的隐性 bug</summary>
    private static void SendCtrlKey(ushort virtualKey)
    {
        const ushort VK_CONTROL = 0x11;

        var inputs = new NativeMethods.INPUT[4];
        inputs[0].type = NativeMethods.INPUT_KEYBOARD;
        inputs[0].u.ki = new NativeMethods.KEYBDINPUT { wVk = VK_CONTROL };
        inputs[1].type = NativeMethods.INPUT_KEYBOARD;
        inputs[1].u.ki = new NativeMethods.KEYBDINPUT { wVk = virtualKey };
        inputs[2].type = NativeMethods.INPUT_KEYBOARD;
        inputs[2].u.ki = new NativeMethods.KEYBDINPUT { wVk = virtualKey, dwFlags = NativeMethods.KEYEVENTF_KEYUP };
        inputs[3].type = NativeMethods.INPUT_KEYBOARD;
        inputs[3].u.ki = new NativeMethods.KEYBDINPUT { wVk = VK_CONTROL, dwFlags = NativeMethods.KEYEVENTF_KEYUP };

        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
