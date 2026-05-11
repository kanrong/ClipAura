using PopClip.Hooks.Interop;

namespace PopClip.Hooks.Window;

/// <summary>monitor 物理像素工作区 + 该 monitor 的 effective DPI</summary>
public readonly record struct MonitorMetrics(int WorkLeft, int WorkTop, int WorkRight, int WorkBottom, uint DpiX, uint DpiY)
{
    public int WorkWidth => WorkRight - WorkLeft;
    public int WorkHeight => WorkBottom - WorkTop;
}

public static class MonitorQuery
{
    /// <summary>返回包含给定物理像素点的 monitor 工作区 + DPI。
    /// MONITOR_DEFAULTTONEAREST 保证总能返回一个有效 monitor</summary>
    public static MonitorMetrics FromPoint(int physicalX, int physicalY)
    {
        var pt = new NativeMethods.POINT { X = physicalX, Y = physicalY };
        var hMon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return FromHandle(hMon);
    }

    public static MonitorMetrics FromRect(int left, int top, int right, int bottom)
    {
        var rc = new NativeMethods.RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        var hMon = NativeMethods.MonitorFromRect(ref rc, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return FromHandle(hMon);
    }

    private static MonitorMetrics FromHandle(nint hMonitor)
    {
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        uint dpiX = 96, dpiY = 96;
        if (hMonitor != 0)
        {
            NativeMethods.GetMonitorInfo(hMonitor, ref info);
            // 如果 GetDpiForMonitor 不可用（Shcore 未启用），退化为 96
            try { NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MonitorDpiType.Effective, out dpiX, out dpiY); }
            catch { dpiX = 96; dpiY = 96; }
        }
        return new MonitorMetrics(
            info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom,
            dpiX == 0 ? 96 : dpiX,
            dpiY == 0 ? 96 : dpiY);
    }
}
