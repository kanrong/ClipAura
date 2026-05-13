namespace PopClip.Core.Actions;

/// <summary>JSON 中声明动作的形状。支持 builtin / url-template / script / ai 四种类型</summary>
public sealed class ActionDescriptor
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Type { get; set; } = "builtin";

    /// <summary>builtin 类型时引用的内置实现键</summary>
    public string? BuiltIn { get; set; }

    /// <summary>触发正则；为空表示总是显示</summary>
    public string? MatchRegex { get; set; }

    /// <summary>v2 占位：URL 模板，{text}/{urlencoded} 占位</summary>
    public string? UrlTemplate { get; set; }

    public string? ScriptPath { get; set; }
    public string? Arguments { get; set; }

    /// <summary>type=ai 时的用户 prompt 模板。可使用 {text} {language} {clipboard} {foreground_proc} {selection_len} 等占位符</summary>
    public string? Prompt { get; set; }

    /// <summary>type=ai 时附加的 system prompt；为空则使用默认助手 prompt</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>type=ai 时的输出方式：chat / replace / clipboard / inlineToast；默认 chat</summary>
    public string? OutputMode { get; set; }

    public bool Enabled { get; set; } = true;
}

public sealed class ActionsConfig
{
    public int SchemaVersion { get; set; } = 1;
    public List<ActionDescriptor> Actions { get; set; } = new();
}
