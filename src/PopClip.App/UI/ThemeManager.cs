using System.Windows;
using System.Windows.Media;
using PopClip.App.Config;
using WpfApplication = System.Windows.Application;

namespace PopClip.App.UI;

/// <summary>主题/字体的全局调度器。
/// 把 ToolbarThemeMode 解析为：
///   - Toolbar* 系列画刷：浮窗外观（与 Theme.xaml 中以 ToolbarXxx{Suffix} 命名的预设资源对应）
///   - Settings.* 系列画刷：设置窗口及对话窗、剪贴板等次级窗口的整体色调
///   - Settings.FontFamily / ToolbarFontFamily：界面与浮窗字体回退链
/// 全部写入 Application.Resources 顶层，DynamicResource 自动联动；
/// 这样切主题不必再逐窗口推送，新窗口打开也能立刻接续</summary>
internal static class ThemeManager
{
    /// <summary>主题切换需要覆盖的浮窗资源后缀，与 Theme.xaml 中的 Light/Dark/各预设
    /// 八个画刷一一对应。Separator 与 Border 公用同色，但分键便于后续单独细化</summary>
    private static readonly string[] ToolbarSuffixes =
    {
        "Background", "Shadow", "Border", "Foreground",
        "Hover", "AccentSoft", "ToastBackground", "Separator",
    };

    private static readonly string[] SettingsThemeKeys =
    {
        "Settings.Window.Background",
        "Settings.Sidebar.Background",
        "Settings.Sidebar.SelectedBackground",
        "Settings.Sidebar.HoverBackground",
        "Settings.Card.Background",
        "Settings.Card.Border",
        "Settings.Card.SubtleBackground",
        "Settings.Foreground",
        "Settings.SubtleForeground",
        "Settings.Muted",
        "Settings.Stroke",
        "Settings.Accent",
        "Settings.AccentHover",
        "Settings.AccentSoft",
        "Settings.Input.Background",
        "Settings.Input.Border",
        "Settings.Input.BorderFocused",
        "Settings.Hover",
    };

    public static void Apply(AppSettings settings)
    {
        var res = WpfApplication.Current?.Resources;
        if (res is null) return;

        // 先切 WPF-UI 控件库主题（ToggleSwitch / NumberBox / ComboBox 等内部画刷）
        // 否则深色主题下控件 Content 文本仍取浅色主题写死的黑色
        ApplyWpfUiControlTheme(settings);
        ApplyToolbarTheme(res, settings);
        ApplySettingsTheme(res, settings);
        ApplyFonts(res, settings);
    }

