namespace PopClip.Core.Actions;

/// <summary>JSON 中声明动作的形状。MVP 仅支持 type=builtin，引用一个内置 Id；v2 扩展 type=script/url-template</summary>
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

    public bool Enabled { get; set; } = true;
}

public sealed class ActionsConfig
{
    public int SchemaVersion { get; set; } = 1;
    public List<ActionDescriptor> Actions { get; set; } = new();
}
