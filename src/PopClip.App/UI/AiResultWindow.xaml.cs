using System.Windows;
using PopClip.Core.Actions;

namespace PopClip.App.UI;

public partial class AiResultWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly IClipboardWriter _clipboard;
    private readonly Func<string, Task> _replaceAsync;
    private string _rawResult = "";

    public AiResultWindow(
        string actionTitle,
        string sourceText,
        string model,
        IClipboardWriter clipboard,
        Func<string, Task> replaceAsync)
    {
        _clipboard = clipboard;
        _replaceAsync = replaceAsync;
        InitializeComponent();
        TitleText.Text = actionTitle;
        SetLoading(model, sourceText.Length);
    }

    public void SetLoading(string model, int sourceLength)
    {
        MetaText.Text = $"{model} · 正在处理 · 原文 {sourceLength} 字符";
        _rawResult = "";
        MarkdownHost.Visibility = Visibility.Collapsed;
        ResultBox.Visibility = Visibility.Visible;
        ResultBox.Text = "正在请求模型服务，请稍候...";
        ResultBox.IsReadOnly = true;
        LoadingBar.Visibility = Visibility.Visible;
        CopyButton.IsEnabled = false;
        ReplaceButton.IsEnabled = false;
    }

    public void BeginStreaming(string model, int sourceLength)
    {
        MetaText.Text = $"{model} · 正在生成 · 原文 {sourceLength} 字符";
        _rawResult = "";
        MarkdownHost.Visibility = Visibility.Collapsed;
        ResultBox.Visibility = Visibility.Visible;
        ResultBox.Text = "";
        ResultBox.IsReadOnly = true;
        LoadingBar.Visibility = Visibility.Visible;
        CopyButton.IsEnabled = false;
        ReplaceButton.IsEnabled = false;
    }

    public void AppendDelta(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        _rawResult += delta;
        ResultBox.AppendText(delta);
        ResultBox.ScrollToEnd();
    }

    public void SetResult(string resultText, string model, TimeSpan elapsed, int sourceLength)
    {
        _rawResult = resultText;
        var isMarkdown = MarkdownPreviewRenderer.LooksLikeMarkdown(resultText);
        MetaText.Text = isMarkdown
            ? $"{model} · {elapsed.TotalSeconds:0.0}s · Markdown 预览 · 原文 {sourceLength} 字符"
            : $"{model} · {elapsed.TotalSeconds:0.0}s · 原文 {sourceLength} 字符";

        if (isMarkdown)
        {
            MarkdownViewer.Document = MarkdownPreviewRenderer.Render(resultText);
            MarkdownHost.Visibility = Visibility.Visible;
            ResultBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            MarkdownHost.Visibility = Visibility.Collapsed;
            ResultBox.Visibility = Visibility.Visible;
            if (!string.Equals(ResultBox.Text, resultText, StringComparison.Ordinal))
            {
                ResultBox.Text = resultText;
            }
            ResultBox.IsReadOnly = false;
        }
        LoadingBar.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = true;
        ReplaceButton.IsEnabled = true;
    }

    public void SetError(string message, string model, int sourceLength)
    {
        MetaText.Text = $"{model} · 请求失败 · 原文 {sourceLength} 字符";
        _rawResult = "AI 请求失败：\r\n" + message;
        MarkdownHost.Visibility = Visibility.Collapsed;
        ResultBox.Visibility = Visibility.Visible;
        ResultBox.Text = _rawResult;
        ResultBox.IsReadOnly = true;
        LoadingBar.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = true;
        ReplaceButton.IsEnabled = false;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        _clipboard.SetText(CurrentRawText());
        Close();
    }

    private async void OnReplaceClicked(object sender, RoutedEventArgs e)
    {
        await _replaceAsync(CurrentRawText()).ConfigureAwait(true);
        Close();
    }

    private string CurrentRawText()
        => string.IsNullOrEmpty(_rawResult) ? ResultBox.Text : _rawResult;
}
