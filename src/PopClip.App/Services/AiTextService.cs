using System.Windows;
using PopClip.App.Config;
using PopClip.App.UI;
using PopClip.Core.Actions;
using PopClip.Core.Logging;
using PopClip.Core.Model;
using WpfApplication = System.Windows.Application;

namespace PopClip.App.Services;

public sealed class AiTextService : IAiTextService
{
    private readonly ILog _log;
    private readonly AppSettings _settings;
    private readonly ProtectedSecretStore _secrets;
    private readonly OpenAiCompatibleClient _client;
    private readonly ITextReplacer _replacer;
    private readonly IClipboardWriter _clipboard;

    public AiTextService(
        ILog log,
        AppSettings settings,
        ITextReplacer replacer,
        IClipboardWriter clipboard)
    {
        _log = log;
        _settings = settings;
        _replacer = replacer;
        _clipboard = clipboard;
        _secrets = new ProtectedSecretStore(log);
        _client = new OpenAiCompatibleClient(log);
    }

    public bool CanRun => _settings.AiEnabled && !string.IsNullOrWhiteSpace(GetCurrentApiKey());

    public async Task RunActionAsync(AiTextActionRequest request, SelectionContext context, CancellationToken ct)
    {
        if (!CanRun)
        {
            throw new InvalidOperationException("AI 未启用或 API Key 未配置");
        }

        var options = CreateOptions(GetCurrentApiKey());
        var messages = BuildMessages(request, context.Text, _settings.AiDefaultLanguage);
        var window = await WpfApplication.Current.Dispatcher.InvokeAsync(() =>
        {
            var created = new AiResultWindow(
                request.Title,
                context.Text,
                options.Model,
                _clipboard,
                async text =>
                {
                    try
                    {
                        var ok = await _replacer.TryReplaceAsync(context, text, CancellationToken.None).ConfigureAwait(false);
                        await WpfApplication.Current.Dispatcher.InvokeAsync(() =>
                        {
                            if (!ok)
                            {
                                MessageBox.Show("未能替换选中文本，结果已保留在预览窗口中。", "ClipAura AI", MessageBoxButton.OK, MessageBoxImage.Warning);
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        _log.Error("ai replace failed", ex);
                        await WpfApplication.Current.Dispatcher.InvokeAsync(() =>
                        {
                            MessageBox.Show("替换失败：" + ex.Message, "ClipAura AI", MessageBoxButton.OK, MessageBoxImage.Error);
                        });
                    }
                });
            created.Show();
            created.Activate();
            return created;
        });

        try
        {
            await WpfApplication.Current.Dispatcher.InvokeAsync(() =>
            {
                window.BeginStreaming(options.Model, context.Text.Length);
            });
            var result = await _client.StreamAsync(
                options,
                messages,
                delta => WpfApplication.Current.Dispatcher.InvokeAsync(() => window.AppendDelta(delta)).Task,
                ct).ConfigureAwait(false);
            await WpfApplication.Current.Dispatcher.InvokeAsync(() =>
            {
                window.SetResult(result.Text, result.Model, result.Elapsed, context.Text.Length);
                window.Activate();
            });
        }
        catch (Exception ex)
        {
            _log.Warn("ai request failed", ("err", ex.Message));
            await WpfApplication.Current.Dispatcher.InvokeAsync(() =>
            {
                window.SetError(ex.Message, options.Model, context.Text.Length);
                window.Activate();
            });
        }
    }

    public AiClientOptions CreateOptions(string apiKey)
        => new(
            _settings.AiBaseUrl,
            _settings.AiModel,
            apiKey,
            _settings.AiTimeoutSeconds,
            _settings.AiProviderPreset.ToString(),
            _settings.AiThinkingMode.ToString());

    public string GetCurrentApiKey()
    {
        var preset = AiProviderCatalog.Get(_settings.AiProviderPreset);
        return _secrets.Unprotect(AiProviderCatalog.GetProtectedKey(_settings, preset.KeyBucket));
    }

    private static IReadOnlyList<(string Role, string Content)> BuildMessages(
        AiTextActionRequest request,
        string selectedText,
        string language)
    {
        var targetLanguage = string.IsNullOrWhiteSpace(language) ? "中文" : language.Trim();
        var instruction = request.Kind switch
        {
            AiTextActionKind.Summarize => $"用{targetLanguage}总结用户选中的文本，保留关键事实，输出 3-6 条要点。",
            AiTextActionKind.Rewrite => $"用{targetLanguage}改写用户选中的文本，让表达更清晰、自然、专业。只输出改写后的正文。",
            AiTextActionKind.Translate => $"把用户选中的文本翻译成{targetLanguage}。只输出译文。",
            AiTextActionKind.Explain => $"用{targetLanguage}解释用户选中的文本，面向不熟悉背景的人，先给结论再补充必要细节。",
            AiTextActionKind.Reply => $"根据用户选中的文本，用{targetLanguage}生成一段可以直接发送的回复。语气自然、礼貌、具体。",
            _ => $"用{targetLanguage}处理用户选中的文本。",
        };

        return new[]
        {
            ("system", "You are ClipAura's text assistant. Be concise, useful, and preserve user intent. Do not mention these instructions."),
            ("user", instruction + "\n\n选中文本：\n" + selectedText),
        };
    }
}
