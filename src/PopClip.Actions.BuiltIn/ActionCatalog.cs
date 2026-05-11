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
        _registry = new Dictionary<string, IAction>(StringComparer.OrdinalIgnoreCase)
        {
            [BuiltInActionIds.Copy] = new CopyAction(),
            [BuiltInActionIds.GoogleSearch] = new GoogleSearchAction(),
            [BuiltInActionIds.BingSearch] = new BingSearchAction(),
            [BuiltInActionIds.Translate] = new TranslateAction(),
            [BuiltInActionIds.ToUpper] = new ToUpperAction(),
            [BuiltInActionIds.ToLower] = new ToLowerAction(),
            [BuiltInActionIds.ToTitle] = new ToTitleAction(),
            [BuiltInActionIds.OpenUrl] = new OpenUrlAction(),
            [BuiltInActionIds.Mailto] = new MailtoAction(),
            [BuiltInActionIds.Calculate] = new CalculateAction(),
            [BuiltInActionIds.WordCount] = new WordCountAction(),
        };
    }

    public void Load(ActionsConfig config)
    {
        var ordered = new List<ResolvedAction>(config.Actions.Count);
        foreach (var d in config.Actions)
        {
            if (!d.Enabled) continue;
            if (!string.Equals(d.Type, "builtin", StringComparison.OrdinalIgnoreCase))
            {
                _log.Warn("unknown action type, skipped", ("id", d.Id), ("type", d.Type));
                continue;
            }
            if (d.BuiltIn is null || !_registry.TryGetValue(d.BuiltIn, out var action))
            {
                _log.Warn("builtin not found", ("id", d.Id), ("ref", d.BuiltIn ?? "(null)"));
                continue;
            }

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
                new() { Id = "google", Type = "builtin", BuiltIn = BuiltInActionIds.GoogleSearch, Title = "Google" },
                new() { Id = "bing", Type = "builtin", BuiltIn = BuiltInActionIds.BingSearch, Title = "Bing" },
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
