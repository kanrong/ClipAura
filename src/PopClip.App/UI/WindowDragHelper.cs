using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using PopClip.Hooks.Interop;

namespace PopClip.App.UI;

/// <summary>无 Snap 干预的窗口拖动 helper。
///
/// 为什么不用 Window.DragMove()：
/// DragMove() 内部走 WM_SYSCOMMAND + SC_MOVE 启动 OS 级移动模态循环。这条路径
/// 受 Windows 10/11 的 Aero Snap / Snap Layouts 全权管制 —— 窗口拖到屏幕顶部 / 边沿时
/// OS 会主动把它"贴边" / "最大化"，对于 AllowsTransparency=True + ResizeMode=NoResize
/// 这种"不能正常 maximize"的窗口，结果是窗口被弹回工作区上沿并保持固定空隙
/// （表象就是 ScreenshotPreviewWindow 无法把上边拖到屏幕顶部 / 屏外）。
///
/// 这里手动处理 PreviewMouseMove + Win32 SetWindowPos，跳过 SC_MOVE 让窗口位置
/// 完全由像素位移决定，OS Snap 不会拦截。窗口可以拖到任意位置，包括屏幕外。</summary>
internal static class WindowDragHelper
{
    /// <summary>从当前鼠标按下事件开始接管拖动。
    /// 调用方应在 MouseLeftButtonDown 事件里调，不需要在意 e.Handled。
    /// 内部用 CaptureMouse + PreviewMouseMove 监听跟踪鼠标到 MouseUp / LostCapture 为止。
    ///
    /// onMoved（可选）：每次主窗位置更新后回调，参数 (dx, dy) 是相对拖动起点的累计物理像素位移。
    /// 用于联动跟随窗口（如截图预览主窗带动工具条 / 标注弹层一起搬家）—— 由调用方自行 SetWindowPos
    /// 把 follower 按同样 dx/dy 推过去。直接用 dx/dy 而不是 follower 列表，避免本工具感知具体业务。</summary>
    public static void BeginDrag(Window window, Action<int, int>? onMoved = null)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // 物理像素的拖动起点：鼠标 + 窗口左上。后续位移直接加到 startRect 上，
        // 与窗口 DPI / 跨屏 / 缩放都无关，保证 1 像素鼠标位移 = 1 像素窗口位移
        if (!NativeMethods.GetCursorPos(out var startCursor)) return;
        if (!NativeMethods.GetWindowRect(hwnd, out var startRect)) return;

        if (!window.CaptureMouse()) return;

        MouseEventHandler? moveHandler = null;
        MouseButtonEventHandler? upHandler = null;
        MouseEventHandler? captureLostHandler = null;

        void Cleanup()
        {
            if (moveHandler is not null) window.PreviewMouseMove -= moveHandler;
            if (upHandler is not null) window.PreviewMouseLeftButtonUp -= upHandler;
            if (captureLostHandler is not null) window.LostMouseCapture -= captureLostHandler;
            try { window.ReleaseMouseCapture(); } catch { }
        }

        // 参数类型显式标注 —— 让编译器把 lambda 推断为 MouseEventHandler / MouseButtonEventHandler
        // 而不是 generic EventHandler<>；后者无法直接订阅 PreviewMouseMove 等强类型 WPF 事件
        moveHandler = (object s, MouseEventArgs e) =>
        {
            // 用户在窗口外按了别的鼠标 / 系统弹了对话框时 LeftButton 可能已经松开但
            // 没触发 LeftButtonUp，主动停拖
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                Cleanup();
                return;
            }
            if (!NativeMethods.GetCursorPos(out var cursor)) return;
            int dx = cursor.X - startCursor.X;
            int dy = cursor.Y - startCursor.Y;
            NativeMethods.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                startRect.Left + dx,
                startRect.Top + dy,
                0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            // follower 回调放在主窗 SetWindowPos 之后：让"跟随窗口"看上去和主窗同步抵达新位置，
            // 中间不会出现"主窗动了一帧、follower 还在原地"的撕裂
            try { onMoved?.Invoke(dx, dy); } catch { }
        };

        upHandler = (object s, MouseButtonEventArgs e) => Cleanup();
        captureLostHandler = (object s, MouseEventArgs e) => Cleanup();

        window.PreviewMouseMove += moveHandler;
        window.PreviewMouseLeftButtonUp += upHandler;
        window.LostMouseCapture += captureLostHandler;
    }
}
