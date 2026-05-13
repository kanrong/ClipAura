using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using PopClip.Core.Actions;
using PopClip.Core.Logging;
using PopClip.Core.Model;

namespace PopClip.Actions.BuiltIn;

/// <summary>把 JSON 配置展开为运行时动作集合，并提供按选区过滤的 GetVisible</summary>
public sealed class ActionCatalog
{
    private readonly ILog _log;
    private readonly Dictionary<string, IAction> _registry;
    private List<ResolvedAction> _ordered = new();

    public ActionCatalog(ILog log)
    {
        _log = log;
        // 旧 GoogleSearch / BingSearch ID 全部别名到统一 SearchAction，兼容历史 actions.json
        var search = new SearchAction();
        _registry = new Dictionary<string, IAction>(StringComparer.OrdinalIgnoreCase)
        {
            [BuiltInActionIds.Copy] = new CopyAction(),
            [BuiltInActionIds.Search] = search,
            [BuiltInActionIds.GoogleSearch] = search,
            [BuiltInActionIds.BingSearch] = search,
            [BuiltInActionIds.Translate] = new TranslateAction(),
            [BuiltInActionIds.ToUpper] = new ToUpperAction(),
            [BuiltInActionIds.ToLower] = new ToLowerAction(),
            [BuiltInActionIds.ToTitle] = new ToTitleAction(),
            [BuiltInActionIds.OpenUrl] = new OpenUrlAction(),
            [BuiltInActionIds.Mailto] = new MailtoAction(),
            [BuiltInActionIds.Calculate] = new CalculateAction(),
            [BuiltInActionIds.WordCount] = new WordCountAction(),
            [BuiltInActionIds.ClipboardHistory] = new ClipboardHistoryAction(),
            [BuiltInActionIds.AiChat] = new AiTextAction(BuiltInActionIds.AiChat, "AI 对话", "AiChat", AiTextActionKind.Chat),
            [BuiltInActionIds.AiSummarize] = new AiTextAction(BuiltInActionIds.AiSummarize, "AI 总结", "AiSummary", AiTextActionKind.Summarize),
            [BuiltInActionIds.AiRewrite] = new AiTextAction(BuiltInActionIds.AiRewrite, "AI 改写", "AiRewrite", AiTextActionKind.Rewrite),
            [BuiltInActionIds.AiTranslate] = new AiTextAction(BuiltInActionIds.AiTranslate, "AI 翻译", "AiTranslate", AiTextActionKind.Translate),
            [BuiltInActionIds.AiExplain] = new AiTextAction(BuiltInActionIds.AiExplain, "AI 解释", "AiExplain", AiTextActionKind.Explain),
            [BuiltInActionIds.AiReply] = new AiTextAction(BuiltInActionIds.AiReply, "AI 回复", "AiReply", AiTextActionKind.Reply),
            [BuiltInActionIds.AiTidy] = new AiTextAction(BuiltInActionIds.AiTidy, "AI 整理", "AiTidy", AiTextActionKind.Tidy),
        };
    }

    public void Load(ActionsConfig config)
    {
        var ordered = new List<ResolvedAction>(config.Actions.Count);
        foreach (var d in config.Actions)
        {
            if (!d.Enabled) continue;
            var action = ResolveAction(d);
            if (action is null) continue;

            Regex? matcher = null;
            if (!string.IsNullOrEmpty(d.MatchRegex))
            {
                try { matcher = new Regex(d.MatchRegex, RegexOptions.Compiled | RegexOptions.CultureInvariant); }
                catch (Exception ex) { _log.Warn("invalid regex", ("id", d.Id), ("err", ex.Message)); }
            }
            ordered.Add(new ResolvedAction(d, action, matcher));
        }
        _ordered = ordered;
        _log.Info("actions loaded", ("count", _ordered.Count));
    }

    private IAction? ResolveAction(ActionDescriptor descriptor)
    {
        if (string.Equals(descriptor.Type, "builtin", StringComparison.OrdinalIgnoreCase))
        {
            if (descriptor.BuiltIn is null || !_registry.TryGetValue(descriptor.BuiltIn, out var action))
            {
                _log.Warn("builtin not found", ("id", descriptor.Id), ("ref", descriptor.BuiltIn ?? "(null)"));
                return null;
            }
            return action;
        }

        if (string.Equals(descriptor.Type, "url-template", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(descriptor.UrlTemplate))
            {
                _log.Warn("url-template action missing template", ("id", descriptor.Id));
                return null;
            }
            return new UrlTemplateAction(descriptor);
        }

        if (string.Equals(descriptor.Type, "script", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(descriptor.ScriptPath))
            {
                _log.Warn("script action missing path", ("id", descriptor.Id));
                return null;
            }
            return new ScriptAction(descriptor);
        }

        if (string.Equals(descriptor.Type, "ai", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(descriptor.Prompt))
            {
                _log.Warn("ai action missing prompt", ("id", descriptor.Id));
                return null;
            }
            return new AiPromptAction(descriptor);
        }

        _log.Warn("unknown action type, skipped", ("id", descriptor.Id), ("type", descriptor.Type));
        return null;
    }