    /// <summary>切换 WPF-UI 控件库内置的明暗主题。
    /// 彩色预设也按 Dark 渲染，让控件 Content 文本与彩色背景产生足够对比</summary>
    private static void ApplyWpfUiControlTheme(AppSettings settings)
    {
        var theme = IsDark(settings.ToolbarTheme)
            ? Wpf.Ui.Appearance.ApplicationTheme.Dark
            : Wpf.Ui.Appearance.ApplicationTheme.Light;
        try
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                theme,
                Wpf.Ui.Controls.WindowBackdropType.None,
                updateAccent: true);
        }
        catch
        {
            // 测试 / 设计器环境下偶尔抛异常；忽略不影响功能
        }
    }

    private static void ApplyToolbarTheme(ResourceDictionary res, AppSettings settings)
    {
        var resolved = ResolveTheme(settings.ToolbarTheme);
        var prefix = ToolbarPrefix(resolved);
        foreach (var suffix in ToolbarSuffixes)
        {
            var sourceKey = $"{prefix}{suffix}";
            var targetKey = $"Toolbar{suffix}";
            if (res.Contains(sourceKey))
            {
                res[targetKey] = res[sourceKey];
            }
        }
        // 系统强调色仅作用于基础三档；彩色预设有自洽的强调色，再叠会破坏整体感
        if (settings.FollowAccentColor && IsBaseTheme(resolved))
        {
            var blended = BlendAccent(SystemThemeHelper.AccentColor(), resolved);
            res["ToolbarAccentSoft"] = new SolidColorBrush(blended);
        }
    }

    private static void ApplySettingsTheme(ResourceDictionary res, AppSettings settings)
    {
        var resolved = ResolveTheme(settings.ToolbarTheme);
        // 浅色模式回到 SettingsTheme.xaml 中声明的默认浅色画刷：移除顶层覆盖即可
        if (resolved == ToolbarThemeMode.Light)
        {
            ResetSettingsKeys(res);
            return;
        }

        if (resolved == ToolbarThemeMode.Dark)
        {
            ApplyNeutralDarkSettingsTheme(res);
            return;
        }

        ApplyPresetSettingsTheme(res, resolved);
    }

    /// <summary>基础 Dark 主题：保留原有"中性深灰"调色板，避免无端染色破坏 Windows Fluent 观感</summary>
    private static void ApplyNeutralDarkSettingsTheme(ResourceDictionary res)
    {
        res["Settings.Window.Background"] = Brush(0x24, 0x28, 0x2E);
        res["Settings.Sidebar.Background"] = Brush(0x27, 0x2C, 0x33);
        res["Settings.Sidebar.SelectedBackground"] = Brush(0x31, 0x45, 0x61);
        res["Settings.Sidebar.HoverBackground"] = Brush(0x31, 0x37, 0x40);
        res["Settings.Card.Background"] = Brush(0x2B, 0x30, 0x37);
        res["Settings.Card.Border"] = Brush(0x4A, 0x51, 0x5B);
        res["Settings.Card.SubtleBackground"] = Brush(0x25, 0x2A, 0x31);
        res["Settings.Foreground"] = Brush(0xF2, 0xF4, 0xF7);
        res["Settings.SubtleForeground"] = Brush(0xA8, 0xB0, 0xBA);
        res["Settings.Muted"] = Brush(0x8A, 0x95, 0xA3);
        res["Settings.Stroke"] = Brush(0x43, 0x4A, 0x54);
        res["Settings.Accent"] = Brush(0x4D, 0x90, 0xFE);
        res["Settings.AccentHover"] = Brush(0x6C, 0xA4, 0xFF);
        res["Settings.AccentSoft"] = Brush(0x2C, 0x42, 0x5E);
        res["Settings.Input.Background"] = Brush(0x25, 0x2A, 0x31);
        res["Settings.Input.Border"] = Brush(0x4A, 0x51, 0x5B);
        res["Settings.Input.BorderFocused"] = Brush(0x4D, 0x90, 0xFE);
        res["Settings.Hover"] = Brush(0x36, 0x3D, 0x46);
    }

    /// <summary>彩色预设：从浮窗 Background / Foreground / Border / Accent 派生整套设置窗口画刷。
    /// 让设置窗口与浮窗共享色相，整体氛围一致；同时通过亮度调节保持卡片与背景的层次感。
    /// SubtleForeground / Muted 用"fg 向 bg 少量混合"派生而不是单纯把 fg 加深 —— 后者在
    /// 中等亮度背景（如 MistyGreen #688E73）下会让 muted 与 bg 明度接近，HintText 看不清</summary>
    private static void ApplyPresetSettingsTheme(ResourceDictionary res, ToolbarThemeMode mode)
    {
        var palette = PresetPalettes.For(mode);
        var bg = palette.Background;
        var fg = palette.Foreground;
        var border = palette.Border;
        var accent = palette.Accent;
        var accentSoft = palette.AccentSoft;

        res["Settings.Window.Background"] = new SolidColorBrush(Shift(bg, -0.10));
        res["Settings.Sidebar.Background"] = new SolidColorBrush(Shift(bg, -0.18));
        res["Settings.Sidebar.SelectedBackground"] = new SolidColorBrush(accentSoft);
        res["Settings.Sidebar.HoverBackground"] = new SolidColorBrush(Shift(bg, -0.05));
        res["Settings.Card.Background"] = new SolidColorBrush(bg);
        res["Settings.Card.Border"] = new SolidColorBrush(border);
        res["Settings.Card.SubtleBackground"] = new SolidColorBrush(Shift(bg, 0.06));
        res["Settings.Foreground"] = new SolidColorBrush(fg);
        // 朝 bg 混 12% / 22%：保持 fg 主色相，明度差缩小但仍能与 bg 拉开
        res["Settings.SubtleForeground"] = new SolidColorBrush(Mix(fg, bg, 0.12));
        res["Settings.Muted"] = new SolidColorBrush(Mix(fg, bg, 0.22));
        res["Settings.Stroke"] = new SolidColorBrush(border);
        res["Settings.Accent"] = new SolidColorBrush(accent);
        res["Settings.AccentHover"] = new SolidColorBrush(Shift(accent, 0.12));
        res["Settings.AccentSoft"] = new SolidColorBrush(accentSoft);
        res["Settings.Input.Background"] = new SolidColorBrush(Shift(bg, -0.04));
        res["Settings.Input.Border"] = new SolidColorBrush(border);
        res["Settings.Input.BorderFocused"] = new SolidColorBrush(accent);
        res["Settings.Hover"] = new SolidColorBrush(Shift(bg, 0.10));
    }

    private static void ResetSettingsKeys(ResourceDictionary res)
    {
        foreach (var key in SettingsThemeKeys)
        {
            if (res.Contains(key)) res.Remove(key);
        }
    }

    private static void ApplyFonts(ResourceDictionary res, AppSettings settings)
    {
        try
        {
            res["Settings.FontFamily"] = new FontFamily(FontFamilyHelper.ResolveUi(settings.UiFontFamily));
        }
        catch
        {
            // 用户填的字体名无效时不抛，让 SettingsTheme.xaml 自带默认值继续生效
        }
        try
        {
            res["ToolbarFontFamily"] = new FontFamily(
                FontFamilyHelper.ResolveToolbar(settings.ToolbarFontFamily, settings.UiFontFamily));
        }
        catch
        {
            // 同上
        }
    }

    private static ToolbarThemeMode ResolveTheme(ToolbarThemeMode mode) => mode switch
    {
        ToolbarThemeMode.Auto => SystemThemeHelper.IsSystemDark() ? ToolbarThemeMode.Dark : ToolbarThemeMode.Light,
        _ => mode,
    };

    /// <summary>主题是否进入深色基底；Light = 否，其它（Dark / 各彩色预设）= 是。
    /// 设置窗口、子窗口的 WPF-UI 控件主题切换都基于此判定</summary>
    public static bool IsDark(ToolbarThemeMode mode) => ResolveTheme(mode) != ToolbarThemeMode.Light;

    private static bool IsBaseTheme(ToolbarThemeMode mode)
        => mode == ToolbarThemeMode.Light || mode == ToolbarThemeMode.Dark;

    private static string ToolbarPrefix(ToolbarThemeMode mode) => mode switch
    {
        ToolbarThemeMode.Dark => "ToolbarDark",
        ToolbarThemeMode.Light => "ToolbarLight",
        ToolbarThemeMode.QingciBlue => "ToolbarQingciBlue",
        ToolbarThemeMode.DeepInkGreen => "ToolbarDeepInkGreen",
        ToolbarThemeMode.MistyGreen => "ToolbarMistyGreen",
        ToolbarThemeMode.SunsetRose => "ToolbarSunsetRose",
        ToolbarThemeMode.DistantMountain => "ToolbarDistantMountain",
        ToolbarThemeMode.Sandalwood => "ToolbarSandalwood",
        _ => "ToolbarLight",
    };

    private static Color BlendAccent(Color accent, ToolbarThemeMode theme)
    {
        var factor = theme == ToolbarThemeMode.Dark ? 0.36 : 0.18;
        var baseColor = theme == ToolbarThemeMode.Dark
            ? Color.FromRgb(0x2B, 0x30, 0x37)
            : Color.FromRgb(0xFF, 0xFF, 0xFF);
        byte Blend(byte a, byte b) => (byte)Math.Round(a * factor + b * (1 - factor));
        return Color.FromRgb(Blend(accent.R, baseColor.R), Blend(accent.G, baseColor.G), Blend(accent.B, baseColor.B));
    }

    /// <summary>把颜色按指定百分比向白/黑端移动。
    /// percent>0 提亮（向白），percent&lt;0 加深（向黑）。用于派生 Hover / 卡片背景等同色相的相邻层级</summary>
    private static Color Shift(Color c, double percent)
    {
        if (percent >= 0)
        {
            byte M(byte v) => (byte)Math.Clamp(v + (255 - v) * percent, 0, 255);
            return Color.FromRgb(M(c.R), M(c.G), M(c.B));
        }
        var f = -percent;
        byte D(byte v) => (byte)Math.Clamp(v * (1 - f), 0, 255);
        return Color.FromRgb(D(c.R), D(c.G), D(c.B));
    }

    /// <summary>两色线性混合：t=0 全 c1，t=1 全 c2。
    /// 用来把 Foreground 朝 Background 少量混合派生次级前景色，
    /// 比 Shift 更稳健 —— Shift 在浅 fg + 中等 bg 组合下容易让派生色与 bg 撞明度</summary>
    private static Color Mix(Color c1, Color c2, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte M(byte a, byte b) => (byte)Math.Round(a * (1 - t) + b * t);
        return Color.FromRgb(M(c1.R, c2.R), M(c1.G, c2.G), M(c1.B, c2.B));
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b)
        => new(Color.FromRgb(r, g, b));
}

