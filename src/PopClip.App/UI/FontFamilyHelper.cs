namespace PopClip.App.UI;

/// <summary>统一应用层默认字体回退链。
/// "系统默认"首选 Windows 当前消息字体（SystemFonts.MessageFontFamily，
/// Win11 通常为 Segoe UI Variable，Win10 通常为 Segoe UI），让应用自动跟随 Windows 主体字体；
/// 之后串入中文优先回退链 Microsoft YaHei UI / 微软雅黑 / PingFang SC，保证在缺中文字形的环境下
/// 不出现方块字</summary>
internal static class FontFamilyHelper
{
    private const string ChineseFallbackChain = "Microsoft YaHei UI, Microsoft YaHei, 微软雅黑, PingFang SC, Segoe UI Variable Display, Segoe UI";

    /// <summary>设置窗口 / 全局 UI 默认字体回退链。
    /// 在 Windows 消息字体名之后追加中文优先链，让"系统字未含中文字形"时仍能正常显示中文</summary>
    public static string UiDefault => $"{PreferredUiName}, {ChineseFallbackChain}";

    /// <summary>浮窗默认字体回退链；与 UiDefault 同源，但 Segoe Variable 在低 DPI 上偶尔抖动，
    /// 这里仍包含它，统一由 Windows 系统字体在首位时优先使用，缺中文再回退</summary>
    public static string ToolbarDefault => $"{PreferredUiName}, {ChineseFallbackChain}";

    /// <summary>"系统默认"项展示给用户的字体名 = Windows 当前消息字体名。
    /// 用户切换 Win10 / Win11 / 高对比度主题后，这里会自动跟随变化</summary>
    public static string PreferredUiName => ResolveSystemMessageFamily();

    /// <summary>按"浮窗专字 → 全局字 → 默认链"优先级解析浮窗实际使用的字体</summary>
    public static string ResolveToolbar(string toolbarOverride, string globalOverride)
    {
        if (!string.IsNullOrWhiteSpace(toolbarOverride)) return toolbarOverride.Trim();
        if (!string.IsNullOrWhiteSpace(globalOverride)) return globalOverride.Trim();
        return ToolbarDefault;
    }

    /// <summary>按"全局字 → 默认链"优先级解析设置窗口实际使用的字体</summary>
    public static string ResolveUi(string globalOverride)
    {
        if (!string.IsNullOrWhiteSpace(globalOverride)) return globalOverride.Trim();
        return UiDefault;
    }

    private static string ResolveSystemMessageFamily()
    {
        try
        {
            var name = System.Windows.SystemFonts.MessageFontFamily?.Source;
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch
        {
            // 设计器 / 非 GUI 上下文可能没有 SystemFonts；走兜底
        }
        return "Segoe UI";
    }
}
