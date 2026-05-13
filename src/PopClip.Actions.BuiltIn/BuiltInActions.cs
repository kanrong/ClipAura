using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using PopClip.Core.Actions;
using PopClip.Core.Model;

namespace PopClip.Actions.BuiltIn;

/// <summary>所有内置动作的 Id 常量，JSON 配置通过 builtIn 字段引用</summary>
public static class BuiltInActionIds
{
    public const string Copy = "builtin.copy";
    /// <summary>统一搜索动作；具体引擎 URL 通过 ISettingsProvider 注入，运行时从设置读取</summary>
    public const string Search = "builtin.search";
    /// <summary>历史 ID，保留是为了兼容旧 actions.json；映射到 SearchAction 上</summary>
    public const string GoogleSearch = "builtin.search.google";
    /// <summary>历史 ID，同上</summary>
    public const string BingSearch = "builtin.search.bing";
    public const string Translate = "builtin.translate.bing";
    public const string ToUpper = "builtin.case.upper";
    public const string ToLower = "builtin.case.lower";
    public const string ToTitle = "builtin.case.title";
    public const string OpenUrl = "builtin.open.url";
    public const string Mailto = "builtin.open.mailto";
    public const string Calculate = "builtin.calculate";
    public const string WordCount = "builtin.wordcount";
    public const string AiChat = "builtin.ai.chat";
    public const string AiSummarize = "builtin.ai.summarize";
    public const string AiRewrite = "builtin.ai.rewrite";
    public const string AiTranslate = "builtin.ai.translate";
    public const string AiExplain = "builtin.ai.explain";
    public const string AiReply = "builtin.ai.reply";
    /// <summary>从浮动工具栏唤起"剪贴板历史"面板。运行时由 IClipboardHistoryLauncher 提供具体实现</summary>
    public const string ClipboardHistory = "builtin.clipboard.history";
}

internal abstract class BuiltInAction : IAction
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string IconKey { get; }
    public virtual bool CanRun(SelectionContext context) => !context.IsEmpty;
    public abstract Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct);
}

internal sealed class CopyAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.Copy;
    public override string Title => "复制";
    public override string IconKey => "Copy";
    public override Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        host.Clipboard.SetText(context.Text);
        return Task.CompletedTask;
    }
}

/// <summary>统一搜索动作。Title/IconKey 与最终 URL 都来源于 ISettingsProvider，
/// 用户可在设置中切换 Google / Bing / Baidu / 自定义模板</summary>
internal sealed class SearchAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.Search;
    public override string Title => "搜索";
    public override string IconKey => "Search";

    public override Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        var template = host.Settings.SearchUrlTemplate;
        if (string.IsNullOrWhiteSpace(template))
        {
            host.Log.Warn("search url template empty, fallback to google");
            template = "https://www.google.com/search?q={q}";
        }
        var url = template.Replace("{q}", WebUtility.UrlEncode(context.Text));
        host.UrlLauncher.Open(url);
        return Task.CompletedTask;
    }
}


internal sealed class TranslateAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.Translate;
    public override string Title => "翻译";
    public override string IconKey => "Translate";
    public override Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        host.UrlLauncher.Open("https://www.bing.com/translator?text=" + WebUtility.UrlEncode(context.Text));
        return Task.CompletedTask;
    }
}

internal sealed class ToUpperAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.ToUpper;
    public override string Title => "大写";
    public override string IconKey => "Upper";
    public override async Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
        => await host.Replacer.TryReplaceAsync(context, context.Text.ToUpperInvariant(), ct).ConfigureAwait(false);
}

internal sealed class ToLowerAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.ToLower;
    public override string Title => "小写";
    public override string IconKey => "Lower";
    public override async Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
        => await host.Replacer.TryReplaceAsync(context, context.Text.ToLowerInvariant(), ct).ConfigureAwait(false);
}

internal sealed class ToTitleAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.ToTitle;
    public override string Title => "标题大小写";
    public override string IconKey => "Title";

    public override async Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        var titled = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(context.Text.ToLowerInvariant());
        await host.Replacer.TryReplaceAsync(context, titled, ct).ConfigureAwait(false);
    }
}

internal sealed class OpenUrlAction : BuiltInAction
{
    private static readonly Regex UrlRegex = new(
        @"^(https?://|www\.)\S+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public override string Id => BuiltInActionIds.OpenUrl;
    public override string Title => "打开链接";
    public override string IconKey => "Url";

    public override bool CanRun(SelectionContext context)
        => !context.IsEmpty && UrlRegex.IsMatch(context.Text.Trim());

    public override Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        var url = context.Text.Trim();
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }
        host.UrlLauncher.Open(url);
        return Task.CompletedTask;
    }
}

internal sealed class MailtoAction : BuiltInAction
{
    private static readonly Regex MailRegex = new(
        @"^[^\s@]+@[^\s@]+\.[^\s@]+$",
        RegexOptions.Compiled);

    public override string Id => BuiltInActionIds.Mailto;
    public override string Title => "发送邮件";
    public override string IconKey => "Mail";

    public override bool CanRun(SelectionContext context)
        => !context.IsEmpty && MailRegex.IsMatch(context.Text.Trim());

