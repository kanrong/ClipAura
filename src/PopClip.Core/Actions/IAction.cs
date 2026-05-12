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
    Logging.ILog Log { get; }
}

/// <summary>对动作开放的只读设置视图。Core 层与 UI 设置解耦，便于测试与替换</summary>
public interface ISettingsProvider
{
    /// <summary>当前搜索引擎名称（仅展示用）</summary>
    string SearchEngineName { get; }

    /// <summary>当前搜索 URL 模板，必须包含 {q} 占位符；运行时会用 UrlEncode 后的选区文本替换</summary>
    string SearchUrlTemplate { get; }
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
