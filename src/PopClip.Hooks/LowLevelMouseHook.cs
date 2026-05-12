using System.Runtime.InteropServices;
using System.Threading.Channels;
using PopClip.Core.Logging;
using PopClip.Core.Session;
using PopClip.Hooks.Interop;

namespace PopClip.Hooks;

/// <summary>低级鼠标钩子。回调内只做最少工作（解码 + Channel 投递），
/// 重逻辑在消费侧异步处理，避免触发 LowLevelHooksTimeout 被系统摘除</summary>
public sealed class LowLevelMouseHook
{
    private readonly ILog _log;
    private readonly Channel<InputEvent> _channel;
    private readonly NativeMethods.HookProc _proc;
    private long _lastEventTicks;

    public DateTime LastEventUtc => new(Volatile.Read(ref _lastEventTicks), DateTimeKind.Utc);

    public void ResetHeartbeat() => Volatile.Write(ref _lastEventTicks, DateTime.UtcNow.Ticks);

    public LowLevelMouseHook(ILog log, Channel<InputEvent> channel)
    {
        _log = log;
        _channel = channel;
        _proc = HookCallback;
        ResetHeartbeat();
    }

    /// <summary>在钩子线程内调用。返回 hook handle</summary>
    public nint Install()
    {
        var hMod = NativeMethods.GetModuleHandle(null);
        return NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, hMod, 0);
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0) return NativeMethods.CallNextHookEx(0, nCode, wParam, lParam);

        try
        {
            var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            var msg = (int)wParam;
            var now = DateTime.UtcNow;
            Volatile.Write(ref _lastEventTicks, now.Ticks);
            // WM_LBUTTONDBLCLK 是系统合成给目标窗口的消息，低级钩子收不到，
            // 双击需要在状态机里依据两次 down/up 的时间+距离自行识别
            // mouse down/up 同步采样 Shift / Ctrl 状态：
            //   Shift+原地点击 → 走正常工具条路径（与扩展选区配合）
            //   Ctrl +原地点击 → 直接弹"粘贴"
            var shift = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
            var ctrl = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
            var alt = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;
            InputEvent? ev = msg switch
            {
                NativeMethods.WM_LBUTTONDOWN => new MouseDownEvent(data.pt.X, data.pt.Y, shift, ctrl, alt, now),
                NativeMethods.WM_LBUTTONUP => new MouseUpEvent(data.pt.X, data.pt.Y, shift, ctrl, alt, now),
                NativeMethods.WM_MOUSEMOVE => new MouseMoveEvent(data.pt.X, data.pt.Y, IsLeftDown(), now),
                _ => null,
            };
            if (ev is not null && !_channel.Writer.TryWrite(ev))
            {
                _log.Warn("mouse event channel full", ("msg", msg));
            }
        }
        catch (Exception ex)
        {
            _log.Error("mouse hook decode failed", ex);
        }

        return NativeMethods.CallNextHookEx(0, nCode, wParam, lParam);
    }

    private static bool IsLeftDown()
        => (NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0;
}