/// <summary>彩色预设的核心颜色对，给 ThemeManager 派生用</summary>
internal readonly record struct ThemePalette(Color Background, Color Foreground, Color Border, Color Accent, Color AccentSoft);

internal static class PresetPalettes
{
    public static ThemePalette For(ToolbarThemeMode mode) => mode switch
    {
        ToolbarThemeMode.QingciBlue => new(
            Background: Rgb(0x11, 0x39, 0x74),
            Foreground: Rgb(0xC7, 0xE5, 0xE6),
            Border: Rgb(0x1E, 0x4F, 0x8E),
            Accent: Rgb(0x99, 0xD3, 0xD4),
            AccentSoft: Rgb(0x2A, 0x6D, 0xA8)),

        ToolbarThemeMode.DeepInkGreen => new(
            Background: Rgb(0x2B, 0x56, 0x4A),
            Foreground: Rgb(0xE2, 0xEB, 0xB7),
            Border: Rgb(0x3F, 0x6E, 0x5F),
            Accent: Rgb(0xC4, 0xD3, 0x73),
            AccentSoft: Rgb(0x5A, 0x82, 0x66)),

        ToolbarThemeMode.MistyGreen => new(
            Background: Rgb(0x68, 0x8E, 0x73),
            Foreground: Rgb(0xFB, 0xF1, 0xDC),
            Border: Rgb(0x7A, 0xA0, 0x83),
            Accent: Rgb(0xF6, 0xE9, 0xCE),
            AccentSoft: Rgb(0x8F, 0xB4, 0x98)),

        ToolbarThemeMode.SunsetRose => new(
            Background: Rgb(0x8B, 0x3A, 0x4D),
            Foreground: Rgb(0xFF, 0xE2, 0xD5),
            Border: Rgb(0xA0, 0x49, 0x60),
            Accent: Rgb(0xFF, 0xC2, 0xCE),
            AccentSoft: Rgb(0xB2, 0x5A, 0x72)),

        ToolbarThemeMode.DistantMountain => new(
            Background: Rgb(0x2B, 0x3D, 0x4F),
            Foreground: Rgb(0xD5, 0xDB, 0xE5),
            Border: Rgb(0x3D, 0x51, 0x69),
            Accent: Rgb(0x88, 0xA8, 0xC7),
            AccentSoft: Rgb(0x4A, 0x64, 0x81)),

        ToolbarThemeMode.Sandalwood => new(
            Background: Rgb(0x5C, 0x40, 0x33),
            Foreground: Rgb(0xF2, 0xE1, 0xC2),
            Border: Rgb(0x73, 0x53, 0x46),
            Accent: Rgb(0xE5, 0xC2, 0x8E),
            AccentSoft: Rgb(0x8B, 0x65, 0x49)),

        _ => new(
            Background: Rgb(0x2B, 0x30, 0x37),
            Foreground: Rgb(0xF2, 0xF4, 0xF7),
            Border: Rgb(0x4A, 0x51, 0x5B),
            Accent: Rgb(0x4D, 0x90, 0xFE),
            AccentSoft: Rgb(0x2C, 0x42, 0x5E)),
    };

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
