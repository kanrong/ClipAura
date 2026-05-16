using PopClip.Core.Model;

namespace PopClip.Core.Actions;

/// <summary>动作统一接口，MVP 仅内置，v2 经由外部 manifest 加载相同形状的实现</summary>
public interface IAction
{
    string Id { get; }
    string Title { get; }
    string IconKey { get; }

    /// <summary>静态可见性判断：当前选区是否值得让此动作出现在工具栏</summary>
    bool CanRun(SelectionContext context);

    /// <summary>执行动作。如果会改写选区文本，应通过 ITextReplacer 写回</summary>
    Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct);
}

/// <summary>动作运行时宿主能力。隔离平台细节，便于测试</summary>
public interface IActionHost
{
    ITextReplacer Replacer { get; }
    IUrlLauncher UrlLauncher { get; }
    IClipboardWriter Clipboard { get; }
    INotificationSink Notifier { get; }
    ISettingsProvider Settings { get; }
    IAiTextService Ai { get; }
    /// <summary>粘贴能力（把剪贴板内容写回当前选区/光标位置）。
    /// 内置粘贴动作通过它实现，外部脚本/扩展动作也可使用</summary>
    IPasteService Paste { get; }
    /// <summary>剪贴板历史唤起器。可能为 null（如轻量化测试场景）</summary>
    IClipboardHistoryLauncher? ClipboardHistory { get; }
    Logging.ILog Log { get; }
    /// <summary>当前正在执行的动作对应的 ActionDescriptor，可能为 null。
    /// 智能动作通过 Descriptor.OutputMode 决定输出落点（仅复制 / 仅气泡 / 复制+气泡 / 独立对话窗口）。
    /// 单例 ActionHost 自身永远返回 null，运行时由 ScopedActionHost 通过包装注入</summary>
    ActionDescriptor? Descriptor { get; }
    /// <summary>把"独立结果窗口"模式的请求传给宿主：宿主用 AiResultWindow 同款窗口展示结果文本。
    /// 仅 OutputMode=Dialog 时调用；非 GUI 场景（测试 / 命令行）可为 null</summary>
    IResultDialogPresenter? ResultDialog { get; }
    /// <summary>展示 AiBubbleWindow 同款气泡（流式 / 静态）；
    /// 智能动作用 ShowStaticAsync 把已就绪的整段文本直接送进气泡呈现</summary>
    IInlineBubblePresenter? Bubble { get; }
}

/// <summary>"独立结果窗口"展示能力。
/// 智能动作 OutputMode=Dialog 时，宿主用 AiResultWindow 同款窗口承载结果，
/// 支持滚动多行、复制、关闭。窗口标题来自动作 Title，正文是动作产出的纯文本</summary>
public interface IResultDialogPresenter
{
    void Show(string title, string referenceText, string resultText);
}

/// <summary>浮窗下方的"轻量气泡"展示能力。
/// 智能动作 OutputMode=Bubble / CopyAndBubble 时调用 ShowStatic 把整段文本一次性投入气泡，
/// 不走流式；气泡会负责锚点定位、滚动条、复制按钮等通用交互</summary>
public interface IInlineBubblePresenter
{
    void ShowStatic(string title, string text, bool canReplace, System.Func<string, System.Threading.Tasks.Task>? onReplace = null);
}

/// <summary>抽象"代用户对目标窗口模拟剪贴板键盘动作"的能力（Ctrl+C / Ctrl+V）。
/// 这两个动作都让源应用/目标应用自己去处理剪贴板，
/// 借此保留 HTML / RTF / 图片 / 文件清单 等系统按键时本就会带的多格式数据，
/// 不允许用 Clipboard.SetText 单独写纯文本来代替——那会让 Word/Outlook 等富文本应用粘贴出格式丢失/方块乱码。
/// HasClipboardText 用于 CanRun 的轻量判断（高频调用，不允许读取剪贴板正文）</summary>
public interface IPasteService
{
    /// <summary>剪贴板当前是否包含可粘贴的文本。
    /// 实现必须保持轻量（仅 ContainsText 判定），用于浮窗弹出前对内置粘贴动作做可见性过滤</summary>
    bool HasClipboardText { get; }

