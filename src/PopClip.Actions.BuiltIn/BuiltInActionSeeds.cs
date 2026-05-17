namespace PopClip.Actions.BuiltIn;

/// <summary>内置动作分组：用于设置 UI 分类展示，也用于"添加内置动作"对话框的标题</summary>
public enum BuiltInActionGroup
{
    /// <summary>基础动作：复制 / 搜索 / 翻译 / 大小写 / 字数 / 剪贴板历史 等核心能力</summary>
    Basic,
    /// <summary>智能识别动作：CanRun 自行嗅探文本类型，仅在 JSON / 颜色 / 时间戳 / 路径 / 表格 等场景出现</summary>
    Smart,
    /// <summary>AI 动作：仅在 AI 已启用并配置 API Key 时可用</summary>
    Ai,
}

/// <summary>一个"可在动作列表中以条目形式出现的内置动作"的描述。
/// 同时承担两个角色：
/// 1. 数据补齐（seed）：老用户磁盘 actions.json 中缺失的内置动作以此为模板写入
/// 2. 添加内置动作对话框：列出所有"尚未在动作列表中"的内置动作供用户多选添加</summary>
/// <param name="DefaultEnabled">seed 时写入的 enabled 默认值。
/// 智能识别类动作设为 true：CanRun 自带"按文本类型守门"，不会在不匹配的选区上显示，
/// 默认启用让用户能开箱体验。AI 系列即使为 true，AI 没启用时也会被 SelectionSessionManager 屏蔽，
/// 因此同样默认 true，待用户启用 AI 后自动出现</param>
public sealed record BuiltInActionSeed(
    string BuiltIn,
    string DescriptorId,
    string Title,
    string IconKey,
    BuiltInActionGroup Group,
    string? Description = null,
    bool DefaultEnabled = true);

