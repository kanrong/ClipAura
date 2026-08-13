using System.Runtime.InteropServices;
using PopClip.Core.Logging;
using PopClip.Hooks.Interop;

namespace PopClip.Uia.Clipboard;

/// <summary>剪贴板兜底用的复制组合键。
/// 终端面必须用 Ctrl+Shift+C：Ctrl+C 在无选区 / TUI / agent 会话里是中断。</summary>
public enum ClipboardCopyChord
{
    CtrlC,
    CtrlShiftC,
}

/// <summary>剪贴板兜底链路：
/// 1) STA 上备份当前剪贴板
/// 2) 后台线程模拟复制组合键
/// 3) 后台线程 + STA Get 轮询剪贴板文本变化
/// 4) STA 上恢复
/// SendInput 与 Thread.Sleep 留在后台，避免阻塞 UI 与 STA</summary>
public sealed class ClipboardFallback
{
    private readonly ILog _log;
    private readonly ClipboardAccess _clipboard;
    private readonly CapturedSelectionCache _capturedSelection;
    private static readonly object Gate = new();

    public ClipboardFallback(ILog log, ClipboardAccess clipboard, CapturedSelectionCache capturedSelection)
    {
        _log = log;
        _clipboard = clipboard;
        _capturedSelection = capturedSelection;
    }

    public string? CopySelection(TimeSpan timeout, ClipboardCopyChord chord)
    {
        if (!Monitor.TryEnter(Gate, TimeSpan.FromMilliseconds(50)))
        {
            _log.Warn("clipboard fallback busy, skip");
            return null;
        }

        try
        {
            var snapshot = _clipboard.Capture();
            var beforeSeq = NativeMethods.GetClipboardSequenceNumber();

            try
            {
                if (!WaitForSafeCopyModifiers(TimeSpan.FromMilliseconds(350)))
                {
                    _log.Info("clipboard fallback skipped: modifier still down");
                    return null;
                }

                SendCopyChord(chord);

                var deadline = DateTime.UtcNow + timeout;
                string? newText = null;
                while (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(15);
                    if (NativeMethods.GetClipboardSequenceNumber() == beforeSeq)
                    {
                        continue;
                    }

                    newText = _clipboard.GetText();
                    if (!string.IsNullOrEmpty(newText))
                    {
                        // 此刻剪贴板正是源应用响应复制组合键写入的选区多格式数据（Firefox 等含 CF_HTML）。
                        // 在下面 finally 还原旧剪贴板前抓一份快照缓存，供"复制"动作直接回写：
                        // 既保住富文本格式，又不必对终端再合成一次会触发 ^C 中断的 Ctrl+C
                        _capturedSelection.Store(newText, _clipboard.Capture());
                    }
                    break;
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

    private static bool WaitForSafeCopyModifiers(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsCopyAlteringModifierDown()) return true;
            Thread.Sleep(10);
        }
        return !IsCopyAlteringModifierDown();
    }

    private static bool IsCopyAlteringModifierDown()
    {
        const int VK_LMENU = 0xA4;
        const int VK_RMENU = 0xA5;
        const int VK_LWIN = 0x5B;
        const int VK_RWIN = 0x5C;

        return IsDown(NativeMethods.VK_SHIFT)
            || IsDown(NativeMethods.VK_LSHIFT)
            || IsDown(NativeMethods.VK_RSHIFT)
            || IsDown(NativeMethods.VK_MENU)
            || IsDown(VK_LMENU)
            || IsDown(VK_RMENU)
            || IsDown(VK_LWIN)
            || IsDown(VK_RWIN);
    }

    private static bool IsDown(int vk)
        => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static void SendCopyChord(ClipboardCopyChord chord)
    {
        const ushort VK_CONTROL = 0x11;
        const ushort VK_SHIFT = 0x10;
        const ushort VK_C = 0x43;

        NativeMethods.INPUT[] inputs = chord == ClipboardCopyChord.CtrlShiftC
            ? new NativeMethods.INPUT[6]
            : new NativeMethods.INPUT[4];

        var i = 0;
        inputs[i++] = Key(VK_CONTROL, down: true);
        if (chord == ClipboardCopyChord.CtrlShiftC)
        {
            inputs[i++] = Key(VK_SHIFT, down: true);
        }
        inputs[i++] = Key(VK_C, down: true);
        inputs[i++] = Key(VK_C, down: false);
        if (chord == ClipboardCopyChord.CtrlShiftC)
        {
            inputs[i++] = Key(VK_SHIFT, down: false);
        }
        inputs[i] = Key(VK_CONTROL, down: false);

        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static NativeMethods.INPUT Key(ushort vk, bool down)
    {
        var input = new NativeMethods.INPUT { type = NativeMethods.INPUT_KEYBOARD };
        input.u.ki = new NativeMethods.KEYBDINPUT
        {
            wVk = vk,
            dwFlags = down ? 0u : NativeMethods.KEYEVENTF_KEYUP,
        };
        return input;
    }
}
