using System.Windows;
using System.Windows.Controls;
using PopClip.App.Services;

namespace PopClip.App.UI;

/// <summary>历史对话的只读回放窗口。
/// 把消息序列以"用户/助手"两色块渲染，不复活流式 / 不支持继续追问；
/// 继续追问可以走"复制到剪贴板再发起新对话"等流程，这里保持简单</summary>
public partial class ConversationReplayWindow : Wpf.Ui.Controls.FluentWindow
{
    public ConversationReplayWindow(ConversationRecord record)
    {
        InitializeComponent();
        TitleBar.Title = record.Title;
        var local = record.CreatedAtUtc.ToLocalTime();
        MetaText.Text = $"{local:yyyy-MM-dd HH:mm} · {record.Model} · {record.Provider} · "
                       + $"{record.PromptTokens}→{record.CompletionTokens} tok · {record.Messages.Count} 条";
        ReferenceText.Text = string.IsNullOrWhiteSpace(record.ReferenceText)
            ? ""
            : "引用：" + record.ReferenceText;
        ReferenceText.Visibility = string.IsNullOrWhiteSpace(record.ReferenceText)
            ? Visibility.Collapsed : Visibility.Visible;

        foreach (var (role, content) in record.Messages)
        {
            AppendMessage(role, content);
        }
    }

    private void AppendMessage(string role, string content)
    {
        var isUser = string.Equals(role, "user", System.StringComparison.OrdinalIgnoreCase);
        var border = new Border
        {
            Margin = new Thickness(0, 4, 0, 6),
            Padding = new Thickness(12, 8, 12, 10),
            BorderThickness = new Thickness(1),
        };
        border.SetResourceReference(Border.BorderBrushProperty, "Settings.Stroke");
        border.SetResourceReference(Border.BackgroundProperty,
            isUser ? "Settings.AccentSoft" : "Settings.Card.SubtleBackground");
        border.SetResourceReference(Border.CornerRadiusProperty, "Settings.Radius.Md");

        var stack = new StackPanel();
        var header = new TextBlock
        {
            Text = isUser ? "你" : "ClipAura AI",
            FontWeight = FontWeights.SemiBold,
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 0, 4),
        };
        header.SetResourceReference(TextBlock.ForegroundProperty,
            isUser ? "Settings.Accent" : "Settings.SubtleForeground");
        var text = new TextBox
        {
            Text = content,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            Padding = new Thickness(0),
        };
        text.SetResourceReference(TextBox.ForegroundProperty, "Settings.Foreground");

        stack.Children.Add(header);
        stack.Children.Add(text);
        border.Child = stack;
        MessagesPanel.Children.Add(border);
    }
}
