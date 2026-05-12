using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PopClip.Hooks.Interop;

namespace PopClip.App.UI;

internal sealed class WindowChromeWorker
{
    private readonly Window _window;
    private readonly bool _transientBackdrop;

    public WindowChromeWorker(Window window, bool transientBackdrop = false)
    {
        _window = window;
        _transientBackdrop = transientBackdrop;
        _window.SourceInitialized += (_, _) => Apply();
    }

    public void Apply()
    {
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == 0) return;

        try
        {
            var dark = SystemThemeHelper.IsSystemDark() ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
                ref dark,
                Marshal.SizeOf<int>());

            var backdrop = _transientBackdrop
                ? NativeMethods.DWMSBT_TRANSIENTWINDOW
                : NativeMethods.DWMSBT_MAINWINDOW;
            NativeMethods.DwmSetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE,
                ref backdrop,
                Marshal.SizeOf<int>());

            var margins = new NativeMethods.MARGINS
            {
                cxLeftWidth = 0,
                cxRightWidth = 0,
                cyTopHeight = 1,
                cyBottomHeight = 0,
            };
            NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }
        catch
        {
        }
    }
}
