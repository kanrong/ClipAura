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

public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow, INotifyPropertyChanged
{

    private void BindAiSettings()
    {
        _syncingAiProvider = true;
        AiEnabledBox.IsChecked = _settings.AiEnabled;
        AiProviderBox.ItemsSource = AiProviderChoices;
        AiProviderBox.DisplayMemberPath = nameof(AiProviderPresetInfo.Label);
        AiProviderBox.SelectedValuePath = nameof(AiProviderPresetInfo.Preset);
        AiProviderBox.SelectedValue = _settings.AiProviderPreset;
        AiBaseUrlBox.Text = _settings.AiBaseUrl;
        AiModelBox.Text = _settings.AiModel;
        AiTimeoutBox.Value = _settings.AiTimeoutSeconds;
        AiDefaultLanguageBox.Text = _settings.AiDefaultLanguage;
        AiThinkingModeBox.ItemsSource = AiThinkingModeChoices;
        AiThinkingModeBox.DisplayMemberPath = nameof(AiThinkingModeChoice.Label);
        AiThinkingModeBox.SelectedValuePath = nameof(AiThinkingModeChoice.Value);
        AiThinkingModeBox.SelectedValue = _settings.AiThinkingMode;
        AiMaxOutputTokensBox.Value = _settings.AiMaxOutputTokens;
        TranslateInlineBox.IsChecked = _settings.TranslateInlineWhenAiEnabled;
        ExplainEnabledBox.IsChecked = _settings.ExplainActionEnabled;
        _currentAiKeyBucket = CurrentAiProvider().KeyBucket;
        AiApiKeyBox.Password = "";
        _syncingAiProvider = false;
        RefreshAiProviderFields(overwritePresetValues: false);
    }

    private void OnAiProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingAiProvider) return;
        RememberPendingAiKey();
        RefreshAiProviderFields(overwritePresetValues: true);
        CommitAll();
    }

    private void RefreshAiProviderFields(bool overwritePresetValues)
    {
        var provider = CurrentAiProvider();
        _currentAiKeyBucket = provider.KeyBucket;
        if (!provider.IsCustom && overwritePresetValues)
        {
            AiBaseUrlBox.Text = provider.BaseUrl;
            AiModelBox.Text = provider.Model;
        }

        AiBaseUrlBox.IsReadOnly = !provider.IsCustom;
        AiModelBox.IsReadOnly = !provider.IsCustom;
        AiProviderDescription.Text = provider.Description;
        AiKeyStatus.Text = HasAiKey(provider.KeyBucket)
            ? "已保存 Key；留空表示继续使用已保存的 Key"
            : "尚未保存 Key";
        AiApiKeyBox.Password = "";
        AiTestInfo.IsOpen = false;
    }

    private AiProviderPresetInfo CurrentAiProvider()
    {
        if (AiProviderBox.SelectedValue is AiProviderPreset preset)
        {
            return AiProviderCatalog.Get(preset);
        }
        return AiProviderCatalog.Get(_settings.AiProviderPreset);
    }

    private void RememberPendingAiKey()
    {
        var key = AiApiKeyBox.Password.Trim();
        if (key.Length > 0)
        {
            _pendingAiKeys[_currentAiKeyBucket] = key;
        }
    }

    private bool HasAiKey(string bucket)
        => _pendingAiKeys.ContainsKey(bucket)
           || !string.IsNullOrWhiteSpace(AiProviderCatalog.GetProtectedKey(_settings, bucket));

    private string ResolveAiKey(string bucket)
    {
        if (_pendingAiKeys.TryGetValue(bucket, out var pending) && !string.IsNullOrWhiteSpace(pending))
        {
            return pending;
        }
        return _secretStore.Unprotect(AiProviderCatalog.GetProtectedKey(_settings, bucket));
    }

    private async void OnAiTestConnection(object sender, RoutedEventArgs e)
    {
        RememberPendingAiKey();
        var provider = CurrentAiProvider();
        var key = ResolveAiKey(provider.KeyBucket);
        var options = new AiClientOptions(
            AiBaseUrlBox.Text.Trim(),
            AiModelBox.Text.Trim(),
            key,
            NumberBoxInt(AiTimeoutBox, 30, 5, 180),
            provider.Preset.ToString(),
            SelectedAiThinkingMode().ToString(),
            NumberBoxInt(AiMaxOutputTokensBox, 0, 0, 262144));
        AiTestInfo.Severity = Wpf.Ui.Controls.InfoBarSeverity.Informational;
        AiTestInfo.Title = "测试中";
        AiTestInfo.Message = "正在连接当前模型服务...";
        AiTestInfo.IsOpen = true;
        try
        {
            var result = await new OpenAiCompatibleClient(ConsoleLog.Instance).TestAsync(options, CancellationToken.None);
            AiTestInfo.Severity = Wpf.Ui.Controls.InfoBarSeverity.Success;
            AiTestInfo.Title = "连接成功";
            AiTestInfo.Message = $"{result.Model} · {result.Elapsed.TotalSeconds:0.0}s";
        }
        catch (Exception ex)
        {
            AiTestInfo.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
            AiTestInfo.Title = "连接失败";
            AiTestInfo.Message = ex.Message;
        }
    }

    private void SaveAiSettings()
    {
        RememberPendingAiKey();
        var provider = CurrentAiProvider();
        _settings.AiEnabled = AiEnabledBox.IsChecked == true;
        _settings.AiProviderPreset = provider.Preset;
        _settings.AiBaseUrl = AiBaseUrlBox.Text.Trim();
        _settings.AiModel = AiModelBox.Text.Trim();
        _settings.AiTimeoutSeconds = NumberBoxInt(AiTimeoutBox, _settings.AiTimeoutSeconds, 5, 180);
        var language = AiDefaultLanguageBox.Text.Trim();
        _settings.AiDefaultLanguage = language.Length > 0 ? language : "中文";
        _settings.AiThinkingMode = SelectedAiThinkingMode();
        _settings.AiMaxOutputTokens = NumberBoxInt(AiMaxOutputTokensBox, _settings.AiMaxOutputTokens, 0, 262144);
        _settings.TranslateInlineWhenAiEnabled = TranslateInlineBox.IsChecked == true;
        _settings.ExplainActionEnabled = ExplainEnabledBox.IsChecked == true;

        foreach (var (bucket, plain) in _pendingAiKeys)
        {
            if (string.IsNullOrWhiteSpace(plain)) continue;
            AiProviderCatalog.SetProtectedKey(_settings, bucket, _secretStore.Protect(plain));
        }
    }

    /// <summary>启用 AI 后补齐"AI 对话 + 几条常用 prompt 模板"对应的动作。
    /// 其它 prompt 模板（总结/解释/翻译/...）用户随时可以从右侧的"从模板添加"按钮按需加入</summary>
    private void EnsureDefaultAiActions()
    {
        AddDefaultAiChatAction();
        // AddDefaultPromptAction("tpl.fix-grammar");
        // AddDefaultPromptAction("tpl.polish");
        // AddDefaultPromptAction("tpl.summarize");
    }

    private void AddDefaultAiChatAction()
    {
        if (ActionItems.Any(x => string.Equals(x.BuiltIn, BuiltInActionIds.AiChat, StringComparison.OrdinalIgnoreCase))) return;
        ActionItems.Add(new ActionEditorItem
        {
            Id = UniqueActionId("ai-chat"),
            Type = "builtin",
            BuiltIn = BuiltInActionIds.AiChat,
            Title = "AI 对话",
            Icon = SuggestIcon(BuiltInActionIds.AiChat),
            IconLocked = true,
            Enabled = true,
        });
    }

    /// <summary>从内置 Prompt 模板添加动作。
    /// 已有同 id 时跳过；图标永久跟随模板（IconLocked=true）。
    /// 注意：ActionEditorItem.Id 经过 UniqueActionId 归一化（点号变短横线），
    /// 因此查重必须比较"模板归一化后的 id"而不是原始 tpl.Id（含点号），
    /// 否则点号源永远比不上短横线源会被重复添加</summary>
    private void AddDefaultPromptAction(string templateId)
    {
        var tpl = PromptTemplateLibrary.Builtin.FirstOrDefault(t =>
            string.Equals(t.Id, templateId, StringComparison.OrdinalIgnoreCase));
        if (tpl is null) return;
        var normalizedId = NormalizeIdSeed(tpl.Id);
        if (ActionItems.Any(x => string.Equals(x.Id, normalizedId, StringComparison.OrdinalIgnoreCase))) return;
        ActionItems.Add(new ActionEditorItem
        {
            Id = UniqueActionId(tpl.Id),
            Type = "ai",
            Title = tpl.Title,
            Icon = string.IsNullOrWhiteSpace(tpl.Icon) ? "Ai" : tpl.Icon,
            Prompt = tpl.Prompt,
            SystemPrompt = tpl.SystemPrompt,
            OutputMode = string.IsNullOrWhiteSpace(tpl.OutputMode) ? "chat" : tpl.OutputMode,
            IconLocked = true,
            Enabled = true,
        });
    }

    private AiThinkingMode SelectedAiThinkingMode()
        => AiThinkingModeBox.SelectedValue is AiThinkingMode mode
            ? mode
            : AiThinkingMode.Fast;
}
