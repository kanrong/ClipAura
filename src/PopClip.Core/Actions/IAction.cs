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
    Logging.ILog Log { get; }
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
