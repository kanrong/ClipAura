using PopClip.Core.Session;
using PopClip.Core.Logging;

namespace PopClip.App.Config;

/// <summary>浮窗按钮内容呈现方式</summary>
public enum ToolbarDisplayMode
{
    IconOnly,
    IconAndText,
    TextOnly,
}

/// <summary>浮窗按钮的分行布局策略。
/// 多行模式只在"实际有 ≥2 组可见动作"时才会真的换行：
/// 只有一类动作可见时退化为 Single，避免把"翻译"或"格式化 JSON"单独丢一个孤行</summary>
public enum ToolbarLayoutMode
{
    /// <summary>单行紧凑，完全按用户在动作页配置的顺序排（默认）</summary>
    Single,
    /// <summary>智能动作单独成行：在 Single 基础上把 Smart 一组挑到第二行</summary>
    SmartOnSeparateRow,
    /// <summary>按 基础 / 智能 / AI 三组各占一行</summary>
    GroupRows,
}

/// <summary>浮窗颜色主题。
/// Auto/Light/Dark 为基础三档，跟随系统或强制明暗；
/// 之后的枚举值为彩色预设，仅作用于浮窗，不读系统明暗，避免预设被反向覆盖。
/// 顺序关系到序列化兼容：新预设只能追加在末尾</summary>
public enum ToolbarThemeMode
{
    Auto,
    Light,
    Dark,
    QingciBlue,
    DeepInkGreen,
    MistyGreen,
    SunsetRose,
    DistantMountain,
    Sandalwood,
}

/// <summary>浮窗外缘层次风格</summary>
public enum ToolbarSurfaceStyle
{
    Shadow,
    Border,
    ShadowAndBorder,
}

/// <summary>浮窗内边距密度档位。
/// 只用一档"按钮间距"无法同时控制按钮内 padding 和按钮间外 margin，
/// 这里把三档（紧凑/标准/宽松）抽出来统一调度按钮 padding 与 ItemsPanel margin，
/// 让用户一次选定整体疏密风格</summary>
public enum ToolbarDensity
{
    Compact,
    Standard,
    Comfortable,
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
    public LogLevel LogLevel { get; set; } = LogLevel.Debug;

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

    /// <summary>浮窗整体密度档位：紧凑 / 标准（默认）/ 宽松。
    /// 影响按钮内边距与 ItemsPanel 外边距，不影响圆角与字号；
    /// 用户更倾向"一键切换风格"而不是逐项微调时使用</summary>
    public ToolbarDensity ToolbarDensity { get; set; } = ToolbarDensity.Standard;

    /// <summary>设置窗口与全局 UI 字体族；空串表示使用内置默认中文字体回退链。
    /// 用户可在外观设置里挑选系统已安装字体（如 "JetBrains Mono"、"霞鹜文楷" 等）</summary>
    public string UiFontFamily { get; set; } = "";

    /// <summary>浮窗专用字体族；空串=继承 UiFontFamily，再空=默认中文字体回退链。
    /// 单独分一项是因为浮窗按钮空间窄，用户常希望换成更紧凑或更宽松的字体</summary>
    public string ToolbarFontFamily { get; set; } = "";

    /// <summary>浮窗默认透明度（鼠标未悬停时）。范围 0.3 ~ 1.0；1.0 表示完全不透明。
    /// 鼠标进入浮窗后会自动恢复为 1.0，离开后回到此值，避免遮挡背后内容</summary>
    public double ToolbarIdleOpacity { get; set; } = 1.0;
    public bool EnableToolbarKeyboardShortcuts { get; set; } = true;
    public bool EnableToolbarTabNavigation { get; set; } = true;
    public bool EnableToolbarNumberShortcuts { get; set; } = true;

    /// <summary>浮窗按钮的分行策略。Single=完全按用户在动作页配置的顺序单行展示；
    /// SmartOnSeparateRow=智能动作单独成行；GroupRows=按 基础/智能/AI 三组各占一行。
    /// 后两种只在"实际有 ≥2 组可见动作"时才换行，单一类别仍走紧凑单行</summary>
    public ToolbarLayoutMode ToolbarLayoutMode { get; set; } = ToolbarLayoutMode.Single;

    public SelectionPopupMode PopupMode { get; set; } = SelectionPopupMode.Immediate;
    public int PopupDelayMs { get; set; } = 200;
    public int HoverDelayMs { get; set; } = 300;
    public SelectionModifierKey RequiredModifier { get; set; } = SelectionModifierKey.Alt;
    public SelectionModifierKey QuickClickModifier { get; set; } = SelectionModifierKey.Ctrl;

    /// <summary>Ctrl+A 全选时是否弹出浮窗。
    /// 默认 true：全选是有明确意图的键盘操作，保留弹窗便于后续动作；
    /// false：纯键盘用户可关闭以避免一切键盘事件触发浮窗</summary>
    public bool EnableSelectAllPopup { get; set; } = true;

    public string PauseHotKey { get; set; } = "Ctrl+Alt+P";
    public string ToolbarHotKey { get; set; } = "Ctrl+Alt+Space";

    /// <summary>区域 OCR 截选热键。按下后弹出全屏蒙层让用户拉框，
    /// 截图区域被 RapidOcrNet (PP-OCRv5 ONNX) 引擎识别后走与正常选区相同的浮窗 + 动作链路</summary>
    public string OcrHotKey { get; set; } = "Ctrl+Alt+Shift+O";

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

    /// <summary>启用 AI 后，浮窗"翻译"按钮是否走内联 AI 气泡。
    /// 关闭则始终打开 Bing 网页翻译，保留旧行为兜底；
    /// AI 未启用或未配置 API Key 时本开关无效，永远走网页翻译</summary>
    public bool TranslateInlineWhenAiEnabled { get; set; } = true;

    /// <summary>是否启用浮窗"AI 解释"按钮。
    /// 仅在 AI 已启用且配置了 API Key 时按钮才会出现；
    /// 该开关存在的意义是让习惯精简浮窗的用户能彻底关掉这个动作</summary>
    public bool ExplainActionEnabled { get; set; } = true;

    /// <summary>已经被"内置动作 seed 机制"补齐过的 builtin id 集合。
    /// 用途：让新版本新增的内置动作（如智能动作、AI 解释）能在老用户的 actions.json 中
    /// 以 enabled=false 形式自动出现一次。
    /// 一旦某 id 进入此集合，无论 actions.json 中是否被用户删除，都视为"用户已知晓"，
    /// 下次启动不会再次补齐，保证用户的删除行为是终态</summary>
    public HashSet<string> SeededBuiltInIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>用户自建 Prompt 模板。内置模板不存这里，按需用 PromptTemplateLibrary.Builtin 合并</summary>
    public List<PromptTemplateDefinition> PromptTemplates { get; set; } = new();
}
