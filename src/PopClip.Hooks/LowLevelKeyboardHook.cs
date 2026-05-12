using System.Runtime.InteropServices;
using System.Threading.Channels;
using PopClip.Core.Logging;
using PopClip.Core.Session;
using PopClip.Hooks.Interop;

namespace PopClip.Hooks;

/// <summary>低级键盘钩子，仅产出 KeyEvent。Shift/Ctrl/Alt 状态使用 GetAsyncKeyState 即时查询，
/// 避免依赖钩子的修饰符位状态（不同 Windows 版本表现不一致）</summary>
public sealed class LowLevelKeyboardHook
{
    private readonly ILog _log;
    private readonly Channel<InputEvent> _channel;
    private readonly NativeMethods.HookProc _proc;
    private Func<KeyEvent, bool>? _interceptor;

    public LowLevelKeyboardHook(ILog log, Channel<InputEvent> channel)
    {
        _log = log;
        _channel = channel;
        _proc = HookCallback;
    }

    public nint Install()
    {
        var hMod = NativeMethods.GetModuleHandle(null);
        return NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, hMod, 0);
    }

    public void SetInterceptor(Func<KeyEvent, bool>? interceptor) => _interceptor = interceptor;

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0) return NativeMethods.CallNextHookEx(0, nCode, wParam, lParam);

        try
        {
            var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            var msg = (int)wParam;
            var isDown = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
            var isUp = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;
            if (!isDown && !isUp) return NativeMethods.CallNextHookEx(0, nCode, wParam, lParam);

            var shift = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
            var ctrl = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
            var alt = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;

            var ev = new KeyEvent((int)data.vkCode, isDown, shift, ctrl, alt, DateTime.UtcNow);
            if (_interceptor?.Invoke(ev) == true)
            {
                return 1;
            }
            _channel.Writer.TryWrite(ev);
        }
        catch (Exception ex)
        {
            _log.Error("keyboard hook decode failed", ex);
        }

        return NativeMethods.CallNextHookEx(0, nCode, wParam, lParam);
    }
}
