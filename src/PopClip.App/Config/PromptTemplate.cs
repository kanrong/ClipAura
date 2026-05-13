namespace PopClip.App.Config;

/// <summary>Prompt 模板的持久化形态。
/// 用户可在设置里编辑这些模板，"提升为动作"会基于此生成 ActionDescriptor 写入 actions.json</summary>
public sealed class PromptTemplateDefinition
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Icon { get; set; } = "Ai";
    /// <summary>chat | replace | clipboard | inlineToast</summary>
    public string OutputMode { get; set; } = "chat";
    public string Prompt { get; set; } = "";
    public string? SystemPrompt { get; set; }
    public string? Description { get; set; }
    public bool BuiltIn { get; set; }
}

public static class PromptTemplateLibrary
{
    /// <summary>开箱即用的高质量模板。
    /// 设计原则：每个模板都明确 outputMode + 写清"只输出 X" 等约束，避免模型啰嗦</summary>
    public static IReadOnlyList<PromptTemplateDefinition> Builtin { get; } = new[]
    {
        new PromptTemplateDefinition
        {
            Id = "tpl.fix-grammar",
            Title = "修语法",
            Icon = "AiRewrite",
            OutputMode = "replace",
            BuiltIn = true,
            Description = "原地修正语法、标点与拼写，保持原意",
            Prompt = "只修正下面文本中的语法、标点和明显拼写错误，不要改写表达、不要解释、保留原语言：\n\n{text}",
        },
        new PromptTemplateDefinition
        {
            Id = "tpl.polish",
            Title = "润色",
            Icon = "AiRewrite",
            OutputMode = "replace",
            BuiltIn = true,
            Description = "在不改变原意的前提下让表达更自然、清晰",
            Prompt = "用{language}润色下面的文本，让表达更自然、清晰、流畅，保持语义不变。只输出润色后的正文：\n\n{text}",
        },
        new PromptTemplateDefinition
        {
            Id = "tpl.summarize",
            Title = "三句话总结",
            Icon = "AiSummary",
            OutputMode = "chat",
            BuiltIn = true,
            Description = "三句话内提炼核心信息",
            Prompt = "用{language}在三句话内总结下面文本的核心信息：\n\n{text}",
        },
        new PromptTemplateDefinition
        {
            Id = "tpl.translate-en",
            Title = "译为英文",
            Icon = "AiTranslate",
            OutputMode = "replace",
            BuiltIn = true,
            Description = "翻译为英文并原地替换",
            Prompt = "把下面的文本翻译为英文。只输出译文，不要解释：\n\n{text}",
        },
        new PromptTemplateDefinition
        {
            Id = "tpl.translate-zh",
            Title = "译为中文",
            Icon = "AiTranslate",
            OutputMode = "replace",
            BuiltIn = true,
            Description = "翻译为简体中文并原地替换",
            Prompt = "把下面的文本翻译为简体中文。只输出译文，不要解释：\n\n{text}",
        },
        new PromptTemplateDefinition
        {
            Id = "tpl.code-explain",
            Title = "解释代码",
            Icon = "AiExplain",
            OutputMode = "chat",
            BuiltIn = true,
            Description = "用中文解释这段代码做了什么",
            Prompt = "用{language}解释下面的代码片段做了什么、关键思路、潜在风险。先给一句话总结，再分点说明：\n\n```\n{text}\n```",
        },
        new PromptTemplateDefinition
        {
            Id = "tpl.commit-message",
            Title = "生成 commit",
            Icon = "AiRewrite",
            OutputMode = "clipboard",
            BuiltIn = true,
            Description = "把改动描述/diff 转成 conventional commit",
            Prompt = "你是一个 git commit message 写作助手。基于下面的描述或 diff，生成一条 conventional commit 风格的中文消息（feat/fix/chore/refactor/...），单行不超过 60 字符，只输出消息本身：\n\n{text}",
        },
        new PromptTemplateDefinition
        {
            Id = "tpl.formal-reply",
            Title = "正式回复",
            Icon = "AiReply",
            OutputMode = "chat",
            BuiltIn = true,
            Description = "根据消息生成一段礼貌、正式的回复",
            Prompt = "下面是一段消息。请根据消息内容用{language}起草一段可直接发送的正式、礼貌、具体的回复：\n\n{text}",
        },
    };
}
