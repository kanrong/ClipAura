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
}

/// <summary>抽象"把剪贴板内容粘到当前位置"的能力。
/// HasClipboardText 用于 CanRun 的轻量判断（高频调用，不允许读取剪贴板正文）；
/// PasteAsync 才真正触发粘贴动作</summary>
public interface IPasteService
{
    /// <summary>剪贴板当前是否包含可粘贴的文本。
    /// 实现必须保持轻量（仅 ContainsText 判定），用于浮窗弹出前对内置粘贴动作做可见性过滤</summary>
    bool HasClipboardText { get; }

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
}

public enum AiTextActionKind
{
    Chat,
    Summarize,
    Rewrite,
    Translate,
    Explain,
    Reply,
    /// <summary>整理文本格式：合并多余空行，保留标题与正文之间的视觉分隔，不改动任何文字内容</summary>
    Tidy,
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

public sealed record AiTextActionRequest(AiTextActionKind Kind, string Title);

public sealed record AiConversationRequest(string Title, string ReferenceText, AiTextActionRequest? InitialAction = null);

/// <summary>由 actions.json 中的 type:ai 动作触发的运行时请求。
/// Prompt 已经包含变量占位符的原模板，由 IAiTextService 在调用前展开</summary>
public sealed record AiPromptRequest(
    string Title,
    string Prompt,
    string? SystemPrompt,
    AiOutputMode OutputMode);

public interface IAiTextService
{
    bool CanRun { get; }
    Task OpenConversationAsync(AiConversationRequest request, CancellationToken ct);
    Task RunActionAsync(AiTextActionRequest request, SelectionContext context, CancellationToken ct);

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