/// <summary>内置动作的单一真理源。
/// 顺序即"添加对话框"中的默认呈现顺序；新增内置动作只追加到对应分组的末尾，
/// 否则老用户的 SeededBuiltInIds 集合会因为 ID 不变但顺序变化而出现"重复 seed"假象</summary>
public static class BuiltInActionSeeds
{
    public static IReadOnlyList<BuiltInActionSeed> All { get; } = new[]
    {
        new BuiltInActionSeed(BuiltInActionIds.Copy, "copy", "复制", "Copy", BuiltInActionGroup.Basic),
        new BuiltInActionSeed(BuiltInActionIds.Paste, "paste", "粘贴", "Paste", BuiltInActionGroup.Basic),
        new BuiltInActionSeed(BuiltInActionIds.OpenUrl, "open-url", "打开链接", "Url", BuiltInActionGroup.Basic, "选中文本是 URL 时一键打开"),
        new BuiltInActionSeed(BuiltInActionIds.Mailto, "mailto", "发送邮件", "Mail", BuiltInActionGroup.Basic, "选中文本是邮箱时调起默认邮件客户端"),
        new BuiltInActionSeed(BuiltInActionIds.Search, "search", "搜索", "Search", BuiltInActionGroup.Basic, "用当前搜索引擎搜索选区文本"),
        new BuiltInActionSeed(BuiltInActionIds.Translate, "translate", "翻译", "Translate", BuiltInActionGroup.Basic, "AI 启用时走内联气泡，否则打开网页翻译"),
        new BuiltInActionSeed(BuiltInActionIds.ToUpper, "upper", "大写", "Upper", BuiltInActionGroup.Basic),
        new BuiltInActionSeed(BuiltInActionIds.ToLower, "lower", "小写", "Lower", BuiltInActionGroup.Basic),
        new BuiltInActionSeed(BuiltInActionIds.ToTitle, "title", "标题大小写", "Title", BuiltInActionGroup.Basic),
        new BuiltInActionSeed(BuiltInActionIds.Calculate, "calc", "计算", "Calc", BuiltInActionGroup.Basic, "选中算式自动求值"),
        new BuiltInActionSeed(BuiltInActionIds.WordCount, "wc", "字数统计", "Count", BuiltInActionGroup.Basic),
        new BuiltInActionSeed(BuiltInActionIds.ClipboardHistory, "clipboard-history", "剪贴板历史", "ClipboardHistory", BuiltInActionGroup.Basic),
        new BuiltInActionSeed(BuiltInActionIds.JsonFormat, "json-format", "格式化 JSON", "Json", BuiltInActionGroup.Smart, "选中合法 JSON 时显示，缩进 2 空格"),
        new BuiltInActionSeed(BuiltInActionIds.JsonToYaml, "json-to-yaml", "JSON → YAML", "JsonToYaml", BuiltInActionGroup.Smart, "选中合法 JSON 时显示，转 YAML 复制"),
        new BuiltInActionSeed(BuiltInActionIds.Color, "color", "颜色", "Color", BuiltInActionGroup.Smart, "识别 #HEX / rgb / rgba，输出 HEX / RGB / HSL"),
        new BuiltInActionSeed(BuiltInActionIds.Timestamp, "timestamp", "时间戳", "Time", BuiltInActionGroup.Smart, "识别 10/13 位 Unix 时间戳，转本地/UTC/相对时间"),
        new BuiltInActionSeed(BuiltInActionIds.PathOpen, "path-open", "在资源管理器打开", "FolderOpen", BuiltInActionGroup.Smart, "识别 Windows 路径，直接定位到文件"),
        new BuiltInActionSeed(BuiltInActionIds.MarkdownTableToCsv, "mdtable-to-csv", "MD 表 → CSV", "MdToCsv", BuiltInActionGroup.Smart, "识别 Markdown 表格，转 CSV 复制"),
        new BuiltInActionSeed(BuiltInActionIds.CsvToMarkdown, "csv-to-mdtable", "CSV → MD 表", "Table", BuiltInActionGroup.Smart, "识别 CSV 文本（行列对齐），转 Markdown 表格"),
        new BuiltInActionSeed(BuiltInActionIds.TsvToCsv, "tsv-to-csv", "TSV → CSV", "TsvToCsv", BuiltInActionGroup.Smart, "识别 Tab 分隔文本（如从 Excel 复制），转 CSV 复制"),
        new BuiltInActionSeed(BuiltInActionIds.TsvToMarkdown, "tsv-to-mdtable", "TSV → MD 表", "TsvToMd", BuiltInActionGroup.Smart, "识别 Tab 分隔文本，转 Markdown 表格"),

        new BuiltInActionSeed(BuiltInActionIds.AiChat, "ai-chat", "AI 对话", "AiChat", BuiltInActionGroup.Ai),
        new BuiltInActionSeed(BuiltInActionIds.AiExplain, "ai-explain", "AI 解释", "AiExplain", BuiltInActionGroup.Ai, "用 AI 解释选中文本含义，结果走流式气泡"),
    };

    public static string GroupTitle(BuiltInActionGroup group) => group switch
    {
        BuiltInActionGroup.Basic => "基础动作",
        BuiltInActionGroup.Smart => "智能识别（按选中内容自动出现）",
        BuiltInActionGroup.Ai => "AI 动作（需在 AI 页配置）",
        _ => group.ToString(),
    };

    /// <summary>按 BuiltInId 反查所属分组。运行时浮窗布局需要据此决定按钮归到哪一行。
    /// 未注册的 BuiltInId（如未来扩展或用户手写但未对应 seed 的）返回 Basic 作兜底</summary>
    public static BuiltInActionGroup GroupOf(string? builtInId)
    {
        if (string.IsNullOrEmpty(builtInId)) return BuiltInActionGroup.Basic;
        foreach (var seed in All)
        {
            if (string.Equals(seed.BuiltIn, builtInId, System.StringComparison.OrdinalIgnoreCase))
                return seed.Group;
        }
        return BuiltInActionGroup.Basic;
    }
}
