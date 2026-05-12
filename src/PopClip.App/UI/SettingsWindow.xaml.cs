using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PopClip.Actions.BuiltIn;
using PopClip.App.Config;
using PopClip.App.Services;
using PopClip.Core.Actions;
using PopClip.Core.Logging;
using PopClip.Core.Session;
using PopClip.Hooks;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;

namespace PopClip.App.UI;

public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    private static readonly (string Name, string Url)[] Presets =
    {
        ("Google", "https://www.google.com/search?q={q}"),
        ("Bing", "https://www.bing.com/search?q={q}"),
        ("百度", "https://www.baidu.com/s?wd={q}"),
    };

    private readonly ConfigStore _store;
    private readonly AppSettings _settings;
    private readonly ProtectedSecretStore _secretStore = new(ConsoleLog.Instance);
    private readonly Dictionary<string, string> _pendingAiKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FrameworkElement> _pages;
    private WpfPoint _dragStartPoint;
    private ActionEditorItem? _dragItem;
    private string _currentAiKeyBucket = AiProviderCatalog.DeepSeekKeyBucket;
    private bool _syncingAiProvider;

    public ObservableCollection<string> ProcessFilters { get; } = new();
    public ObservableCollection<ActionEditorItem> ActionItems { get; } = new();
    public IReadOnlyList<AiProviderPresetInfo> AiProviderChoices => AiProviderCatalog.All;
    public IReadOnlyList<AiThinkingModeChoice> AiThinkingModeChoices { get; } = new[]
    {
        new AiThinkingModeChoice(AiThinkingMode.Auto, "自动", "使用当前服务商默认策略"),
        new AiThinkingModeChoice(AiThinkingMode.Fast, "快速", "DeepSeek 关闭 thinking；OpenAI 使用 low reasoning"),
        new AiThinkingModeChoice(AiThinkingMode.Deep, "深度", "DeepSeek 启用 thinking + max；OpenAI 使用 high reasoning"),
    };
    public IReadOnlyList<ActionTypeChoice> ActionTypeChoices { get; } = new[]
    {
        new ActionTypeChoice("builtin", "内置"),
        new ActionTypeChoice("url-template", "URL 模板"),
        new ActionTypeChoice("script", "脚本"),
    };
    public IReadOnlyList<BuiltInChoice> BuiltInChoices { get; } = new[]
    {
        new BuiltInChoice(BuiltInActionIds.Copy, "复制"),
        new BuiltInChoice(BuiltInActionIds.OpenUrl, "打开链接"),
        new BuiltInChoice(BuiltInActionIds.Mailto, "发送邮件"),
        new BuiltInChoice(BuiltInActionIds.Search, "搜索"),
        new BuiltInChoice(BuiltInActionIds.Translate, "翻译"),
        new BuiltInChoice(BuiltInActionIds.ToUpper, "大写"),
        new BuiltInChoice(BuiltInActionIds.ToLower, "小写"),
        new BuiltInChoice(BuiltInActionIds.ToTitle, "标题大小写"),
        new BuiltInChoice(BuiltInActionIds.Calculate, "计算"),
        new BuiltInChoice(BuiltInActionIds.WordCount, "字数统计"),
        new BuiltInChoice(BuiltInActionIds.AiSummarize, "AI 总结"),
        new BuiltInChoice(BuiltInActionIds.AiRewrite, "AI 改写"),
        new BuiltInChoice(BuiltInActionIds.AiTranslate, "AI 翻译"),
        new BuiltInChoice(BuiltInActionIds.AiExplain, "AI 解释"),
        new BuiltInChoice(BuiltInActionIds.AiReply, "AI 回复"),
    };

    public event Action? Saved;

    public SettingsWindow(ConfigStore store, AppSettings settings, string? initialPage = null)
    {
        _store = store;
        _settings = settings;
        // FluentWindow 自己会按 WindowBackdropType 处理 Mica，再走 ApplicationThemeManager 切深浅色
        ApplyWpfUiTheme();
        InitializeComponent();
        DataContext = this;
        // 系统亮暗主题切换时自动同步 WPF-UI 控件主题与窗口背景
        SourceInitialized += (_, _) =>
        {
            try
            {
                Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this, Wpf.Ui.Controls.WindowBackdropType.Mica, updateAccents: true);
            }
            catch
            {
                // 某些 Win10 旧版本不支持 Mica，watcher 会回退；任何异常都不影响功能
            }
        };
        ApplyThemeResources();
        _pages = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["General"] = GeneralPage,
            ["Appearance"] = AppearancePage,
            ["Actions"] = ActionsPage,
            ["Processes"] = ProcessesPage,
            ["Search"] = SearchPage,
            ["Hotkeys"] = HotkeysPage,
            ["AI"] = AIPage,
            ["About"] = AboutPage,
        };
        Bind();
        if (!string.IsNullOrEmpty(initialPage) && _pages.ContainsKey(initialPage))
        {
            NavigateTo(initialPage);
        }
        else
        {
            NavigationList.SelectedIndex = 0;
        }
    }

    private static void ApplyWpfUiTheme()
    {
        var theme = SystemThemeHelper.IsSystemDark()
            ? Wpf.Ui.Appearance.ApplicationTheme.Dark
            : Wpf.Ui.Appearance.ApplicationTheme.Light;
        try
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(theme, Wpf.Ui.Controls.WindowBackdropType.None, updateAccent: true);
        }
        catch
        {
            // ApplicationThemeManager 偶尔在测试 / 设计器环境抛异常；忽略不影响功能
        }
    }

    /// <summary>深色模式时覆盖 Settings.* 主题画刷。
    /// 走 Resources 实例字典而非全局 ResourceDictionary，避免污染其他窗口</summary>
    private void ApplyThemeResources()
    {
        if (!SystemThemeHelper.IsSystemDark()) return;

        SolidColorBrush B(byte r, byte g, byte b, byte a = 0xFF) =>
            new(System.Windows.Media.Color.FromArgb(a, r, g, b));

        Resources["Settings.Window.Background"] = B(0x24, 0x28, 0x2E);
        Resources["Settings.Sidebar.Background"] = B(0x27, 0x2C, 0x33);
        Resources["Settings.Sidebar.SelectedBackground"] = B(0x31, 0x45, 0x61);
        Resources["Settings.Sidebar.HoverBackground"] = B(0x31, 0x37, 0x40);
        Resources["Settings.Card.Background"] = B(0x2B, 0x30, 0x37);
        Resources["Settings.Card.Border"] = B(0x4A, 0x51, 0x5B);
        Resources["Settings.Card.SubtleBackground"] = B(0x25, 0x2A, 0x31);
        Resources["Settings.Foreground"] = B(0xF2, 0xF4, 0xF7);
        Resources["Settings.SubtleForeground"] = B(0xA8, 0xB0, 0xBA);
        Resources["Settings.Muted"] = B(0x8A, 0x95, 0xA3);
        Resources["Settings.Stroke"] = B(0x43, 0x4A, 0x54);
        Resources["Settings.Accent"] = B(0x4D, 0x90, 0xFE);
        Resources["Settings.AccentHover"] = B(0x6C, 0xA4, 0xFF);
        Resources["Settings.AccentSoft"] = B(0x2C, 0x42, 0x5E);
        Resources["Settings.Input.Background"] = B(0x25, 0x2A, 0x31);
        Resources["Settings.Input.Border"] = B(0x4A, 0x51, 0x5B);
        Resources["Settings.Input.BorderFocused"] = B(0x4D, 0x90, 0xFE);
        Resources["Settings.Hover"] = B(0x36, 0x3D, 0x46);
        Resources["Settings.SuccessSoft"] = B(0x16, 0x33, 0x25);
        Resources["Settings.Success"] = B(0x5E, 0xD4, 0x8E);
        Resources["Settings.DangerSoft"] = B(0x40, 0x1B, 0x1F);
        Resources["Settings.Danger"] = B(0xFF, 0x8A, 0x8A);
        Resources["Settings.WarningSoft"] = B(0x3D, 0x2C, 0x14);
        Resources["Settings.Warning"] = B(0xF3, 0xC2, 0x68);
        Foreground = (SolidColorBrush)Resources["Settings.Foreground"];
    }

    private void Bind()
    {
        BlacklistRadio.IsChecked = _settings.BlacklistMode;
        WhitelistRadio.IsChecked = !_settings.BlacklistMode;
        FullScreenSuppress.IsChecked = _settings.SuppressOnFullScreen;
        LaunchAtStartup.IsChecked = _settings.LaunchAtStartup;
        MinTextLengthBox.Value = _settings.MinTextLength;
        MaxTextLengthBox.Value = _settings.MaxTextLength;

        foreach (var p in _settings.ProcessFilter.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ProcessFilters.Add(p);
        }
        RefreshRecentProcesses();

        DisplayIconAndText.IsChecked = _settings.ToolbarDisplay == ToolbarDisplayMode.IconAndText;
        DisplayIconOnly.IsChecked = _settings.ToolbarDisplay == ToolbarDisplayMode.IconOnly;
        DisplayTextOnly.IsChecked = _settings.ToolbarDisplay == ToolbarDisplayMode.TextOnly;
        ThemeAuto.IsChecked = _settings.ToolbarTheme == ToolbarThemeMode.Auto;
        ThemeLight.IsChecked = _settings.ToolbarTheme == ToolbarThemeMode.Light;
        ThemeDark.IsChecked = _settings.ToolbarTheme == ToolbarThemeMode.Dark;
        SurfaceShadow.IsChecked = _settings.ToolbarSurface == ToolbarSurfaceStyle.Shadow;
        SurfaceBorder.IsChecked = _settings.ToolbarSurface == ToolbarSurfaceStyle.Border;
        SurfaceShadowAndBorder.IsChecked = _settings.ToolbarSurface == ToolbarSurfaceStyle.ShadowAndBorder;
        FollowAccentColor.IsChecked = _settings.FollowAccentColor;
        CornerRadiusBox.Value = _settings.ToolbarCornerRadius;
        ButtonSpacingBox.Value = _settings.ToolbarButtonSpacing;
        ToolbarFontSizeBox.Value = _settings.ToolbarFontSize;
        MaxActionsPerRowBox.Value = _settings.ToolbarMaxActionsPerRow;
        EnableToolbarKeyboardShortcutsBox.IsChecked = _settings.EnableToolbarKeyboardShortcuts;
        EnableToolbarTabNavigationBox.IsChecked = _settings.EnableToolbarTabNavigation;
        EnableToolbarNumberShortcutsBox.IsChecked = _settings.EnableToolbarNumberShortcuts;

        SelectComboByTag(PopupModeBox, _settings.PopupMode.ToString());
        SelectComboByTag(RequiredModifierBox, _settings.RequiredModifier.ToString());
        PopupDelayBox.Value = _settings.PopupDelayMs;
        HoverDelayBox.Value = _settings.HoverDelayMs;
        EnableSelectAllPopupBox.IsChecked = _settings.EnableSelectAllPopup;

        DismissOnMouseLeaveBox.IsChecked = _settings.DismissOnMouseLeave;
        DismissOnForegroundChangedBox.IsChecked = _settings.DismissOnForegroundChanged;
        DismissOnClickOutsideBox.IsChecked = _settings.DismissOnClickOutside;
        DismissOnEscapeKeyBox.IsChecked = _settings.DismissOnEscapeKey;
        DismissOnNewSelectionBox.IsChecked = _settings.DismissOnNewSelection;
        DismissOnActionInvokedBox.IsChecked = _settings.DismissOnActionInvoked;
        DismissMouseLeaveDelayBox.Value = _settings.DismissMouseLeaveDelayMs;

        SearchEngineName.Text = _settings.SearchEngineName;
        SearchUrlTemplate.Text = _settings.SearchUrlTemplate;
        SyncPresetSelection();

        PauseHotKeyBox.Text = _settings.PauseHotKey;
        ToolbarHotKeyBox.Text = _settings.ToolbarHotKey;

        BindAiSettings();

        var actions = _store.LoadActions() ?? CreateDefaultActions();
        foreach (var action in actions.Actions)
        {
            ActionItems.Add(ActionEditorItem.FromDescriptor(action));
        }
    }

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
        _currentAiKeyBucket = CurrentAiProvider().KeyBucket;
        AiApiKeyBox.Password = "";
        _syncingAiProvider = false;
        RefreshAiProviderFields(overwritePresetValues: false);
    }

    private static ActionsConfig CreateDefaultActions()
    {
        return new ActionsConfig
        {
            Actions =
            {
                new() { Id = "copy", Type = "builtin", BuiltIn = BuiltInActionIds.Copy, Title = "复制", Icon = "Copy", Enabled = true },
                new() { Id = "open-url", Type = "builtin", BuiltIn = BuiltInActionIds.OpenUrl, Title = "打开链接", Icon = "Url", Enabled = true },
                new() { Id = "mailto", Type = "builtin", BuiltIn = BuiltInActionIds.Mailto, Title = "邮件", Icon = "Mail", Enabled = true },
                new() { Id = "search", Type = "builtin", BuiltIn = BuiltInActionIds.Search, Title = "搜索", Icon = "Search", Enabled = true },
                new() { Id = "translate", Type = "builtin", BuiltIn = BuiltInActionIds.Translate, Title = "翻译", Icon = "Translate", Enabled = true },
                new() { Id = "upper", Type = "builtin", BuiltIn = BuiltInActionIds.ToUpper, Title = "大写", Icon = "Upper", Enabled = true },
                new() { Id = "lower", Type = "builtin", BuiltIn = BuiltInActionIds.ToLower, Title = "小写", Icon = "Lower", Enabled = true },
                new() { Id = "title", Type = "builtin", BuiltIn = BuiltInActionIds.ToTitle, Title = "Title", Icon = "Title", Enabled = true },
                new() { Id = "calc", Type = "builtin", BuiltIn = BuiltInActionIds.Calculate, Title = "计算", Icon = "Calc", Enabled = true },
                new() { Id = "wc", Type = "builtin", BuiltIn = BuiltInActionIds.WordCount, Title = "字数", Icon = "Count", Enabled = true },
            },
        };
    }

    private void OnNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavigationList.SelectedItem is not ListBoxItem item || item.Tag is not string tag) return;
        NavigateTo(tag);
    }

    public void NavigateTo(string tag)
    {
        foreach (var (key, page) in _pages)
        {
            page.Visibility = key.Equals(tag, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        foreach (var raw in NavigationList.Items)
        {
            if (raw is ListBoxItem item && item.Tag is string itemTag
                && itemTag.Equals(tag, StringComparison.OrdinalIgnoreCase))
            {
                NavigationList.SelectedItem = item;
                break;
            }
        }
    }

    private void OnSettingsSearchChanged(object sender, TextChangedEventArgs e)
    {
        var query = SettingsSearchBox.Text.Trim();
        if (query.Length == 0)
        {
            SearchHint.Text = "";
            return;
        }

        var hit = FindSettingsHit(query);
        if (hit is null)
        {
            SearchHint.Text = "未找到匹配设置";
            return;
        }

        NavigateTo(hit.Value.Page);
        SearchHint.Text = hit.Value.Label;
    }

    private void OnSettingsSearchKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var hit = FindSettingsHit(SettingsSearchBox.Text.Trim());
        if (hit is not null) NavigateTo(hit.Value.Page);
    }

    private static (string Page, string Label)? FindSettingsHit(string query)
    {
        var entries = new[]
        {
            ("General", "通用 全屏 开机 自启 触发 延迟 悬停 修饰键 快捷键"),
            ("Appearance", "外观 主题 深色 浅色 自动 强调色 阴影 边框 圆角 间距 字号"),
            ("Actions", "动作 添加 内置 URL 脚本 正则 matchRegex 排序"),
            ("Processes", "进程 过滤 黑名单 白名单 最近活动窗口"),
            ("Search", "搜索 引擎 Google Bing 百度 URL 模板"),
            ("Hotkeys", "快捷键 热键 暂停 恢复 唤起 工具栏"),
            ("AI", "AI 模型 Provider DeepSeek OpenAI API Key 自定义 测试连接"),
            ("About", "关于 版本 配置目录"),
        };
        return entries.FirstOrDefault(x => x.Item2.Contains(query, StringComparison.OrdinalIgnoreCase)) is var hit
               && hit.Item1 is not null
            ? (hit.Item1, hit.Item2.Split(' ')[0])
            : null;
    }

    private void SyncPresetSelection()
    {
        var url = _settings.SearchUrlTemplate?.Trim() ?? "";
        var preset = Array.Find(Presets, p => string.Equals(p.Url, url, StringComparison.OrdinalIgnoreCase));
        if (preset.Url is null)
        {
            SearchCustom.IsChecked = true;
            return;
        }
        SearchGoogle.IsChecked = preset.Name == "Google";
        SearchBing.IsChecked = preset.Name == "Bing";
        SearchBaidu.IsChecked = preset.Name == "百度";
    }

    private void OnSearchPresetChanged(object sender, RoutedEventArgs e)
    {
        (string Name, string Url)? hit = null;
        if (SearchGoogle.IsChecked == true) hit = Presets[0];
        else if (SearchBing.IsChecked == true) hit = Presets[1];
        else if (SearchBaidu.IsChecked == true) hit = Presets[2];
        if (hit is null) return;

        SearchEngineName.Text = hit.Value.Name;
        SearchUrlTemplate.Text = hit.Value.Url;
    }

    private void OnAiProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingAiProvider) return;
        RememberPendingAiKey();
        RefreshAiProviderFields(overwritePresetValues: true);
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
            SelectedAiThinkingMode().ToString());
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

    private void OnProcessSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ProcessList.SelectedItem is string process)
        {
            ProcessNameBox.Text = process;
        }
    }

    private void OnAddOrUpdateProcess(object sender, RoutedEventArgs e)
    {
        var process = NormalizeProcessName(ProcessNameBox.Text);
        if (string.IsNullOrWhiteSpace(process)) return;

        var existing = ProcessFilters
            .Select((Value, Index) => (Value, Index))
            .FirstOrDefault(x => string.Equals(x.Value, process, StringComparison.OrdinalIgnoreCase));
        if (existing.Value is not null)
        {
            ProcessFilters[existing.Index] = process;
        }
        else if (ProcessList.SelectedIndex >= 0)
        {
            ProcessFilters[ProcessList.SelectedIndex] = process;
        }
        else
        {
            ProcessFilters.Add(process);
        }
    }

    private void OnRemoveProcess(object sender, RoutedEventArgs e)
    {
        if (ProcessList.SelectedItem is string selected)
        {
            ProcessFilters.Remove(selected);
        }
    }

    private void OnRefreshRecentProcesses(object sender, RoutedEventArgs e) => RefreshRecentProcesses();

    private void RefreshRecentProcesses()
    {
        RecentProcessList.ItemsSource = ForegroundWatcher.RecentProcesses()
            .Where(x => !string.IsNullOrWhiteSpace(x.ProcessName))
            .Select(x => new RecentProcessItem(x.ProcessName, x.WindowTitle))
            .ToList();
    }

    private void OnRecentProcessPicked(object sender, SelectionChangedEventArgs e)
    {
        if (RecentProcessList.SelectedItem is not RecentProcessItem item) return;
        ProcessNameBox.Text = item.ProcessName;
        if (!ProcessFilters.Any(x => string.Equals(x, item.ProcessName, StringComparison.OrdinalIgnoreCase)))
        {
            ProcessFilters.Add(item.ProcessName);
        }
    }

    private static string NormalizeProcessName(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return "";
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + ".exe";
    }

    private void OnActionGlobalTestChanged(object sender, TextChangedEventArgs e)
    {
        foreach (var item in ActionItems)
        {
            item.RegexTestText = ActionGlobalTestText.Text;
        }
    }

    private void OnAddBuiltInAction(object sender, RoutedEventArgs e)
    {
        var choice = BuiltInChoices.FirstOrDefault(x => ActionItems.All(a => !string.Equals(a.BuiltIn, x.Id, StringComparison.OrdinalIgnoreCase)))
                     ?? BuiltInChoices[0];
        AddAction(new ActionEditorItem
        {
            Id = UniqueActionId(choice.Id.Split('.').Last()),
            Type = "builtin",
            BuiltIn = choice.Id,
            Title = choice.Title,
            Icon = SuggestIcon(choice.Id),
            Enabled = true,
            RegexTestText = ActionGlobalTestText.Text,
        });
    }

    private void OnAddUrlAction(object sender, RoutedEventArgs e)
    {
        AddAction(new ActionEditorItem
        {
            Id = UniqueActionId("url"),
            Type = "url-template",
            Title = "打开 URL",
            Icon = "Url",
            UrlTemplate = "https://www.google.com/search?q={urlencoded}",
            Enabled = true,
            RegexTestText = ActionGlobalTestText.Text,
        });
    }

    private void OnAddScriptAction(object sender, RoutedEventArgs e)
    {
        AddAction(new ActionEditorItem
        {
            Id = UniqueActionId("script"),
            Type = "script",
            Title = "脚本",
            Icon = "Script",
            ScriptPath = "",
            Arguments = "{text}",
            Enabled = true,
            RegexTestText = ActionGlobalTestText.Text,
        });
    }

    private void AddAction(ActionEditorItem item)
    {
        // 新建动作默认展开，方便用户立刻填字段
        item.IsExpanded = true;
        ActionItems.Add(item);
    }

    private string UniqueActionId(string seed)
    {
        var id = seed.Replace("builtin-", "", StringComparison.OrdinalIgnoreCase)
            .Replace("builtin.", "", StringComparison.OrdinalIgnoreCase)
            .Replace(".", "-", StringComparison.Ordinal);
        if (ActionItems.All(x => !string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))) return id;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{id}-{i}";
            if (ActionItems.All(x => !string.Equals(x.Id, candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
        return $"{id}-{Guid.NewGuid():N}";
    }

    private static string SuggestIcon(string builtIn) => builtIn switch
    {
        BuiltInActionIds.Copy => "Copy",
        BuiltInActionIds.OpenUrl => "Url",
        BuiltInActionIds.Mailto => "Mail",
        BuiltInActionIds.Search => "Search",
        BuiltInActionIds.Translate => "Translate",
        BuiltInActionIds.ToUpper => "Upper",
        BuiltInActionIds.ToLower => "Lower",
        BuiltInActionIds.ToTitle => "Title",
        BuiltInActionIds.Calculate => "Calc",
        BuiltInActionIds.WordCount => "Count",
        BuiltInActionIds.AiSummarize => "AiSummary",
        BuiltInActionIds.AiRewrite => "AiRewrite",
        BuiltInActionIds.AiTranslate => "AiTranslate",
        BuiltInActionIds.AiExplain => "AiExplain",
        BuiltInActionIds.AiReply => "AiReply",
        _ => "Script",
    };

    /// <summary>卡片内"上移"按钮：通过 sender.Tag 拿到目标 item，避免依赖 ListBox 选中状态</summary>
    private void OnActionMoveUp(object sender, RoutedEventArgs e) => MoveAction(SenderItem(sender), -1);
    private void OnActionMoveDown(object sender, RoutedEventArgs e) => MoveAction(SenderItem(sender), 1);

    private void OnActionDelete(object sender, RoutedEventArgs e)
    {
        var item = SenderItem(sender);
        if (item is not null) ActionItems.Remove(item);
    }

    private void MoveAction(ActionEditorItem? item, int delta)
    {
        if (item is null) return;
        var index = ActionItems.IndexOf(item);
        var next = index + delta;
        if (index < 0 || next < 0 || next >= ActionItems.Count) return;
        ActionItems.Move(index, next);
    }

    private static ActionEditorItem? SenderItem(object sender)
        => (sender as FrameworkElement)?.Tag as ActionEditorItem
           ?? (sender as FrameworkElement)?.DataContext as ActionEditorItem;

    /// <summary>拖拽起始：记录起点；命中具体卡片才记录 _dragItem，避免在卡片间空白区起拖</summary>
    private void OnActionDragStart(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _dragItem = FindAncestor<ContentPresenter>((DependencyObject)e.OriginalSource)?.Content as ActionEditorItem;
    }

    private void OnActionMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null) return;
        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }
        // 在输入框内不触发拖拽，避免与文本选择冲突
        if (e.OriginalSource is DependencyObject src && FindAncestor<TextBox>(src) is not null) return;
        DragDrop.DoDragDrop(ActionsList, _dragItem, System.Windows.DragDropEffects.Move);
        _dragItem = null;
    }

    private void OnActionDrop(object sender, WpfDragEventArgs e)
    {
        if (e.Data.GetData(typeof(ActionEditorItem)) is not ActionEditorItem dropped) return;
        var targetPresenter = FindAncestor<ContentPresenter>((DependencyObject)e.OriginalSource);
        var targetItem = targetPresenter?.Content as ActionEditorItem;
        if (targetItem is null || ReferenceEquals(dropped, targetItem)) return;

        var oldIndex = ActionItems.IndexOf(dropped);
        var newIndex = ActionItems.IndexOf(targetItem);
        if (oldIndex < 0 || newIndex < 0) return;
        ActionItems.Move(oldIndex, newIndex);
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T found) return found;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void OnPopupModeChanged(object sender, SelectionChangedEventArgs e)
    {
        var mode = SelectedTag(PopupModeBox);
        PopupDelayBox.IsEnabled = mode == nameof(SelectionPopupMode.Delayed);
        HoverDelayBox.IsEnabled = mode == nameof(SelectionPopupMode.HoverStill);
        RequiredModifierBox.IsEnabled = mode == nameof(SelectionPopupMode.ModifierRequired);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.BlacklistMode = BlacklistRadio.IsChecked == true;
        _settings.SuppressOnFullScreen = FullScreenSuppress.IsChecked == true;
        _settings.LaunchAtStartup = LaunchAtStartup.IsChecked == true;
        _settings.MinTextLength = NumberBoxInt(MinTextLengthBox, _settings.MinTextLength, 1, 100_000);
        _settings.MaxTextLength = NumberBoxInt(MaxTextLengthBox, _settings.MaxTextLength, _settings.MinTextLength, 300_000);
        _settings.ProcessFilter = ProcessFilters
            .Select(NormalizeProcessName)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _settings.ToolbarDisplay = DisplayIconOnly.IsChecked == true
            ? ToolbarDisplayMode.IconOnly
            : DisplayTextOnly.IsChecked == true
                ? ToolbarDisplayMode.TextOnly
                : ToolbarDisplayMode.IconAndText;
        _settings.ToolbarTheme = ThemeDark.IsChecked == true
            ? ToolbarThemeMode.Dark
            : ThemeLight.IsChecked == true
                ? ToolbarThemeMode.Light
                : ToolbarThemeMode.Auto;
        _settings.ToolbarSurface = SurfaceShadow.IsChecked == true
            ? ToolbarSurfaceStyle.Shadow
            : SurfaceBorder.IsChecked == true
                ? ToolbarSurfaceStyle.Border
                : ToolbarSurfaceStyle.ShadowAndBorder;
        _settings.FollowAccentColor = FollowAccentColor.IsChecked == true;
        _settings.ToolbarCornerRadius = NumberBoxDouble(CornerRadiusBox, _settings.ToolbarCornerRadius, 0, 18);
        _settings.ToolbarButtonSpacing = NumberBoxDouble(ButtonSpacingBox, _settings.ToolbarButtonSpacing, 0, 10);
        _settings.ToolbarFontSize = NumberBoxDouble(ToolbarFontSizeBox, _settings.ToolbarFontSize, 10, 18);
        _settings.ToolbarMaxActionsPerRow = NumberBoxInt(MaxActionsPerRowBox, _settings.ToolbarMaxActionsPerRow, 3, 12);
        _settings.EnableToolbarKeyboardShortcuts = EnableToolbarKeyboardShortcutsBox.IsChecked == true;
        _settings.EnableToolbarTabNavigation = EnableToolbarTabNavigationBox.IsChecked == true;
        _settings.EnableToolbarNumberShortcuts = EnableToolbarNumberShortcutsBox.IsChecked == true;

        _settings.PopupMode = Enum.TryParse<SelectionPopupMode>(SelectedTag(PopupModeBox), out var popupMode)
            ? popupMode
            : SelectionPopupMode.Immediate;
        _settings.RequiredModifier = Enum.TryParse<SelectionModifierKey>(SelectedTag(RequiredModifierBox), out var modifier)
            ? modifier
            : SelectionModifierKey.Alt;
        _settings.PopupDelayMs = NumberBoxInt(PopupDelayBox, _settings.PopupDelayMs, 0, 1500);
        _settings.HoverDelayMs = NumberBoxInt(HoverDelayBox, _settings.HoverDelayMs, 0, 1500);
        _settings.EnableSelectAllPopup = EnableSelectAllPopupBox.IsChecked == true;

        _settings.DismissOnMouseLeave = DismissOnMouseLeaveBox.IsChecked == true;
        _settings.DismissOnForegroundChanged = DismissOnForegroundChangedBox.IsChecked == true;
        _settings.DismissOnClickOutside = DismissOnClickOutsideBox.IsChecked == true;
        _settings.DismissOnEscapeKey = DismissOnEscapeKeyBox.IsChecked == true;
        _settings.DismissOnNewSelection = DismissOnNewSelectionBox.IsChecked == true;
        _settings.DismissOnActionInvoked = DismissOnActionInvokedBox.IsChecked == true;
        _settings.DismissMouseLeaveDelayMs = NumberBoxInt(DismissMouseLeaveDelayBox, _settings.DismissMouseLeaveDelayMs, 0, 5000);

        var name = SearchEngineName.Text?.Trim() ?? "";
        var url = SearchUrlTemplate.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(name)) _settings.SearchEngineName = name;
        if (!string.IsNullOrEmpty(url) && url.Contains("{q}", StringComparison.Ordinal))
        {
            _settings.SearchUrlTemplate = url;
        }

        _settings.PauseHotKey = PauseHotKeyBox.Text.Trim();
        _settings.ToolbarHotKey = ToolbarHotKeyBox.Text.Trim();
        SaveAiSettings();
        _settings.FirstRunCompleted = true;

        if (_settings.AiEnabled)
        {
            EnsureDefaultAiActions();
        }

        _store.SaveSettings(_settings);
        _store.SaveActions(new ActionsConfig
        {
            SchemaVersion = 1,
            Actions = ActionItems.Select(x => x.ToDescriptor()).ToList(),
        });
        Saved?.Invoke();
        Close();
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

        foreach (var (bucket, plain) in _pendingAiKeys)
        {
            if (string.IsNullOrWhiteSpace(plain)) continue;
            AiProviderCatalog.SetProtectedKey(_settings, bucket, _secretStore.Protect(plain));
        }
    }

    private void EnsureDefaultAiActions()
    {
        AddDefaultAiAction("ai-summary", BuiltInActionIds.AiSummarize, "AI 总结");
        AddDefaultAiAction("ai-rewrite", BuiltInActionIds.AiRewrite, "AI 改写");
        AddDefaultAiAction("ai-translate", BuiltInActionIds.AiTranslate, "AI 翻译");
        AddDefaultAiAction("ai-explain", BuiltInActionIds.AiExplain, "AI 解释");
        AddDefaultAiAction("ai-reply", BuiltInActionIds.AiReply, "AI 回复");
    }

    private void AddDefaultAiAction(string id, string builtIn, string title)
    {
        if (ActionItems.Any(x => string.Equals(x.BuiltIn, builtIn, StringComparison.OrdinalIgnoreCase))) return;
        ActionItems.Add(new ActionEditorItem
        {
            Id = UniqueActionId(id),
            Type = "builtin",
            BuiltIn = builtIn,
            Title = title,
            Icon = SuggestIcon(builtIn),
            Enabled = true,
            RegexTestText = ActionGlobalTestText.Text,
        });
    }

    private static int ParseInt(string text, int fallback, int min, int max)
        => int.TryParse(text, out var value) ? Math.Clamp(value, min, max) : fallback;

    private static double ParseDouble(string text, double fallback, double min, double max)
        => double.TryParse(text, out var value) ? Math.Clamp(value, min, max) : fallback;

    /// <summary>从 WPF-UI NumberBox 读取数值并做 clamp，Value 为 null 时回落到 fallback</summary>
    private static int NumberBoxInt(Wpf.Ui.Controls.NumberBox box, int fallback, int min, int max)
        => box.Value is double v ? Math.Clamp((int)Math.Round(v), min, max) : fallback;

    private static double NumberBoxDouble(Wpf.Ui.Controls.NumberBox box, double fallback, double min, double max)
        => box.Value is double v ? Math.Clamp(v, min, max) : fallback;

    private static void SelectComboByTag(WpfComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private static string SelectedTag(WpfComboBox combo)
        => combo.SelectedItem is ComboBoxItem item && item.Tag is string tag ? tag : "";

    private AiThinkingMode SelectedAiThinkingMode()
        => AiThinkingModeBox.SelectedValue is AiThinkingMode mode
            ? mode
            : AiThinkingMode.Auto;

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}

