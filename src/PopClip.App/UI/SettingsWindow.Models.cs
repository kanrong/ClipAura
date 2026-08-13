using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PopClip.Actions.BuiltIn;
using PopClip.App.Config;
using PopClip.App.Hosting;
using PopClip.App.Ocr;
using PopClip.App.Services;
using PopClip.Core.Actions;
using PopClip.Core.Logging;
using PopClip.Core.Session;
using PopClip.Hooks;
using System.Windows.Automation;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;

namespace PopClip.App.UI;


/// <summary>"添加内置动作"对话框的一行候选；Group 用于在对话框中按段分组展示</summary>
public sealed record BuiltInChoice(
    string Id,
    string Title,
    string IconKey,
    BuiltInActionGroup Group,
    string? Description);

/// <summary>外观页"实时预览"中的示例按钮。
/// Icon 使用 WPF-UI SymbolRegular 而非应用内自定义图标 key，因为预览只需呈现视觉效果，
/// 不必走真实浮窗的 IconKeyToMaterialDesignKindConverter 转换链</summary>
public sealed record ToolbarPreviewItem(string Title, Wpf.Ui.Controls.SymbolRegular Icon);

public sealed record AiOutputModeChoice(string Value, string Label);

public sealed record ConversationListItem(string Id, string Title, string MetaText)
{
    public static ConversationListItem From(ConversationSummary s)
    {
        var local = s.CreatedAtUtc.ToLocalTime();
        var meta = $"{local:yyyy-MM-dd HH:mm} · {s.Model} · {s.MessageCount} 条消息";
        return new ConversationListItem(s.Id, string.IsNullOrWhiteSpace(s.Title) ? "未命名" : s.Title, meta);
    }
}

public sealed record UsageRow(string DateLabel, string CallsLabel, string PromptLabel, string CompletionLabel);

public sealed record AiThinkingModeChoice(AiThinkingMode Value, string Label, string Description);
public sealed record LogLevelChoice(LogLevel Value, string Label);

/// <summary>外观页"颜色主题"卡片的数据载体。
/// BackgroundHex / ForegroundHex 直接以颜色字符串声明，方便在常量数组里写死；
/// XAML 端通过 BackgroundBrush / ForegroundBrush 拿到 SolidColorBrush 渲染预览圆点</summary>
public sealed class ToolbarThemeChoice
{
    public ToolbarThemeChoice(ToolbarThemeMode mode, string label, string backgroundHex, string foregroundHex)
    {
        Mode = mode;
        Label = label;
        BackgroundBrush = MakeBrush(backgroundHex);
        ForegroundBrush = MakeBrush(foregroundHex);
    }

    public ToolbarThemeMode Mode { get; }
    public string Label { get; }
    public SolidColorBrush BackgroundBrush { get; }
    public SolidColorBrush ForegroundBrush { get; }

