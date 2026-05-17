using System.Net;
using PopClip.Core.Actions;
using PopClip.Core.Logging;
using PopClip.Core.Model;

namespace PopClip.Actions.BuiltIn;

/// <summary>把 JSON 配置展开为运行时动作集合，并提供按选区过滤的 GetVisible。
/// 当前仅支持三种类型：builtin（注册表内 IAction） / url-template（外链） / ai（prompt 模板）；
/// 显示与否由对应 IAction.CanRun 自行裁决，不再借助配置层正则</summary>
public sealed class ActionCatalog
{
    private readonly ILog _log;
    private readonly Dictionary<string, IAction> _registry;
    private List<ResolvedAction> _ordered = new();

    /// <summary>构造函数。
    /// pasteService 必须随 catalog 一起注入：PasteAction.CanRun 依赖它判定剪贴板是否有文本，
    /// 注入到 catalog 而非通过 IActionHost 取，是为了让 CanRun（接口不携带 host）也能访问粘贴能力</summary>
    public ActionCatalog(ILog log, IPasteService pasteService)
    {
        _log = log;
        // 旧 GoogleSearch / BingSearch ID 全部别名到统一 SearchAction，兼容历史 actions.json
        var search = new SearchAction();
        _registry = new Dictionary<string, IAction>(StringComparer.OrdinalIgnoreCase)
        {
            [BuiltInActionIds.Copy] = new CopyAction(),
            [BuiltInActionIds.Paste] = new PasteAction(pasteService),
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
            [BuiltInActionIds.OcrParagraphTidy] = new OcrParagraphTidyAction(),
            [BuiltInActionIds.AiChat] = new AiChatAction(),
            [BuiltInActionIds.AiExplain] = new AiExplainAction(),
            [BuiltInActionIds.JsonFormat] = new JsonFormatAction(),
            [BuiltInActionIds.JsonToYaml] = new JsonToYamlAction(),
            [BuiltInActionIds.Color] = new ColorAction(),
            [BuiltInActionIds.Timestamp] = new TimestampAction(),
            [BuiltInActionIds.PathOpen] = new PathAction(),
            [BuiltInActionIds.MarkdownTableToCsv] = new MarkdownTableToCsvAction(),
            [BuiltInActionIds.CsvToMarkdown] = new CsvToMarkdownAction(),
            [BuiltInActionIds.TsvToCsv] = new TsvToCsvAction(),
            [BuiltInActionIds.TsvToMarkdown] = new TsvToMarkdownAction(),
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
            ordered.Add(new ResolvedAction(d, action));
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

    /// <summary>未提供配置时使用的默认动作顺序。
    /// 浮窗最多显示 5 个，所以这里只保留"用户最可能用到的开箱即用动作"；
    /// 其它（翻译/大小写/计算/字数 等）用户可以从设置里手动加</summary>
    public void LoadDefaults()
    {
        var cfg = new ActionsConfig
        {
            Actions = new List<ActionDescriptor>
            {
                new() { Id = "copy", Type = "builtin", BuiltIn = BuiltInActionIds.Copy, Title = "复制", IconLocked = true },
                new() { Id = "paste", Type = "builtin", BuiltIn = BuiltInActionIds.Paste, Title = "粘贴", IconLocked = true },
                new() { Id = "search", Type = "builtin", BuiltIn = BuiltInActionIds.Search, Title = "搜索", IconLocked = true },
                new() { Id = "open-url", Type = "builtin", BuiltIn = BuiltInActionIds.OpenUrl, Title = "打开链接", IconLocked = true },
                new() { Id = "mailto", Type = "builtin", BuiltIn = BuiltInActionIds.Mailto, Title = "发送邮件", IconLocked = true },
                new() { Id = "ai-chat", Type = "builtin", BuiltIn = BuiltInActionIds.AiChat, Title = "AI 对话", IconLocked = true },
            },
        };
        Load(cfg);
    }

    public IReadOnlyList<ResolvedAction> GetVisible(SelectionContext context)
    {
        var list = new List<ResolvedAction>(_ordered.Count);
        foreach (var a in _ordered)
        {
            if (!a.Action.CanRun(context)) continue;
            list.Add(a);
        }
        return list;
    }
}

public sealed record ResolvedAction(ActionDescriptor Descriptor, IAction Action);

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