    /// <summary>把 context.Foreground 指向窗口的当前选区拷到剪贴板（等价于用户按 Ctrl+C）。
    /// 比"我们自己 Clipboard.SetText(selectionText)"重要的差异在于：
    /// 源应用会被叫醒主动写多格式数据，剪贴板里同时含 CF_UNICODETEXT / CF_HTML / CF_RTF 等，
    /// 之后在富文本编辑器粘贴时保留原格式。
    /// 返回 false 表示底层 SendInput/SetForegroundWindow 失败</summary>
    Task<bool> CopyAsync(SelectionContext context, CancellationToken ct);

    /// <summary>把剪贴板内容粘贴到 context.Foreground 指向的窗口。
    /// 调用方应自行先关闭浮窗并恢复目标窗口焦点，本方法只负责模拟 Ctrl+V。
    /// 返回 false 表示粘贴失败（例如目标 HWND 已失效）</summary>
    Task<bool> PasteAsync(SelectionContext context, CancellationToken ct);
}

/// <summary>剪贴板历史面板的呼出接口，由 UI 层注入实现</summary>
public interface IClipboardHistoryLauncher
{
    void Open(SelectionContext? anchorContext = null);
}

/// <summary>对动作开放的只读设置视图。Core 层与 UI 设置解耦，便于测试与替换</summary>
public interface ISettingsProvider
{
    /// <summary>当前搜索引擎名称（仅展示用）</summary>
    string SearchEngineName { get; }

    /// <summary>当前搜索 URL 模板，必须包含 {q} 占位符；运行时会用 UrlEncode 后的选区文本替换</summary>
    string SearchUrlTemplate { get; }

    bool AiEnabled { get; }
    string AiProviderPreset { get; }
    string AiBaseUrl { get; }
    string AiModel { get; }
    int AiTimeoutSeconds { get; }
    string AiDefaultLanguage { get; }
    string AiThinkingMode { get; }
    bool HasAiApiKey { get; }

    /// <summary>启用 AI 时是否把"翻译"动作改走内联气泡而非打开网页翻译。
    /// 由 TranslateAction 在运行时读取，决定走内联或网页两条路径之一</summary>
    bool TranslateInlineWhenAiEnabled { get; }

    /// <summary>是否启用浮窗"AI 解释"按钮。
    /// ExplainAction 把它纳入 CanRun 判断，关闭后该按钮在浮窗中不出现</summary>
    bool ExplainActionEnabled { get; }
}

/// <summary>AI 动作的输出落点。
/// Chat=进入对话窗口（默认）；Replace=不弹窗直接替换选区；
/// Clipboard=不弹窗只写剪贴板；InlineToast=不弹窗只显示在浮窗 toast</summary>
public enum AiOutputMode
{
    Chat,
    Replace,
    Clipboard,
    InlineToast,
}

public sealed record AiConversationRequest(string Title, string ReferenceText);

/// <summary>由 actions.json 中的 type:ai 动作触发的运行时请求。
/// Prompt 已经包含变量占位符的原模板，由 IAiTextService 在调用前展开。
/// UseInteractiveBubble 仅对 OutputMode=InlineToast 生效：
/// true 时改用流式可交互气泡窗呈现（带插入/替换/复制/打开完整对话按钮），
/// false 时维持旧的"一次性 toast"行为（结果转 preview 投到现有 toast 上）</summary>
public sealed record AiPromptRequest(
    string Title,
    string Prompt,
    string? SystemPrompt,
    AiOutputMode OutputMode,
    bool UseInteractiveBubble = false);

public interface IAiTextService
{
    bool CanRun { get; }
    /// <summary>打开 AI 对话窗，把选区文本作为参考。用于"AI 对话"入口动作</summary>
    Task OpenConversationAsync(AiConversationRequest request, CancellationToken ct);

    /// <summary>执行自定义 prompt 动作。根据 OutputMode 决定是否弹窗</summary>
    Task RunPromptAsync(AiPromptRequest request, SelectionContext context, CancellationToken ct);
}

/// <summary>向用户展示一次性短信息（如计算结果、字数统计）。
/// 实现可以是托盘气球通知、屏幕角落 toast 等，对动作层透明</summary>
public interface INotificationSink
{
    void Notify(string text);
}

public interface ITextReplacer
{
    Task<bool> TryReplaceAsync(SelectionContext context, string newText, CancellationToken ct);
}

public interface IUrlLauncher
{
    void Open(string url);
}

public interface IClipboardWriter
{
    void SetText(string text);
}