    /// <summary>未提供配置时使用的默认动作顺序，等价于"全部启用且无 matchRegex"</summary>
    public void LoadDefaults()
    {
        var cfg = new ActionsConfig
        {
            Actions = new List<ActionDescriptor>
            {
                new() { Id = "copy", Type = "builtin", BuiltIn = BuiltInActionIds.Copy, Title = "复制" },
                new() { Id = "open-url", Type = "builtin", BuiltIn = BuiltInActionIds.OpenUrl, Title = "打开链接" },
                new() { Id = "mailto", Type = "builtin", BuiltIn = BuiltInActionIds.Mailto, Title = "发送邮件" },
                new() { Id = "search", Type = "builtin", BuiltIn = BuiltInActionIds.Search, Title = "搜索" },
                new() { Id = "translate", Type = "builtin", BuiltIn = BuiltInActionIds.Translate, Title = "翻译" },
                new() { Id = "upper", Type = "builtin", BuiltIn = BuiltInActionIds.ToUpper, Title = "大写" },
                new() { Id = "lower", Type = "builtin", BuiltIn = BuiltInActionIds.ToLower, Title = "小写" },
                new() { Id = "title", Type = "builtin", BuiltIn = BuiltInActionIds.ToTitle, Title = "Title" },
                new() { Id = "calc", Type = "builtin", BuiltIn = BuiltInActionIds.Calculate, Title = "计算" },
                new() { Id = "wc", Type = "builtin", BuiltIn = BuiltInActionIds.WordCount, Title = "字数" },
            },
        };
        Load(cfg);
    }

    public IReadOnlyList<ResolvedAction> GetVisible(SelectionContext context)
    {
        var list = new List<ResolvedAction>(_ordered.Count);
        foreach (var a in _ordered)
        {
            if (a.Matcher is not null && !a.Matcher.IsMatch(context.Text)) continue;
            if (!a.Action.CanRun(context)) continue;
            list.Add(a);
        }
        return list;
    }
}

public sealed record ResolvedAction(ActionDescriptor Descriptor, IAction Action, Regex? Matcher);

internal sealed class UrlTemplateAction : IAction
{
    private readonly ActionDescriptor _descriptor;

    public UrlTemplateAction(ActionDescriptor descriptor) => _descriptor = descriptor;

    public string Id => _descriptor.Id;
    public string Title => string.IsNullOrWhiteSpace(_descriptor.Title) ? "打开 URL" : _descriptor.Title;
    public string IconKey => string.IsNullOrWhiteSpace(_descriptor.Icon) ? "Url" : _descriptor.Icon;
    public bool CanRun(SelectionContext context) => !context.IsEmpty;

    public Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        var url = Expand(_descriptor.UrlTemplate ?? "", context.Text);
        host.UrlLauncher.Open(url);
        return Task.CompletedTask;
    }

    private static string Expand(string template, string text)
    {
        var encoded = WebUtility.UrlEncode(text);
        return template
            .Replace("{text}", text, StringComparison.Ordinal)
            .Replace("{q}", encoded, StringComparison.Ordinal)
            .Replace("{urlencoded}", encoded, StringComparison.Ordinal);
    }
}

internal sealed class ScriptAction : IAction
{
    private readonly ActionDescriptor _descriptor;

    public ScriptAction(ActionDescriptor descriptor) => _descriptor = descriptor;

    public string Id => _descriptor.Id;
    public string Title => string.IsNullOrWhiteSpace(_descriptor.Title) ? "脚本" : _descriptor.Title;
    public string IconKey => string.IsNullOrWhiteSpace(_descriptor.Icon) ? "Script" : _descriptor.Icon;
    public bool CanRun(SelectionContext context) => !context.IsEmpty;

    public async Task RunAsync(SelectionContext context, IActionHost host, CancellationToken ct)
    {
        var path = Environment.ExpandEnvironmentVariables(_descriptor.ScriptPath ?? "");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("脚本文件不存在", path);
        }

        var start = new ProcessStartInfo
        {
            FileName = path,
            Arguments = Expand(_descriptor.Arguments ?? "{text}", context.Text),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["CLIPAURA_TEXT"] = context.Text;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("脚本进程启动失败");
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"脚本退出码 {process.ExitCode}");
        }
    }

    private static string Expand(string template, string text)
    {
        var encoded = WebUtility.UrlEncode(text);
        return template
            .Replace("{text}", Quote(text), StringComparison.Ordinal)
            .Replace("{q}", Quote(encoded), StringComparison.Ordinal)
            .Replace("{urlencoded}", Quote(encoded), StringComparison.Ordinal);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
