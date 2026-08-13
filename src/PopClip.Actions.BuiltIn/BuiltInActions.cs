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
    /// <summary>剪贴板历史。划词浮窗不再展示；由全局热键 / 托盘 / 修饰键启动器唤起</summary>
    public const string ClipboardHistory = "builtin.clipboard.history";

    // 文本类型智能动作链：CanRun 自行嗅探内容类型；默认启用状态由 App 侧默认动作工厂和用户配置决定
    public const string JsonFormat = "builtin.text.json.format";
    public const string JsonToYaml = "builtin.text.json.toyaml";
    public const string Color = "builtin.text.color";
    public const string Timestamp = "builtin.text.timestamp";
    public const string PathOpen = "builtin.text.path";
    public const string MarkdownTableToCsv = "builtin.text.mdtable.tocsv";
    public const string CsvToMarkdown = "builtin.text.csv.tomd";
    public const string TsvToCsv = "builtin.text.tsv.tocsv";
    public const string TsvToMarkdown = "builtin.text.tsv.tomd";
    public const string WordLookup = "builtin.text.word.lookup";
    public const string VocabularyAnalyze = "builtin.text.vocabulary.analyze";
}

internal abstract class BuiltInAction : IAction
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string IconKey { get; }
    public virtual bool CanRun(SelectionContext context) => !context.IsEmpty;
    public abstract Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct);
}

internal static class EnglishLetterProbe
{
    public static bool ContainsAsciiLetter(string text)
        => text.Any(IsAsciiLetter);

    public static bool StartsWithAsciiLetterAfterWhitespace(string text)
    {
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            return IsAsciiLetter(c);
        }
        return false;
    }

    private static bool IsAsciiLetter(char c)
        => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}

/// <summary>内置"复制"动作：等价于让用户按一次 Ctrl+C。
/// 按选区来源分三条路径，核心诉求是"既保留富文本格式，又不给终端发会触发 ^C 中断的多余 Ctrl+C"：
///
/// 1) 剪贴板兜底来源（终端 / Firefox 等 UIA 取不到文本的应用）：采集阶段那次 Ctrl+C 已让源应用把
///    多格式数据写进过剪贴板，并被一并捕获成快照。这里直接回写快照——终端快照里只有纯文本（回写纯文本、
///    且不再发 Ctrl+C），Firefox 快照含 CF_HTML（回写即保住格式）。快照不可用时退回 SetText，仍不发 Ctrl+C。
/// 2) UIA 来源：选区仍存活在真实控件里，转交系统 Ctrl+C 让源应用主动写 CF_UNICODETEXT + CF_HTML / CF_RTF，
///    在 Word/Outlook 等粘贴时保留格式；失败再退回 SetText。
/// 3) OCR 来源：没有真实选区，模拟 Ctrl+C 会误复制截图时前台窗口的内容，直接写采集文本。</summary>
internal sealed class CopyAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.Copy;
    public override string Title => "复制";
    public override string IconKey => "Copy";

    public override async Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        if (context.Source == AcquisitionSource.ClipboardFallback)
        {
            if (host.Paste.TryCopyCapturedSelection(context)) return;
            // 快照不可用（极少见：被新一次选区采集覆盖等）→ 用纯文本兜底，绝不退回 Ctrl+C（保护终端）
            host.Clipboard.SetText(context.Text);
            return;
        }
        if (context.Foreground.Hwnd != 0 && context.Source != AcquisitionSource.Ocr)
        {
            var ok = await host.Paste.CopyAsync(context, ct).ConfigureAwait(false);
            if (ok) return;
            host.Log.Warn("copy via Ctrl+C failed, fallback to plain SetText");
        }
        // 兜底：OCR 来源、拿不到目标 HWND，或系统 Ctrl+C 失败时，用采集阶段已取到的文本保底写入（纯文本）
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
        host.UrlLauncher.Open(url, context.Foreground);
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
                OutputMode: AiOutputMode.Bubble);
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
            OutputMode: AiOutputMode.Bubble);
        return host.Ai.RunPromptAsync(request, context, ct);
    }
}

internal sealed class ToUpperAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.ToUpper;
    public override string Title => "大写";
    public override string IconKey => "Upper";
    public override bool CanRun(SelectionContext context)
        => !context.IsEmpty && EnglishLetterProbe.ContainsAsciiLetter(context.Text);
    public override async Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
        => await host.Replacer.TryReplaceAsync(context, context.Text.ToUpperInvariant(), ct).ConfigureAwait(false);
}

internal sealed class ToLowerAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.ToLower;
    public override string Title => "小写";
    public override string IconKey => "Lower";
    public override bool CanRun(SelectionContext context)
        => !context.IsEmpty && EnglishLetterProbe.ContainsAsciiLetter(context.Text);
    public override async Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
        => await host.Replacer.TryReplaceAsync(context, context.Text.ToLowerInvariant(), ct).ConfigureAwait(false);
}

internal sealed class ToTitleAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.ToTitle;
    public override string Title => "标题大小写";
    public override string IconKey => "Title";
    public override bool CanRun(SelectionContext context)
        => !context.IsEmpty && EnglishLetterProbe.StartsWithAsciiLetterAfterWhitespace(context.Text);

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

/// <summary>呼出剪贴板历史面板。划词浮窗不再展示此动作（CanRun 恒 false）；
/// 入口是全局热键、托盘菜单、修饰键快速启动器。若旧 actions.json 仍启用该项，
/// 目录会加载但选区浮窗会过滤掉。</summary>
internal sealed class ClipboardHistoryAction : BuiltInAction
{
    public override string Id => BuiltInActionIds.ClipboardHistory;
    public override string Title => "历史";
    public override string IconKey => "ClipboardHistory";

    public override bool CanRun(SelectionContext context) => false;

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
            "toast" => AiOutputMode.Toast,
            "bubble" => AiOutputMode.Bubble,
            _ => AiOutputMode.Chat,
        };
    }
}
