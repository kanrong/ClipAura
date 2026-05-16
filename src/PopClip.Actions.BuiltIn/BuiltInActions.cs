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
    /// <summary>把剪贴板内容粘贴到当前选区/光标位置。
    /// 与快捷点击触发的剪贴板工具条不同，这是个普通可见动作：
    /// 浮窗里同时有"复制 / 粘贴"两个按钮，选择文本后既可以复制也可以直接替换</summary>
    public const string Paste = "builtin.paste";
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
    /// <summary>仅保留 AI 对话入口；其它 AI 文本操作统一改走 actions.json 中 type:ai 的 prompt 模板</summary>
    public const string AiChat = "builtin.ai.chat";
    /// <summary>"AI 解释"内置动作。仅在 AI 已启用并配置 API Key 时显示，
    /// 结果走流式气泡呈现，让用户在不离开当前应用的情况下读懂选区文本</summary>
    public const string AiExplain = "builtin.ai.explain";
    /// <summary>从浮动工具栏唤起"剪贴板历史"面板。运行时由 IClipboardHistoryLauncher 提供具体实现</summary>
    public const string ClipboardHistory = "builtin.clipboard.history";

    // 文本类型智能动作链：CanRun 自行嗅探内容类型；默认在 actions.json 中 enabled=false，
    // 老用户浮窗不被突然撑大，新用户按需在设置启用
    public const string JsonFormat = "builtin.text.json.format";
    public const string JsonToYaml = "builtin.text.json.toyaml";
    public const string Color = "builtin.text.color";
    public const string Timestamp = "builtin.text.timestamp";
    public const string PathOpen = "builtin.text.path";
    public const string MarkdownTableToCsv = "builtin.text.mdtable.tocsv";
    public const string CsvToMarkdown = "builtin.text.csv.tomd";
    public const string TsvToCsv = "builtin.text.tsv.tocsv";
    public const string TsvToMarkdown = "builtin.text.tsv.tomd";
}

internal abstract class BuiltInAction : IAction
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string IconKey { get; }
    public virtual bool CanRun(SelectionContext context) => !context.IsEmpty;
    public abstract Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct);
}

/// <summary>内置"复制"动作：等价于让用户按一次 Ctrl+C。
/// 关键设计：刻意不走 host.Clipboard.SetText(context.Text)。
/// 那条路径只能写入纯文本格式（CF_UNICODETEXT），会丢失源应用本来一并写入的 CF_HTML / CF_RTF
/// 等富文本格式，进而在 Word/Outlook 等富文本编辑器粘贴时出现"格式丢失/方块乱码"。
/// 改为转交给系统 Ctrl+C，让源应用按自身策略把多格式数据写入剪贴板，行为与用户手动 Ctrl+C 完全一致。
/// 失败时（targetHwnd 为 0 或 SendInput 抛错）退回到 SetText 兜底，至少保住纯文本可粘贴</summary>
internal sealed class CopyAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.Copy;
    public override string Title => "复制";
    public override string IconKey => "Copy";

    public override async Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        if (context.Foreground.Hwnd != 0)
        {
            var ok = await host.Paste.CopyAsync(context, ct).ConfigureAwait(false);
            if (ok) return;
            host.Log.Warn("copy via Ctrl+C failed, fallback to plain SetText");
        }
        // 兜底：拿不到目标 HWND 或键盘模拟失败时，保底把纯文本塞进剪贴板，
        // 让用户至少能 Ctrl+V 出文字（哪怕没有格式）
        host.Clipboard.SetText(context.Text);
    }
}

/// <summary>内置"粘贴"动作：把当前剪贴板内容粘到选区/光标位置。
/// 选区是否为空不影响可见性（空选区即"原地光标处粘贴"），仅要求剪贴板有文本。
/// CanRun 通过持有的 IPasteService 做 HasClipboardText 轻量探测（STA 上仅 ContainsText，不读取正文），
/// 这样剪贴板为空时按钮不出现；浮窗弹出阶段的额外开销控制在亚毫秒级</summary>
internal sealed class PasteAction : BuiltInAction
{
    private readonly IPasteService _paste;

