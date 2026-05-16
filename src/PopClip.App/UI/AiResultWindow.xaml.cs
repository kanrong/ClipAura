using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PopClip.App.Services;
using PopClip.Core.Actions;

namespace PopClip.App.UI;

public partial class AiResultWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly IClipboardWriter _clipboard;
    private readonly Func<string, Task> _replaceAsync;
    private readonly Func<IReadOnlyList<(string Role, string Content)>, AiStreamCallbacks, CancellationToken, Task<AiCompletionResult>> _sendAsync;
    private readonly Action<ConversationSnapshot>? _onSessionFinalize;
    private readonly bool _canReplace;
    private readonly List<(string Role, string Content)> _history = new();
    private readonly CancellationTokenSource _windowCts = new();
    private TextBox? _streamingTextBox;
    private StackPanel? _streamingMessageStack;
    private Border? _streamingReasoningHost;
    private TextBox? _streamingReasoningTextBox;
    private TextBlock? _streamingReasoningStatus;
    private CancellationTokenSource? _sendCts;
    private string _latestAssistantText = "";
    private string _streamingAssistantText = "";
    private string _streamingReasoningText = "";
    private bool _isSending;
    private int _totalPromptTokens;
    private int _totalCompletionTokens;
    private DateTime? _reasoningStartedAt;
    private string _modelLabel = "";

    public AiResultWindow(
        string actionTitle,
        string sourceText,
        string model,
        IClipboardWriter clipboard,
        Func<string, Task> replaceAsync,
        bool canReplace,
        Func<IReadOnlyList<(string Role, string Content)>, AiStreamCallbacks, CancellationToken, Task<AiCompletionResult>> sendAsync,
        Action<ConversationSnapshot>? onSessionFinalize = null)
    {
        _clipboard = clipboard;
        _replaceAsync = replaceAsync;
        _canReplace = canReplace;
        _sendAsync = sendAsync;
        _onSessionFinalize = onSessionFinalize;
        _modelLabel = model;
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
        Closed += OnWindowClosed;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _sendCts?.Cancel();
        _windowCts.Cancel();
        try
        {
            // 只在产生过至少一次助手回复时才持久化，避免存空对话
            if (_history.Any(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)))
            {
                _onSessionFinalize?.Invoke(new ConversationSnapshot(_history.ToArray(), _totalPromptTokens, _totalCompletionTokens));
            }
        }
        catch { /* 关闭路径上不能抛 */ }
        _windowCts.Dispose();
    }

    public void StartInitialPrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        _ = SubmitPromptAsync(prompt, clearInput: false);
    }

    /// <summary>把"已经在气泡里完成的一轮 user→assistant"直接灌进对话，
    /// 不重新调用 AI。语义：用户在 AiBubbleWindow 看到结果后点"打开完整对话"，
    /// 完整窗口应该承接气泡的已有内容，让用户在此基础上继续追问，
    /// 而不是重头跑一遍相同的 prompt（既浪费 token，又会让用户多等一次流式时间）</summary>
    public void SeedAssistantTurn(string userPrompt, string assistantText)
    {
        if (string.IsNullOrWhiteSpace(userPrompt) || string.IsNullOrWhiteSpace(assistantText)) return;

        AddUserMessage(userPrompt);
        _history.Add(("user", userPrompt));
        AddAssistantMessage(assistantText);
        _history.Add(("assistant", assistantText));

        _latestAssistantText = assistantText;
        CopyButton.IsEnabled = true;
        ReplaceButton.IsEnabled = _canReplace;
        MetaText.Text = $"{_modelLabel} · 已迁移自气泡 · 引用 {ReferenceText.Text?.Length ?? 0} 字符";
        ScrollToEnd();
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
        StopButton.Visibility = sending ? Visibility.Visible : Visibility.Collapsed;
        QuestionBox.IsEnabled = !sending;
        VariantPanel.IsEnabled = !sending;
        if (!sending)
        {
            FocusQuestionBox();
        }
    }

    private void OnStopClicked(object sender, RoutedEventArgs e)
    {
        _sendCts?.Cancel();
    }

    private async void OnRegenerateClicked(object sender, RoutedEventArgs e)
    {
        if (_isSending) return;
        // 找到最近一对 user/assistant 消息：删掉 assistant 重新发起
        for (var i = _history.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_history[i].Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                var lastUser = _history[i].Content;
                if (i + 1 < _history.Count
                    && string.Equals(_history[i + 1].Role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    _history.RemoveAt(i + 1);
                }
                // 同时把 user 拿掉，再走标准 Submit 流程重新展示一对消息
                _history.RemoveAt(i);
                RebuildMessagesPanel();
                await SubmitPromptAsync(lastUser, clearInput: false).ConfigureAwait(true);
                return;
            }
        }
    }

    private async void OnContinueClicked(object sender, RoutedEventArgs e)
    {
        if (_isSending) return;
        await SubmitPromptAsync("请继续。", clearInput: false).ConfigureAwait(true);
    }

    /// <summary>变体 chip 点击：把"再来一次/更短/更长/...等"作为一次新的 user 消息追问。
    /// 不修改原 assistant 消息，让用户保留多个版本对比</summary>
    private async void OnVariantClicked(object sender, RoutedEventArgs e)
    {
        if (_isSending) return;
        if (sender is not FrameworkElement fe || fe.Tag is not string variant) return;
        if (string.IsNullOrWhiteSpace(_latestAssistantText) && string.IsNullOrWhiteSpace(_streamingAssistantText))
        {
            return;
        }
        await SubmitPromptAsync(variant, clearInput: false).ConfigureAwait(true);
    }

    private void RebuildMessagesPanel()
    {
        MessagesPanel.Children.Clear();
        // 初始问候
        AddAssistantMessage("已准备好。你可以基于引用文本继续提问。");
        foreach (var (role, content) in _history)
        {
            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
            {
                AddUserMessage(content);
            }
            else if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                AddAssistantMessage(content);
            }
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
            var callbacks = new AiStreamCallbacks(
                delta => Dispatcher.InvokeAsync(() => AppendAssistantDelta(delta)).Task,
                delta => Dispatcher.InvokeAsync(() => AppendReasoningDelta(delta)).Task);
            var result = await _sendAsync(snapshot, callbacks, _sendCts.Token).ConfigureAwait(true);

            _latestAssistantText = result.Text;
            _history.Add(("assistant", result.Text));
            _totalPromptTokens += result.PromptTokens;
            _totalCompletionTokens += result.CompletionTokens;
            _modelLabel = result.Model;
            FinishAssistantMessage(result.Text, reasoning: result.Reasoning);
            UpdateMeta(result);
            CopyButton.IsEnabled = true;
            ReplaceButton.IsEnabled = _canReplace;
        }
        catch (OperationCanceledException) when (_windowCts.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            // 用户主动停止：流式结果保留为可观察文本，不算错误
            FinalizeStreamingAsCancelled();
            MetaText.Text = $"{_modelLabel} · 已停止";
            CopyButton.IsEnabled = !string.IsNullOrWhiteSpace(_streamingAssistantText) || !string.IsNullOrWhiteSpace(_latestAssistantText);
            ReplaceButton.IsEnabled = false;
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

    private void FinalizeStreamingAsCancelled()
    {
        if (_streamingMessageStack is null) return;
        if (_streamingTextBox is not null && string.IsNullOrEmpty(_streamingAssistantText))
        {
            _streamingMessageStack.Children.Remove(_streamingTextBox);
            AddMessageContent(_streamingMessageStack, "（已停止）", isAssistant: true, isError: false);
        }
        else
        {
            // 把流式 TextBox 留作正文，只补一个停止说明
            var tag = new TextBlock
            {
                Text = "（已停止）",
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
                FontStyle = FontStyles.Italic,
            };
            tag.SetResourceReference(TextBlock.ForegroundProperty, "Settings.Muted");
            _streamingMessageStack.Children.Add(tag);
            _latestAssistantText = _streamingAssistantText;
            _history.Add(("assistant", _streamingAssistantText));
        }
        _streamingTextBox = null;
        _streamingMessageStack = null;
        _streamingReasoningHost = null;
        _streamingReasoningTextBox = null;
        _streamingReasoningStatus = null;
    }

    private void UpdateMeta(AiCompletionResult result)
    {
        var markdown = MarkdownPreviewRenderer.LooksLikeMarkdown(result.Text) ? " · Markdown" : "";
        // tok 行：思考模型把 reasoning 单独标出来，便于用户判断 reasoning 是否吃掉了多数 completion 额度
        // 例：12→3408 tok (think 2800)
        var tokens = "";
        if (result.PromptTokens > 0 || result.CompletionTokens > 0)
        {
            tokens = $" · {result.PromptTokens}→{result.CompletionTokens} tok";
            if (result.ReasoningTokens > 0)
            {
                tokens += $" (think {result.ReasoningTokens})";
            }
        }
        MetaText.Text = $"{result.Model} · {result.Elapsed.TotalSeconds:0.0}s{tokens}{markdown}";
    }

    private void AddUserMessage(string text)
        => AddMessage("你", text, isUser: true, isError: false);

    private void AddAssistantMessage(string text)
        => AddMessage("ClipAura AI", text, isUser: false, isError: false);

    private void StartAssistantStreamingMessage()
    {
        _streamingAssistantText = "";
        _streamingReasoningText = "";
        _reasoningStartedAt = null;
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

    /// <summary>把思考通道的 delta 累加到流式消息上方一个可折叠的小字号区域。
    /// 设计要点：默认折叠 + 与正文同区分隔 + 字号比正文小 1-2pt + 字色比正文淡两级
    /// 避免抢占主体；用户想看时点开 Expander 即可</summary>
    private void AppendReasoningDelta(string delta)
    {
        if (_streamingMessageStack is null || string.IsNullOrEmpty(delta)) return;
        EnsureReasoningHost();
        _streamingReasoningText += delta;
        if (_streamingReasoningTextBox is not null)
        {
            _streamingReasoningTextBox.AppendText(delta);
            _streamingReasoningTextBox.ScrollToEnd();
        }
        UpdateReasoningStatus(streaming: true);
    }

    private void EnsureReasoningHost()
    {
        if (_streamingReasoningHost is not null || _streamingMessageStack is null) return;

        _reasoningStartedAt ??= DateTime.UtcNow;

        var status = new TextBlock
        {
            FontSize = 10.5,
            FontStyle = FontStyles.Italic,
            Text = "思考中...",
        };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Settings.Muted");
        _streamingReasoningStatus = status;

        var caret = new System.Windows.Shapes.Path
        {
            Width = 8,
            Height = 8,
            Stretch = Stretch.Uniform,
            Data = Geometry.Parse("M 0 0 L 8 4 L 0 8 Z"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        caret.SetResourceReference(System.Windows.Shapes.Path.FillProperty, "Settings.Muted");

        var headerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
        };
        var label = new TextBlock
        {
            Text = "思考过程",
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Settings.SubtleForeground");
        headerPanel.Children.Add(caret);
        headerPanel.Children.Add(label);
        headerPanel.Children.Add(new TextBlock
        {
            Text = " · ",
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
            Opacity = 0.6,
        });
        headerPanel.Children.Add(status);

        // textBox 先声明，下面 header 的 lambda 用闭包持有它的引用，
        // 这样即便流式结束后 _streamingReasoningTextBox 字段被置 null，header 上的点击切换依然有效
        var textBox = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            FontSize = 11.5,
            Padding = new Thickness(0, 4, 0, 0),
            Margin = new Thickness(0, 2, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = Visibility.Collapsed,
        };
        textBox.SetResourceReference(TextBox.ForegroundProperty, "Settings.Muted");
        _streamingReasoningTextBox = textBox;

        // 用 Border + PreviewMouseLeftButtonDown 自实现折叠头，避免 ToggleButton 在 Wpf.Ui 全局样式下
        // 对自定义子内容 hit-test 不可靠；用 Preview 阶段（tunneling）抢先处理，避免被任何下游控件抢占
        // 关键点：Background = Transparent（非 null）才能让整个矩形参与 hit-test
        var header = new Border
        {
            Background = System.Windows.Media.Brushes.Transparent,
            Padding = new Thickness(2, 2, 6, 2),
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = headerPanel,
        };
        // 闭包持有 textBox/caret 本地引用：不依赖 _streamingReasoningTextBox 字段，
        // 流式结束后字段被清空也不影响这里的开合
        header.PreviewMouseLeftButtonDown += (_, args) =>
        {
            args.Handled = true;
            var expand = textBox.Visibility != Visibility.Visible;
            textBox.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            // caret 三角朝向：折叠时向右（▶），展开时向下（▼）
            caret.RenderTransform = expand ? new RotateTransform(90, 4, 4) : null;
            if (expand) ScrollToEnd();
        };

        var host = new Border
        {
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(8, 4, 8, 6),
            BorderThickness = new Thickness(1),
        };
        host.SetResourceReference(Border.BorderBrushProperty, "Settings.Stroke");
        host.SetResourceReference(Border.BackgroundProperty, "Settings.Card.SubtleBackground");
        host.SetResourceReference(Border.CornerRadiusProperty, "Settings.Radius.Sm");

        var inner = new StackPanel();
        inner.Children.Add(header);
        inner.Children.Add(textBox);
        host.Child = inner;

        // 插入到正文上方（流式正文是 stack 的最后一个；思考块要在它前面）
        var insertIndex = _streamingMessageStack.Children.Count > 0
            ? _streamingMessageStack.Children.Count - 1
            : 0;
        // header 是第 0 个子元素，需要保持在最前
        if (insertIndex < 1) insertIndex = _streamingMessageStack.Children.Count;
        _streamingMessageStack.Children.Insert(insertIndex, host);
        _streamingReasoningHost = host;
    }

    private void UpdateReasoningStatus(bool streaming)
    {
        if (_streamingReasoningStatus is null) return;
        if (_reasoningStartedAt is null)
        {
            _streamingReasoningStatus.Text = "思考中...";
            return;
        }
        var elapsed = DateTime.UtcNow - _reasoningStartedAt.Value;
        var label = streaming ? "思考中" : "已思考";
        _streamingReasoningStatus.Text = elapsed.TotalSeconds < 1
            ? $"{label} ({elapsed.TotalMilliseconds:0} ms)"
            : $"{label} {elapsed.TotalSeconds:0.0}s";
    }

    private void FinishAssistantMessage(string text, bool isError = false, string reasoning = "")
    {
        if (_streamingMessageStack is null)
        {
            AddMessage("ClipAura AI", text, isUser: false, isError: isError);
            return;
        }

        // 非流式响应（CompleteAsync）也可能携带 reasoning，但 stream 未走 callback；
        // 这里如果流式没建过 reasoning 容器、却拿到了 final reasoning，则建一次直接塞进去
        if (!isError && !string.IsNullOrWhiteSpace(reasoning) && _streamingReasoningHost is null)
        {
            EnsureReasoningHost();
            _streamingReasoningText = reasoning;
            _streamingReasoningTextBox!.Text = reasoning;
        }

        // 流式状态文字定格为最终耗时
        UpdateReasoningStatus(streaming: false);

        if (_streamingTextBox is not null)
        {
            _streamingMessageStack.Children.Remove(_streamingTextBox);
        }
        AddMessageContent(_streamingMessageStack, text, isAssistant: true, isError: isError);
        _streamingTextBox = null;
        _streamingMessageStack = null;
        _streamingReasoningHost = null;
        _streamingReasoningTextBox = null;
        _streamingReasoningStatus = null;
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
