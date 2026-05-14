using PopClip.Hooks.Interop;

namespace PopClip.Hooks.Window;

/// <summary>给 WPF 浮窗叠加 WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW，并提供不抢焦点的显示路径</summary>
public static class WindowStyleHelper
{
    public static void ApplyNoActivateToolWindow(nint hwnd)
    {
        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);
    }

    /// <summary>用 ShowWindow + SetWindowPos 组合避免 WPF Show() 走激活路径</summary>
    public static void ShowNoActivate(nint hwnd, int x, int y)
    {
        NativeMethods.SetWindowPos(
            hwnd, NativeMethods.HWND_TOPMOST,
            x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
    }

    public static void ShowNoActivate(nint hwnd, int x, int y, int width, int height)
    {
        NativeMethods.SetWindowPos(
            hwnd, NativeMethods.HWND_TOPMOST,
            x, y, Math.Max(1, width), Math.Max(1, height),
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
    }

    public static void Hide(nint hwnd)
    {
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
    }
}