    public override Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        host.UrlLauncher.Open("mailto:" + context.Text.Trim());
        return Task.CompletedTask;
    }
}

internal sealed class CalculateAction : BuiltInAction
{
    private static readonly Regex MathLike = new(
        @"^[\d\s\.\+\-\*\/\(\)%]+$",
        RegexOptions.Compiled);

    public override string Id => BuiltInActionIds.Calculate;
    public override string Title => "计算";
    public override string IconKey => "Calc";

    public override bool CanRun(SelectionContext context)
        => !context.IsEmpty && MathLike.IsMatch(context.Text.Trim()) && context.Text.IndexOfAny(new[] { '+', '-', '*', '/' }) >= 0;

    public override async Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        var expr = context.Text.Trim();
        try
        {
            var result = SimpleExpressionEvaluator.Evaluate(expr);
            var text = result.ToString("G15", CultureInfo.InvariantCulture);
            // 复制到剪贴板，避免直接替换破坏用户原文
            host.Clipboard.SetText(text);
            host.Notifier.Notify($"{expr} = {text}（已复制）");
            host.Log.Info("calc result copied", ("expr", expr), ("result", text));
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            host.Notifier.Notify($"计算失败：{ex.Message}");
            host.Log.Warn("calc failed", ("err", ex.Message));
        }
    }
}

/// <summary>从浮动工具栏点击后呼出剪贴板历史面板。
/// 不要求选区文本，运行时把当前 SelectionContext 透传给 launcher（用于"插入到选区"）</summary>
internal sealed class ClipboardHistoryAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.ClipboardHistory;
    public override string Title => "历史";
    public override string IconKey => "ClipboardHistory";

    public override bool CanRun(SelectionContext context) => true;

    public override Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        if (host.ClipboardHistory is null)
        {
            host.Notifier.Notify("剪贴板历史不可用");
            return Task.CompletedTask;
        }
        host.ClipboardHistory.Open(context);
        return Task.CompletedTask;
    }
}

internal sealed class WordCountAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.WordCount;
    public override string Title => "字数统计";
    public override string IconKey => "Count";

    public override Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        var text = context.Text;
        var chars = text.Length;
        var charsNoSpace = text.Count(c => !char.IsWhiteSpace(c));
        var words = text.Split(
            new[] { ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries).Length;
        var lines = text.Count(c => c == '\n') + (text.Length > 0 ? 1 : 0);
        var summary = $"字符 {chars}（去空白 {charsNoSpace}）/ 词 {words} / 行 {lines}";
        host.Clipboard.SetText(summary);
        host.Notifier.Notify(summary);
        host.Log.Info("wordcount", ("summary", summary));
        return Task.CompletedTask;
    }
}

internal sealed class AiTextAction : BuiltInAction
{
    private readonly AiTextActionKind _kind;
    private readonly string _id;
    private readonly string _title;
    private readonly string _iconKey;

    public AiTextAction(string id, string title, string iconKey, AiTextActionKind kind)
    {
        _id = id;
        _title = title;
        _iconKey = iconKey;
        _kind = kind;
    }

    public override string Id => _id;
    public override string Title => _title;
    public override string IconKey => _iconKey;

    public override bool CanRun(SelectionContext context)
        => !context.IsEmpty;

    public override async Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
        => await host.Ai.RunActionAsync(new AiTextActionRequest(_kind, _title), context, ct).ConfigureAwait(false);
}

/// <summary>由 actions.json 中 type:ai 动作派生的运行时动作。
/// 用户在 JSON 里写 prompt 与 outputMode，运行时由 IAiTextService 展开变量并按输出模式执行</summary>
public sealed class AiPromptAction : IAction
{
    private readonly ActionDescriptor _descriptor;
    private readonly AiOutputMode _outputMode;

    public AiPromptAction(ActionDescriptor descriptor)
    {
        _descriptor = descriptor;
        _outputMode = ParseOutputMode(descriptor.OutputMode);
    }

    public string Id => _descriptor.Id;
    public string Title => string.IsNullOrWhiteSpace(_descriptor.Title) ? "AI" : _descriptor.Title;
    public string IconKey => string.IsNullOrWhiteSpace(_descriptor.Icon) ? "Ai" : _descriptor.Icon;
    public AiOutputMode OutputMode => _outputMode;

    public bool CanRun(SelectionContext context)
        => !context.IsEmpty && !string.IsNullOrWhiteSpace(_descriptor.Prompt);

    public Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        var request = new AiPromptRequest(
            Title,
            _descriptor.Prompt ?? "",
            _descriptor.SystemPrompt,
            _outputMode);
        return host.Ai.RunPromptAsync(request, context, ct);
    }

    public static AiOutputMode ParseOutputMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return AiOutputMode.Chat;
        return raw.Trim().ToLowerInvariant() switch
        {
            "chat" => AiOutputMode.Chat,
            "replace" => AiOutputMode.Replace,
            "clipboard" => AiOutputMode.Clipboard,
            "inlinetoast" or "toast" or "inline" => AiOutputMode.InlineToast,
            _ => AiOutputMode.Chat,
        };
    }
}
