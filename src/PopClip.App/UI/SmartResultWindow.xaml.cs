using System;
using System.Windows;
using Wpf.Ui.Controls;

namespace PopClip.App.UI;

/// <summary>"Dialog 对话框"输出模式承载窗口。
/// 用于智能动作（如 CSV→MD 表 / JSON 格式化）结果较长时把整段内容放到独立可滚动窗口里查看。
/// 与 AiResultWindow 的差异：不带 prompt 输入框、不带追问按钮，纯静态展示 + 复制 + 关闭。
/// 与 AiBubbleWindow 的差异：尺寸大、可滚动到任意长度，适合多页 CSV / JSON / 多行表格</summary>
public partial class SmartResultWindow : FluentWindow
{
    public SmartResultWindow(string title, string referenceText, string resultText)
    {
        InitializeComponent();
        Title = "ClipAura · " + title;
        TitleBar.Title = title;
        MetaText.Text = $"原文 {referenceText.Length} 字 · 结果 {resultText.Length} 字";
        ReferenceText.Text = string.IsNullOrEmpty(referenceText) ? "—" : referenceText;
        ResultText.Text = resultText ?? "";
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ResultText.Text ?? "");
            CopyButton.Content = "已复制 ✓";
        }
        catch
        {
            // 剪贴板偶现"另一进程持有"异常，忽略后下次重试
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
