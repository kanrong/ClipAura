using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using PopClip.App.Services;
using PopClip.Core.Actions;

namespace PopClip.App.UI;

public partial class AiResultWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly IClipboardWriter _clipboard;
    private readonly Func<string, Task> _replaceAsync;
    private readonly Func<IReadOnlyList<(string Role, string Content)>, Func<string, Task>, CancellationToken, Task<AiCompletionResult>> _sendAsync;
    private readonly bool _canReplace;
    private readonly List<(string Role, string Content)> _history = new();
    private readonly CancellationTokenSource _windowCts = new();
    private TextBox? _streamingTextBox;
    private StackPanel? _streamingMessageStack;
    private CancellationTokenSource? _sendCts;
    private string _latestAssistantText = "";
    private string _streamingAssistantText = "";
    private bool _isSending;

    public AiResultWindow(
        string actionTitle,
        string sourceText,
        string model,
        IClipboardWriter clipboard,
        Func<string, Task> replaceAsync,
        bool canReplace,
        Func<IReadOnlyList<(string Role, string Content)>, Func<string, Task>, CancellationToken, Task<AiCompletionResult>> sendAsync)
    {
        _clipboard = clipboard;
        _replaceAsync = replaceAsync;
        _canReplace = canReplace;
        _sendAsync = sendAsync;
        InitializeComponent();

        var titleText = string.IsNullOrWhiteSpace(actionTitle) ? "ClipAura AI" : actionTitle;
        Title = titleText;
        AppTitleBar.Title = titleText;

        ReferenceText.Text = sourceText;
        ReferenceExpander.Header = BuildReferenceHeader(sourceText);
        ReferencePanel.Visibility = string.IsNullOrWhiteSpace(sourceText) ? Visibility.Collapsed : Visibility.Visible;
        SetIdle(model, sourceText.Length);
        AddAssistantMessage("已准备好。你可以基于引用文本继续提问。");
        Loaded += (_, _) => FocusQuestionBox();
        Closed += (_, _) =>
        {
            _sendCts?.Cancel();
            _windowCts.Cancel();
            _windowCts.Dispose();
        };
    }

    public void StartInitialPrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        _ = SubmitPromptAsync(prompt, clearInput: false);
    }

    public void FocusQuestionBox()
    {
        QuestionBox.Focus();
        Keyboard.Focus(QuestionBox);
    }

    private void SetIdle(string model, int sourceLength)
    {
        MetaText.Text = $"{model} · 会话就绪 · 引用 {sourceLength} 字符";
        LoadingBar.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = false;
        ReplaceButton.IsEnabled = false;
    }

    private void SetSending(bool sending)
    {
        _isSending = sending;
        LoadingBar.Visibility = sending ? Visibility.Visible : Visibility.Collapsed;
        SendButton.IsEnabled = !sending;
        QuestionBox.IsEnabled = !sending;
        if (!sending)
        {
            FocusQuestionBox();
        }
    }

    private async Task SubmitPromptAsync(string prompt, bool clearInput = true)
    {
        var trimmed = prompt.Trim();
        if (_isSending || trimmed.Length == 0) return;

        if (clearInput)
        {
            QuestionBox.Text = "";
        }

        AddUserMessage(trimmed);
        _history.Add(("user", trimmed));
        StartAssistantStreamingMessage();
        SetSending(true);
        MetaText.Text = "正在请求模型服务...";

        _sendCts?.Cancel();
        _sendCts?.Dispose();
        _sendCts = CancellationTokenSource.CreateLinkedTokenSource(_windowCts.Token);

        try
        {
            var snapshot = _history.ToArray();
            var result = await _sendAsync(
                snapshot,
                delta => Dispatcher.InvokeAsync(() => AppendAssistantDelta(delta)).Task,
                _sendCts.Token).ConfigureAwait(true);

            _latestAssistantText = result.Text;
            _history.Add(("assistant", result.Text));
            FinishAssistantMessage(result.Text);
            var markdown = MarkdownPreviewRenderer.LooksLikeMarkdown(result.Text) ? " · Markdown" : "";
            MetaText.Text = $"{result.Model} · {result.Elapsed.TotalSeconds:0.0}s{markdown}";
            CopyButton.IsEnabled = true;
            ReplaceButton.IsEnabled = _canReplace;
        }
        catch (OperationCanceledException) when (_windowCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var message = "AI 请求失败：\r\n" + ex.Message;
            _latestAssistantText = message;
            FinishAssistantMessage(message, isError: true);
            MetaText.Text = "请求失败";
            CopyButton.IsEnabled = true;
            ReplaceButton.IsEnabled = false;
        }
        finally
        {
            if (!_windowCts.IsCancellationRequested)
            {
                SetSending(false);
            }
        }
    }

    private void AddUserMessage(string text)
        => AddMessage("你", text, isUser: true, isError: false);

    private void AddAssistantMessage(string text)
        => AddMessage("ClipAura AI", text, isUser: false, isError: false);

    private void StartAssistantStreamingMessage()
    {
        _streamingAssistantText = "";
        _streamingMessageStack = CreateMessageShell("ClipAura AI", isUser: false, isError: false);
        _streamingTextBox = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _streamingTextBox.SetResourceReference(TextBox.ForegroundProperty, "Settings.Foreground");
        _streamingMessageStack.Children.Add(_streamingTextBox);
        ScrollToEnd();
    }

    private void AppendAssistantDelta(string delta)
    {
        if (_streamingTextBox is null || string.IsNullOrEmpty(delta)) return;
        _streamingAssistantText += delta;
        _streamingTextBox.AppendText(delta);
        _streamingTextBox.ScrollToEnd();
        ScrollToEnd();
    }

    private void FinishAssistantMessage(string text, bool isError = false)
    {
        if (_streamingMessageStack is null)
        {
            AddMessage("ClipAura AI", text, isUser: false, isError: isError);
            return;
        }

        if (_streamingTextBox is not null)
        {
            _streamingMessageStack.Children.Remove(_streamingTextBox);
        }
        AddMessageContent(_streamingMessageStack, text, isAssistant: true, isError: isError);
        _streamingTextBox = null;
        _streamingMessageStack = null;
        ScrollToEnd();
    }

    private void AddMessage(string author, string text, bool isUser, bool isError)
    {
        var stack = CreateMessageShell(author, isUser, isError);
        AddMessageContent(stack, text, isAssistant: !isUser, isError);
        ScrollToEnd();
    }

    private StackPanel CreateMessageShell(string author, bool isUser, bool isError)
    {
        var border = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Margin = isUser ? new Thickness(48, 0, 0, 8) : new Thickness(0, 0, 24, 8),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            MaxWidth = isUser ? 620 : double.PositiveInfinity,
        };
        border.SetResourceReference(Border.CornerRadiusProperty, "Settings.Radius.Md");
        border.SetResourceReference(Border.BorderBrushProperty, isError ? "Settings.Danger" : "Settings.Stroke");
        border.BorderThickness = new Thickness(1);
        border.SetResourceReference(Border.BackgroundProperty,
            isError ? "Settings.DangerSoft" : isUser ? "Settings.AccentSoft" : "Settings.Card.SubtleBackground");

        var stack = new StackPanel();
        var header = new TextBlock
        {
            Text = author,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4),
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "Settings.SubtleForeground");
        stack.Children.Add(header);
        border.Child = stack;
        MessagesPanel.Children.Add(border);
        return stack;
    }

    private void AddMessageContent(StackPanel stack, string text, bool isAssistant, bool isError)
    {
        if (isAssistant && !isError && MarkdownPreviewRenderer.LooksLikeMarkdown(text))
        {
            var viewer = new FlowDocumentScrollViewer
            {
                Document = MarkdownPreviewRenderer.Render(text),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 420,
                Margin = new Thickness(0),
            };
            stack.Children.Add(viewer);
            return;
        }

        var body = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "Settings.Foreground");
        stack.Children.Add(body);
    }

    private void ScrollToEnd()
        => Dispatcher.BeginInvoke(() => MessagesScrollViewer.ScrollToEnd());

    private async void OnSendClicked(object sender, RoutedEventArgs e)
        => await SubmitPromptAsync(QuestionBox.Text).ConfigureAwait(true);

    private async void OnQuestionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await SubmitPromptAsync(QuestionBox.Text).ConfigureAwait(true);
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        _clipboard.SetText(CurrentRawText());
    }

    private async void OnReplaceClicked(object sender, RoutedEventArgs e)
    {
        if (!_canReplace) return;
        await _replaceAsync(CurrentRawText()).ConfigureAwait(true);
    }

    private string CurrentRawText()
        => string.IsNullOrWhiteSpace(_latestAssistantText)
            ? _streamingAssistantText
            : _latestAssistantText;

    private static string BuildReferenceHeader(string sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return "原文";
        }

        // 用双引号 + 单行预览代替静态标签"引用文本"，使折叠状态下也能瞥到原文。
        var collapsed = System.Text.RegularExpressions.Regex.Replace(sourceText, @"\s+", " ").Trim();
        const int maxLen = 60;
        var preview = collapsed.Length <= maxLen
            ? collapsed
            : collapsed[..maxLen] + "…";
        return $"\u201C{preview}\u201D";
    }
}
