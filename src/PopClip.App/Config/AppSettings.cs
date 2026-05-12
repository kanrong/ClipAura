namespace PopClip.App.Config;

/// <summary>浮窗按钮内容呈现方式</summary>
public enum ToolbarDisplayMode
{
    IconOnly,
    IconAndText,
    TextOnly,
}

/// <summary>整个应用的用户配置。MVP 仅落地 actions.json 与黑白名单</summary>
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

    /// <summary>浮窗按钮显示方式，默认图标+文字</summary>
    public ToolbarDisplayMode ToolbarDisplay { get; set; } = ToolbarDisplayMode.IconAndText;

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
