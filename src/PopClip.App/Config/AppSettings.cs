using PopClip.Core.Session;

namespace PopClip.App.Config;

/// <summary>浮窗按钮内容呈现方式</summary>
public enum ToolbarDisplayMode
{
    IconOnly,
    IconAndText,
    TextOnly,
}

/// <summary>浮窗颜色主题</summary>
public enum ToolbarThemeMode
{
    Auto,
    Light,
    Dark,
}

/// <summary>整个应用的用户配置</summary>
public sealed class AppSettings
{
    /// <summary>true=黑名单：列出的进程不弹；false=白名单：仅列出的进程弹</summary>
    public bool BlacklistMode { get; set; } = true;

    public List<string> ProcessFilter { get; set; } = new()
    {
        "Taskmgr.exe",
        "lsass.exe",
        "winlogon.exe",
    };

    public List<string> ClassNameFilter { get; set; } = new()
    {
        "ConsoleWindowClass", // 旧版 cmd
    };

    public bool SuppressOnFullScreen { get; set; } = true;
    public int MinTextLength { get; set; } = 1;
    public int MaxTextLength { get; set; } = 100_000;
    public bool LaunchAtStartup { get; set; }
    public bool FirstRunCompleted { get; set; }

    /// <summary>浮窗按钮显示方式，默认图标+文字</summary>
    public ToolbarDisplayMode ToolbarDisplay { get; set; } = ToolbarDisplayMode.IconAndText;

    /// <summary>浮窗颜色主题，默认跟随系统</summary>
    public ToolbarThemeMode ToolbarTheme { get; set; } = ToolbarThemeMode.Auto;

    public bool FollowAccentColor { get; set; } = true;
    public double ToolbarCornerRadius { get; set; } = 5;
    public double ToolbarButtonSpacing { get; set; } = 2;
    public double ToolbarFontSize { get; set; } = 12;
    public int ToolbarMaxActionsPerRow { get; set; } = 6;

    public SelectionPopupMode PopupMode { get; set; } = SelectionPopupMode.Immediate;
    public int PopupDelayMs { get; set; } = 200;
    public int HoverDelayMs { get; set; } = 300;
    public SelectionModifierKey RequiredModifier { get; set; } = SelectionModifierKey.Alt;

    public string PauseHotKey { get; set; } = "Ctrl+Alt+P";
    public string ToolbarHotKey { get; set; } = "Ctrl+Alt+Space";

    // ================== 浮窗自动消失触发条件 ==================
    // 鼠标离开浮窗一段时间后自动关闭
    public bool DismissOnMouseLeave { get; set; } = true;
    public int DismissMouseLeaveDelayMs { get; set; } = 800;
    // 前台窗口切换时关闭
    public bool DismissOnForegroundChanged { get; set; } = true;
    // 在浮窗外部按下鼠标时关闭
    public bool DismissOnClickOutside { get; set; } = true;
    // 按下 ESC 关闭
    public bool DismissOnEscapeKey { get; set; } = true;
    // 出现新选区候选时关闭旧浮窗
    public bool DismissOnNewSelection { get; set; } = true;
    // 执行动作后自动关闭（关闭=false 表示动作执行后留在原处便于多次点击）
    public bool DismissOnActionInvoked { get; set; } = true;

    /// <summary>搜索引擎名称，用作工具条按钮的显示文字（兼具用户可读性）</summary>
    public string SearchEngineName { get; set; } = "Google";

    /// <summary>搜索 URL 模板，{q} 会被替换为 UrlEncode 后的选区文本。
    /// 内置预设：
    ///   Google -> https://www.google.com/search?q={q}
    ///   Bing   -> https://www.bing.com/search?q={q}
    ///   Baidu  -> https://www.baidu.com/s?wd={q}
    /// 也允许用户填入任意自定义模板</summary>
    public string SearchUrlTemplate { get; set; } = "https://www.google.com/search?q={q}";
}
