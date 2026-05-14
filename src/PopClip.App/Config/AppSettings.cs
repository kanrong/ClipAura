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

/// <summary>浮窗外缘层次风格</summary>
public enum ToolbarSurfaceStyle
{
    Shadow,
    Border,
    ShadowAndBorder,
}

public enum AiProviderPreset
{
    DeepSeekV4Flash,
    DeepSeekV4Pro,
    OpenAiFast,
    OpenAiPro,
    Custom,
}

public enum AiThinkingMode
{
    Auto,
    Fast,
    Deep,
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

    /// <summary>浮窗外缘层次风格，默认阴影与细边框并用</summary>
    public ToolbarSurfaceStyle ToolbarSurface { get; set; } = ToolbarSurfaceStyle.ShadowAndBorder;

    public bool FollowAccentColor { get; set; } = true;
    public double ToolbarCornerRadius { get; set; } = 5;
    public double ToolbarButtonSpacing { get; set; } = 0;
    public double ToolbarFontSize { get; set; } = 12;
    public int ToolbarMaxActionsPerRow { get; set; } = 6;

    /// <summary>浮窗默认透明度（鼠标未悬停时）。范围 0.3 ~ 1.0；1.0 表示完全不透明。
    /// 鼠标进入浮窗后会自动恢复为 1.0，离开后回到此值，避免遮挡背后内容</summary>
    public double ToolbarIdleOpacity { get; set; } = 1.0;
    public bool EnableToolbarKeyboardShortcuts { get; set; } = true;
    public bool EnableToolbarTabNavigation { get; set; } = true;
    public bool EnableToolbarNumberShortcuts { get; set; } = true;

    public SelectionPopupMode PopupMode { get; set; } = SelectionPopupMode.Immediate;
    public int PopupDelayMs { get; set; } = 200;
    public int HoverDelayMs { get; set; } = 300;
    public SelectionModifierKey RequiredModifier { get; set; } = SelectionModifierKey.Alt;

    /// <summary>Ctrl+A 全选时是否弹出浮窗。
    /// 默认 true：全选是有明确意图的键盘操作，保留弹窗便于后续动作；
    /// false：纯键盘用户可关闭以避免一切键盘事件触发浮窗</summary>
    public bool EnableSelectAllPopup { get; set; } = true;

    public string PauseHotKey { get; set; } = "Ctrl+Alt+P";
    public string ToolbarHotKey { get; set; } = "Ctrl+Alt+Space";

    // ================== 浮窗自动消失触发条件 ==================
    // 鼠标离开浮窗一段时间后自动关闭
    public bool DismissOnMouseLeave { get; set; } = true;
    public int DismissMouseLeaveDelayMs { get; set; } = 800;
    // 浮窗显示一段时间后自动关闭（毫秒）
    public bool DismissOnTimeout { get; set; } = false;
    public int DismissTimeoutMs { get; set; } = 5000;
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

    public bool AiEnabled { get; set; }

    /// <summary>是否已经把内置默认 AI 动作（AI 对话 / 修语法 / 润色 / 三句话总结）一次性写入过 actions。
    /// 仅在"用户首次启用 AI 的那一次保存"时为 false→true 切换并补齐动作；
    /// 之后即便用户手动删除这些动作，再次保存也不会被强制补回。
    /// 这样既保留"开箱即用"的引导，又把后续的动作集合主控权完全交给用户</summary>
    public bool AiDefaultActionsSeeded { get; set; }

    public AiProviderPreset AiProviderPreset { get; set; } = AiProviderPreset.DeepSeekV4Flash;
    public string AiBaseUrl { get; set; } = "https://api.deepseek.com";
    public string AiModel { get; set; } = "deepseek-v4-flash";
    public int AiTimeoutSeconds { get; set; } = 30;
    public string AiDefaultLanguage { get; set; } = "中文";
    public AiThinkingMode AiThinkingMode { get; set; } = AiThinkingMode.Auto;

    /// <summary>AI 单次最大输出 token 数。0=自动按思考强度选取（推荐）。
    /// DeepSeek V4 最大 384K；OpenAI o-series 也很宽松。思考模型 reasoning_tokens
    /// 与 visible content 共用同一额度上限，给"长思考链"留足空间避免 content 被挤空</summary>
    public int AiMaxOutputTokens { get; set; } = 0;

    public string AiDeepSeekApiKeyProtected { get; set; } = "";
    public string AiOpenAiApiKeyProtected { get; set; } = "";
    public string AiCustomApiKeyProtected { get; set; } = "";

    /// <summary>用户自建 Prompt 模板。内置模板不存这里，按需用 PromptTemplateLibrary.Builtin 合并</summary>
    public List<PromptTemplateDefinition> PromptTemplates { get; set; } = new();
}