    public PasteAction(IPasteService paste) => _paste = paste;

    public override string Id => BuiltInActionIds.Paste;
    public override string Title => "粘贴";
    public override string IconKey => "Paste";

    public override bool CanRun(SelectionContext context) => _paste.HasClipboardText;

    public override async Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        if (!host.Paste.HasClipboardText)
        {
            host.Notifier.Notify("剪贴板没有可粘贴的文本");
            return;
        }
        var ok = await host.Paste.PasteAsync(context, ct).ConfigureAwait(false);
        if (!ok) host.Notifier.Notify("粘贴失败");
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


/// <summary>"翻译"内置动作。
/// 双路径设计：
/// - AI 已启用、配置了 API Key 且用户没在设置中关掉 TranslateInlineWhenAiEnabled：
///   走内联 AI 气泡，把译文流式渲染到浮窗下方，用户可"插入/替换/复制/打开完整对话"。
/// - 其它任何情况（包括未配 AI、用户主动关掉内联）：维持旧行为，打开 Bing 网页翻译。
/// 这样老用户的"点翻译开网页"工作流不会被破坏，配了 AI 的用户自动升级体验</summary>
internal sealed class TranslateAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.Translate;
    public override string Title => "翻译";
    public override string IconKey => "Translate";

    public override Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        var useInline = host.Ai.CanRun && host.Settings.TranslateInlineWhenAiEnabled;
        if (useInline)
        {
            var request = new AiPromptRequest(
                Title: "翻译",
                Prompt: "把下面的文本翻译为{language}。只输出译文，不要任何解释或附加说明：\n\n{text}",
                SystemPrompt: null,
                OutputMode: AiOutputMode.InlineToast,
                UseInteractiveBubble: true);
            return host.Ai.RunPromptAsync(request, context, ct);
        }

        host.UrlLauncher.Open("https://www.bing.com/translator?text=" + WebUtility.UrlEncode(context.Text));
        return Task.CompletedTask;
    }
}

/// <summary>"AI 解释"内置动作。
/// 不存在合理的"非 AI"兜底（打开词典网页对长句、代码片段都不合适），所以 CanRun 直接要求 AI 可用；
/// 用户在设置里关掉 ExplainActionEnabled 时按钮也不出现，给"只想要原生功能"的用户彻底退出选项</summary>
internal sealed class AiExplainAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.AiExplain;
    public override string Title => "解释";
    public override string IconKey => "AiExplain";

    public override bool CanRun(SelectionContext context)
        => !context.IsEmpty;

    public override Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        // 与 PromptTemplateLibrary 中 tpl.explain 一致；保留在代码里独立维护，
        // 避免 Library 的措辞调整反向影响这个内置动作的语义稳定性
        var request = new AiPromptRequest(
            Title: "解释",
            Prompt: "用{language}解释下面的文本，面向不熟悉背景的人，先给一句话结论，再补充必要细节：\n\n{text}",
            SystemPrompt: null,
            OutputMode: AiOutputMode.InlineToast,
            UseInteractiveBubble: true);
        return host.Ai.RunPromptAsync(request, context, ct);
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
            SmartOutput.Publish(host, context, Title,
                primaryText: text,
                displayText: $"{expr} = {text}",
                copyToast: $"{expr} = {text}（已复制）");
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
        SmartOutput.Publish(host, context, Title,
            primaryText: summary,
            displayText: $"字符：{chars}\n字符（去空白）：{charsNoSpace}\n词：{words}\n行：{lines}",
            copyToast: summary);
        host.Log.Info("wordcount", ("summary", summary));
        return Task.CompletedTask;
    }
}

/// <summary>"AI 对话"入口动作：把选中文本作为参考送入 AI 对话窗口。
/// 这是唯一保留的硬编码 AI 动作；其它 AI 文本操作（总结/改写/解释/翻译/回复 等）统一走 type:ai 的 prompt 模板</summary>
internal sealed class AiChatAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.AiChat;
    public override string Title => "AI 对话";
    public override string IconKey => "AiChat";

    public override Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
        => host.Ai.OpenConversationAsync(new AiConversationRequest(Title, context.Text), ct);
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