public sealed record BuiltInChoice(string Id, string Title);

public sealed record ActionTypeChoice(string Value, string Label);

public sealed record AiThinkingModeChoice(AiThinkingMode Value, string Label, string Description);

public sealed record RecentProcessItem(string ProcessName, string WindowTitle)
{
    public string Display => string.IsNullOrWhiteSpace(WindowTitle)
        ? ProcessName
        : $"{ProcessName} - {WindowTitle}";
}

public sealed class ActionEditorItem : INotifyPropertyChanged
{
    private string _id = "";
    private string _title = "";
    private string _icon = "";
    private string _type = "builtin";
    private string? _builtIn;
    private string? _matchRegex;
    private string? _urlTemplate;
    private string? _scriptPath;
    private string? _arguments;
    private string _regexTestText = "";
    private string _regexResult = "始终显示";
    private bool _enabled = true;
    private bool _isExpanded;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get => _id; set => Set(ref _id, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Icon { get => _icon; set => Set(ref _icon, value); }
    public string Type { get => _type; set => Set(ref _type, value); }
    public string? BuiltIn { get => _builtIn; set => Set(ref _builtIn, value); }

    public string? MatchRegex
    {
        get => _matchRegex;
        set
        {
            if (Set(ref _matchRegex, value)) RefreshRegexResult();
        }
    }

    public string? UrlTemplate { get => _urlTemplate; set => Set(ref _urlTemplate, value); }
    public string? ScriptPath { get => _scriptPath; set => Set(ref _scriptPath, value); }
    public string? Arguments { get => _arguments; set => Set(ref _arguments, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    public string RegexTestText
    {
        get => _regexTestText;
        set
        {
            if (Set(ref _regexTestText, value)) RefreshRegexResult();
        }
    }

    public string RegexResult
    {
        get => _regexResult;
        private set => Set(ref _regexResult, value);
    }

    public static ActionEditorItem FromDescriptor(ActionDescriptor descriptor)
    {
        var item = new ActionEditorItem
        {
            Id = descriptor.Id,
            Title = descriptor.Title,
            Icon = descriptor.Icon,
            Type = descriptor.Type,
            BuiltIn = descriptor.BuiltIn,
            MatchRegex = descriptor.MatchRegex,
            UrlTemplate = descriptor.UrlTemplate,
            ScriptPath = descriptor.ScriptPath,
            Arguments = descriptor.Arguments,
            Enabled = descriptor.Enabled,
        };
        item.RefreshRegexResult();
        return item;
    }

    public ActionDescriptor ToDescriptor()
    {
        return new ActionDescriptor
        {
            Id = Id.Trim(),
            Title = Title.Trim(),
            Icon = Icon.Trim(),
            Type = Type.Trim(),
            BuiltIn = string.IsNullOrWhiteSpace(BuiltIn) ? null : BuiltIn,
            MatchRegex = string.IsNullOrWhiteSpace(MatchRegex) ? null : MatchRegex,
            UrlTemplate = string.IsNullOrWhiteSpace(UrlTemplate) ? null : UrlTemplate,
            ScriptPath = string.IsNullOrWhiteSpace(ScriptPath) ? null : ScriptPath,
            Arguments = string.IsNullOrWhiteSpace(Arguments) ? null : Arguments,
            Enabled = Enabled,
        };
    }

    private void RefreshRegexResult()
    {
        if (string.IsNullOrWhiteSpace(MatchRegex))
        {
            RegexResult = "始终显示";
            return;
        }

        try
        {
            var matched = Regex.IsMatch(RegexTestText ?? "", MatchRegex, RegexOptions.CultureInvariant);
            RegexResult = matched ? "匹配" : "不匹配";
        }
        catch (Exception ex)
        {
            RegexResult = "正则错误：" + ex.Message;
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
