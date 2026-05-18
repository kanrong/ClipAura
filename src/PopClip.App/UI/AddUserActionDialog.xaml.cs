using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PopClip.Actions.BuiltIn;
using PopClip.App.Config;
using Wpf.Ui.Controls;

namespace PopClip.App.UI;

/// <summary>"添加用户动作"对话框。
/// 与 AddBuiltInActionDialog 平行：
/// - AddBuiltInActionDialog 负责"添加预置动作"（基础 / 智能 / AI 内置，不可重复）
/// - 本对话框负责"添加用户自定义动作"（URL / AI 空白 / 从 AI 模板添加，可重复）
///
/// 不直接修改外部 ActionItems：用户点击对应"创建/添加"按钮后，把结果暴露在
/// CreatedItem 字段并以 DialogResult=true 关闭，由调用方决定如何并入动作列表</summary>
public partial class AddUserActionDialog : FluentWindow
{
    public IReadOnlyList<TemplateChoice> Templates { get; }

    /// <summary>用户在对话框中选定要创建的动作；DialogResult=true 时一定非 null。
    /// 调用方读取该字段并写回 ActionItems</summary>
    public ActionEditorItem? CreatedItem { get; private set; }

    public AddUserActionDialog(IEnumerable<PromptTemplateDefinition> templates, IEnumerable<string> existingBuiltInIds)
    {
        InitializeComponent();
        var existingSet = new HashSet<string>(existingBuiltInIds, StringComparer.OrdinalIgnoreCase);
        Templates = templates
            .Select(t => new TemplateChoice(t, IsTemplateCoveredByBuiltIn(t, existingSet)))
            .ToList();
        DataContext = this;
    }

    private static bool IsTemplateCoveredByBuiltIn(PromptTemplateDefinition template, HashSet<string> existingBuiltInIds)
    {
        // 当前内置 AI 动作 ↔ Prompt 模板的等价映射。
        // 仅用于在 UI 上提示"该模板已被内置 AI 动作覆盖"，
        // 仍允许用户添加 —— 重复 / 个性化版本本来就是用户动作的合理用法
        return template.Id switch
        {
            "tpl.explain" => existingBuiltInIds.Contains(BuiltInActionIds.AiExplain),
            _ => false,
        };
    }

    private void OnCreateUrl(object sender, RoutedEventArgs e)
    {
        CreatedItem = new ActionEditorItem
        {
            Type = "url-template",
            Title = "打开 URL",
            Icon = IconChoiceCatalog.UserSelectable[0].IconKey,
            UrlTemplate = "https://www.google.com/search?q={urlencoded}",
            Enabled = true,
        };
        DialogResult = true;
        Close();
    }

    private void OnCreateBlankAi(object sender, RoutedEventArgs e)
    {
        CreatedItem = new ActionEditorItem
        {
            Type = "ai",
            Title = "AI 自定义",
            Icon = IconChoiceCatalog.UserSelectable[0].IconKey,
            Prompt = "请用{language}处理下面的文本：\n\n{text}",
            OutputMode = "chat",
            Enabled = true,
        };
        DialogResult = true;
        Close();
    }

    private void OnAddFromTemplate(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not TemplateChoice choice) return;
        var tpl = choice.Template;
        CreatedItem = new ActionEditorItem
        {
            Id = tpl.Id.Replace("tpl.", "ai-", StringComparison.OrdinalIgnoreCase),
            Type = "ai",
            Title = tpl.Title,
            Icon = string.IsNullOrWhiteSpace(tpl.Icon) ? "Ai" : tpl.Icon,
            Prompt = tpl.Prompt,
            SystemPrompt = tpl.SystemPrompt,
            OutputMode = string.IsNullOrWhiteSpace(tpl.OutputMode) ? "chat" : tpl.OutputMode,
            // 与"从模板添加"保持一致：模板图标承载语义，锁定避免误改
            IconLocked = true,
            Enabled = true,
        };
        DialogResult = true;
        Close();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

/// <summary>模板在对话框中的展示项：携带原模板 + "是否已被等价内置动作覆盖"提示用</summary>
public sealed class TemplateChoice
{
    public PromptTemplateDefinition Template { get; }
    public bool HasBuiltInCounterpart { get; }

    public string Title => Template.Title;
    public string Description => Template.Description ?? "";
    public string OutputMode => Template.OutputMode;

    public TemplateChoice(PromptTemplateDefinition template, bool hasBuiltInCounterpart)
    {
        Template = template;
        HasBuiltInCounterpart = hasBuiltInCounterpart;
    }
}