    private static SolidColorBrush MakeBrush(string hex)
    {
        try
        {
            return (SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;
        }
        catch
        {
            return new SolidColorBrush(System.Windows.Media.Colors.Transparent);
        }
    }
}

/// <summary>字体 ComboBox 的数据载体。
/// 留空 Value 表示"使用默认回退链"，Label 含中文说明便于普通用户理解</summary>
public sealed record FontFamilyChoice(string Value, string Label);

public sealed record RecentProcessItem(string ProcessName, string WindowTitle)
{
    public string Display => string.IsNullOrWhiteSpace(WindowTitle)
        ? ProcessName
        : $"{ProcessName} - {WindowTitle}";
}

/// <summary>动作卡片在设置界面中的可编辑视图。
/// 字段仅保留普通用户能在 GUI 里直观填写的内容；
/// Type/BuiltIn 用作内部分发不在 UI 直接显示（创建动作的入口已经决定它们的取值）。
/// IconLocked=true 时 UI 中的图标选择器隐藏，用于内置/预定义模板等"图标绑定语义"的动作</summary>
public sealed class ActionEditorItem : INotifyPropertyChanged
{
    private string _id = "";
    private string _title = "";
    private string _icon = "";
    private string _type = "builtin";
    private string? _builtIn;
    private string? _urlTemplate;
    private string? _prompt;
    private string? _systemPrompt;
    private string _outputMode = "chat";
    private bool _enabled = true;
    private bool _iconLocked;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get => _id; set => Set(ref _id, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Icon { get => _icon; set => Set(ref _icon, value); }
    public string Type { get => _type; set => Set(ref _type, value); }
    public string? BuiltIn
    {
        get => _builtIn;
        set
        {
            if (Set(ref _builtIn, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBuiltInOutputConfigurable)));
            }
        }
    }
    public string? UrlTemplate { get => _urlTemplate; set => Set(ref _urlTemplate, value); }
    public string? Prompt { get => _prompt; set => Set(ref _prompt, value); }
    public string? SystemPrompt { get => _systemPrompt; set => Set(ref _systemPrompt, value); }
    public string OutputMode { get => _outputMode; set => Set(ref _outputMode, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary>是否为"有结果产出 + 可配置输出模式"的内置动作。
    /// 设置页据此决定主行右侧是否显示 OutputMode 下拉框。
    /// 与 IsAiType 互斥：AI 动作走 AiOutputMode 下拉（已有），builtin smart 走 BuiltInOutputMode 下拉（新）</summary>
    public bool IsBuiltInOutputConfigurable
        => string.Equals(_type, "builtin", StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrEmpty(_builtIn)
           && BuiltInOutputModes.SupportsOutputMode(_builtIn);

    public bool IconLocked
    {
        get => _iconLocked;
        set
        {
            if (Set(ref _iconLocked, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIconEditable)));
            }
        }
    }

    /// <summary>给 XAML 直接 Visibility 绑定的辅助属性。IconLocked 取反即可</summary>
    public bool IsIconEditable => !_iconLocked;

    /// <summary>"是否 AI 动作"。XAML 用它决定 Prompt/SystemPrompt/OutputMode 这一组字段是否显示</summary>
    public bool IsAiType => string.Equals(_type, "ai", StringComparison.OrdinalIgnoreCase);

    /// <summary>"是否 URL 模板动作"。XAML 用它决定 UrlTemplate 输入框是否显示</summary>
    public bool IsUrlType => string.Equals(_type, "url-template", StringComparison.OrdinalIgnoreCase);

    public static ActionEditorItem FromDescriptor(ActionDescriptor descriptor)
    {
        // OutputMode 的默认值因类型而异：
        // - AI 动作走 AiOutputMode，默认 "chat"
        // - 内置 smart 动作走 BuiltInOutputMode，默认 "clipboardAndBubble"
        // - 其它内置（Copy/Paste/Search 等）不读取 OutputMode，留空即可
        var defaultOutputMode = string.Equals(descriptor.Type, "ai", StringComparison.OrdinalIgnoreCase)
            ? "chat"
            : (!string.IsNullOrEmpty(descriptor.BuiltIn) && BuiltInOutputModes.SupportsOutputMode(descriptor.BuiltIn)
                ? BuiltInOutputModes.ClipboardAndBubble
                : "");
        return new ActionEditorItem
        {
            Id = descriptor.Id,
            Title = descriptor.Title,
            Icon = descriptor.Icon,
            Type = descriptor.Type,
            BuiltIn = descriptor.BuiltIn,
            UrlTemplate = descriptor.UrlTemplate,
            Prompt = descriptor.Prompt,
            SystemPrompt = descriptor.SystemPrompt,
            OutputMode = string.IsNullOrWhiteSpace(descriptor.OutputMode) ? defaultOutputMode : descriptor.OutputMode,
            Enabled = descriptor.Enabled,
            IconLocked = descriptor.IconLocked,
        };
    }

    public ActionDescriptor ToDescriptor()
    {
        string? persistedOutputMode = null;
        if (string.Equals(Type, "ai", StringComparison.OrdinalIgnoreCase))
        {
            persistedOutputMode = string.IsNullOrWhiteSpace(OutputMode) ? "chat" : OutputMode;
        }
        else if (IsBuiltInOutputConfigurable)
        {
            persistedOutputMode = string.IsNullOrWhiteSpace(OutputMode)
                ? BuiltInOutputModes.ClipboardAndBubble
                : OutputMode;
        }

        return new ActionDescriptor
        {
            Id = Id.Trim(),
            Title = Title.Trim(),
            Icon = Icon.Trim(),
            Type = Type.Trim(),
            BuiltIn = string.IsNullOrWhiteSpace(BuiltIn) ? null : BuiltIn,
            UrlTemplate = string.IsNullOrWhiteSpace(UrlTemplate) ? null : UrlTemplate,
            Prompt = string.IsNullOrWhiteSpace(Prompt) ? null : Prompt,
            SystemPrompt = string.IsNullOrWhiteSpace(SystemPrompt) ? null : SystemPrompt,
            OutputMode = persistedOutputMode,
            IconLocked = IconLocked,
            Enabled = Enabled,
        };
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
