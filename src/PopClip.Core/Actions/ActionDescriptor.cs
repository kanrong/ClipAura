namespace PopClip.Core.Actions;

/// <summary>JSON 中声明动作的形状。支持 builtin / url-template / ai 三种类型。
/// 设计意图是"普通用户能在 GUI 里勾选/填空完成的能力"，刻意不再支持任意脚本/正则等开发向特性</summary>
public sealed class ActionDescriptor
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Type { get; set; } = "builtin";

    /// <summary>builtin 类型时引用的内置实现键</summary>
    public string? BuiltIn { get; set; }

    /// <summary>type=url-template 时使用的 URL 模板，支持 {text}/{q}/{urlencoded} 占位符</summary>
    public string? UrlTemplate { get; set; }

    /// <summary>type=ai 时的用户 prompt 模板。可使用 {text} {language} {clipboard} 等占位符</summary>
    public string? Prompt { get; set; }

    /// <summary>type=ai 时附加的 system prompt；为空则使用默认助手 prompt</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>type=ai 时的输出方式：chat / replace / clipboard / inlineToast；默认 chat</summary>
    public string? OutputMode { get; set; }

    /// <summary>true=图标固定不可在 UI 修改。
    /// 内置动作以及"从内置 Prompt 模板派生"的 AI 动作都设为 true，让图标语义和动作语义一一对应；
    /// 用户从"添加 URL / 添加 AI 自定义"创建的动作则保持 false，允许在通用图标库中挑选</summary>
    public bool IconLocked { get; set; }

    public bool Enabled { get; set; } = true;
}

public sealed class ActionsConfig
{
    public int SchemaVersion { get; set; } = 1;
    public List<ActionDescriptor> Actions { get; set; } = new();
}
