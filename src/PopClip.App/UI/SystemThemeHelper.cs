using Microsoft.Win32;
using PopClip.Hooks.Interop;
using MediaColor = System.Windows.Media.Color;

namespace PopClip.App.UI;

internal static class SystemThemeHelper
{
    public static bool IsSystemDark()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);
            return value is int intValue && intValue == 0;
        }
        catch
        {
            return false;
        }
    }

    public static MediaColor AccentColor()
    {
        try
        {
            if (NativeMethods.DwmGetColorizationColor(out var raw, out _) == 0)
            {
                var r = (byte)((raw >> 16) & 0xFF);
                var g = (byte)((raw >> 8) & 0xFF);
                var b = (byte)(raw & 0xFF);
                return MediaColor.FromRgb(r, g, b);
            }
        }
        catch
        {
        }

        return MediaColor.FromRgb(0x00, 0x78, 0xD4);
    }
}
