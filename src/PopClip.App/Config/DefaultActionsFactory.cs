using PopClip.Actions.BuiltIn;
using PopClip.Core.Actions;

namespace PopClip.App.Config;

internal static class DefaultActionsFactory
{
    public static ActionsConfig Create()
    {
        return new ActionsConfig
        {
            Actions =
            {
                new() { Id = "copy", Type = "builtin", BuiltIn = BuiltInActionIds.Copy, Title = "复制", Icon = "Copy", IconLocked = true, Enabled = true },
                new() { Id = "paste", Type = "builtin", BuiltIn = BuiltInActionIds.Paste, Title = "粘贴", Icon = "Paste", IconLocked = true, Enabled = true },
                new() { Id = "open-url", Type = "builtin", BuiltIn = BuiltInActionIds.OpenUrl, Title = "打开链接", Icon = "Url", IconLocked = true, Enabled = true },
                new() { Id = "mailto", Type = "builtin", BuiltIn = BuiltInActionIds.Mailto, Title = "发送邮件", Icon = "Mail", IconLocked = true, Enabled = true },
                new() { Id = "search", Type = "builtin", BuiltIn = BuiltInActionIds.Search, Title = "搜索", Icon = "Search", IconLocked = true, Enabled = true },
                new() { Id = "translate", Type = "builtin", BuiltIn = BuiltInActionIds.Translate, Title = "翻译", Icon = "Translate", IconLocked = true, Enabled = true },
                new() { Id = "calc", Type = "builtin", BuiltIn = BuiltInActionIds.Calculate, Title = "计算", Icon = "Calc", OutputMode = BuiltInOutputModes.Bubble, IconLocked = true, Enabled = true },
                new() { Id = "wc", Type = "builtin", BuiltIn = BuiltInActionIds.WordCount, Title = "字数统计", Icon = "Count", OutputMode = BuiltInOutputModes.Bubble, IconLocked = true, Enabled = true },
                new() { Id = "clipboard-history", Type = "builtin", BuiltIn = BuiltInActionIds.ClipboardHistory, Title = "剪贴板历史", Icon = "ClipboardHistory", IconLocked = true, Enabled = true },
                new() { Id = "upper", Type = "builtin", BuiltIn = BuiltInActionIds.ToUpper, Title = "大写", Icon = "Upper", IconLocked = true, Enabled = true },
                new() { Id = "lower", Type = "builtin", BuiltIn = BuiltInActionIds.ToLower, Title = "小写", Icon = "Lower", IconLocked = true, Enabled = true },
                new() { Id = "title", Type = "builtin", BuiltIn = BuiltInActionIds.ToTitle, Title = "标题大小写", Icon = "Title", IconLocked = true, Enabled = true },
                new() { Id = "json-format", Type = "builtin", BuiltIn = BuiltInActionIds.JsonFormat, Title = "格式化 JSON", Icon = "Json", OutputMode = BuiltInOutputModes.Bubble, IconLocked = true, Enabled = true },
                new() { Id = "json-to-yaml", Type = "builtin", BuiltIn = BuiltInActionIds.JsonToYaml, Title = "JSON → YAML", Icon = "JsonToYaml", OutputMode = BuiltInOutputModes.Bubble, IconLocked = true, Enabled = true },
                new() { Id = "color", Type = "builtin", BuiltIn = BuiltInActionIds.Color, Title = "颜色", Icon = "Color", OutputMode = BuiltInOutputModes.Bubble, IconLocked = true, Enabled = true },
                new() { Id = "timestamp", Type = "builtin", BuiltIn = BuiltInActionIds.Timestamp, Title = "时间戳", Icon = "Time", OutputMode = BuiltInOutputModes.Bubble, IconLocked = true, Enabled = true },
                new() { Id = "path-open", Type = "builtin", BuiltIn = BuiltInActionIds.PathOpen, Title = "在资源管理器打开", Icon = "FolderOpen", IconLocked = true, Enabled = true },
                new() { Id = "mdtable-to-csv", Type = "builtin", BuiltIn = BuiltInActionIds.MarkdownTableToCsv, Title = "MD 表 → CSV", Icon = "MdToCsv", OutputMode = BuiltInOutputModes.ClipboardAndBubble, IconLocked = true, Enabled = true },
                new() { Id = "csv-to-mdtable", Type = "builtin", BuiltIn = BuiltInActionIds.CsvToMarkdown, Title = "CSV → MD 表", Icon = "Table", OutputMode = BuiltInOutputModes.ClipboardAndBubble, IconLocked = true, Enabled = true },
                new() { Id = "tsv-to-csv", Type = "builtin", BuiltIn = BuiltInActionIds.TsvToCsv, Title = "TSV → CSV", Icon = "TsvToCsv", OutputMode = BuiltInOutputModes.ClipboardAndBubble, IconLocked = true, Enabled = true },
                new() { Id = "tsv-to-mdtable", Type = "builtin", BuiltIn = BuiltInActionIds.TsvToMarkdown, Title = "TSV → MD 表", Icon = "TsvToMd", OutputMode = BuiltInOutputModes.ClipboardAndBubble, IconLocked = true, Enabled = true },
                new() { Id = "word-lookup", Type = "builtin", BuiltIn = BuiltInActionIds.WordLookup, Title = "查词", Icon = "Dictionary", OutputMode = BuiltInOutputModes.Bubble, IconLocked = true, Enabled = true },
                new() { Id = "vocabulary-analyze", Type = "builtin", BuiltIn = BuiltInActionIds.VocabularyAnalyze, Title = "词汇解析", Icon = "Vocabulary", OutputMode = BuiltInOutputModes.Bubble, IconLocked = true, Enabled = true },
                new() { Id = "ai-chat", Type = "builtin", BuiltIn = BuiltInActionIds.AiChat, Title = "AI 对话", Icon = "AiChat", IconLocked = true, Enabled = true },
                new() { Id = "ai-explain", Type = "builtin", BuiltIn = BuiltInActionIds.AiExplain, Title = "AI 解释", Icon = "AiExplain", IconLocked = true, Enabled = true },
            },
        };
    }
}
