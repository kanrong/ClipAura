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
    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaisePropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        RaisePropertyChanged(name);
        return true;
    }

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
    private readonly IConversationStore? _historyStore;
    private readonly IUsageRecorder? _usage;
    private readonly Action<string>? _onOpenConversation;
    private readonly IReadOnlyList<IOcrProvider> _ocrProviders;
    private WpfPoint _dragStartPoint;
    private ActionEditorItem? _dragItem;
    private string _currentAiKeyBucket = AiProviderCatalog.DeepSeekKeyBucket;
    private bool _syncingAiProvider;
    private bool _suspendCommit;

    public ObservableCollection<string> ProcessFilters { get; } = new();
    public ObservableCollection<ActionEditorItem> ActionItems { get; } = new();
    public IReadOnlyList<AiProviderPresetInfo> AiProviderChoices => AiProviderCatalog.All;
    public IReadOnlyList<AiThinkingModeChoice> AiThinkingModeChoices { get; } = new[]
    {
        new AiThinkingModeChoice(AiThinkingMode.Auto, "自动", "使用当前服务商默认策略"),
        new AiThinkingModeChoice(AiThinkingMode.Fast, "快速", "DeepSeek 关闭 thinking；OpenAI 使用 low reasoning"),
        new AiThinkingModeChoice(AiThinkingMode.Deep, "深度", "DeepSeek 启用 thinking + max；OpenAI 使用 high reasoning"),
    };
    public IReadOnlyList<LogLevelChoice> LogLevelChoices { get; } = new[]
    {
        new LogLevelChoice(LogLevel.Debug, "调试"),
        new LogLevelChoice(LogLevel.Info, "信息"),
        new LogLevelChoice(LogLevel.Warn, "警告"),
        new LogLevelChoice(LogLevel.Error, "错误"),
    };

    /// <summary>外观页"颜色主题"卡片列表数据源。
    /// BackgroundBrush / ForegroundBrush 是渲染预览圆点用，对应浮窗 Theme.xaml 中的主色对；
    /// 基础三档（Auto / Light / Dark）用中性灰色，让用户能在视觉上区分"系统色"和"彩色预设"</summary>
    public IReadOnlyList<ToolbarThemeChoice> ToolbarThemeChoices { get; } = new[]
    {
        new ToolbarThemeChoice(ToolbarThemeMode.Auto, "自动", "#F4F6F9", "#2B3037"),
        new ToolbarThemeChoice(ToolbarThemeMode.Light, "浅色", "#FFFFFF", "#1E2329"),
        new ToolbarThemeChoice(ToolbarThemeMode.Dark, "深色", "#2B3037", "#F2F4F7"),
        new ToolbarThemeChoice(ToolbarThemeMode.QingciBlue, "青瓷蓝", "#113974", "#99D3D4"),
        new ToolbarThemeChoice(ToolbarThemeMode.DeepInkGreen, "深墨绿", "#2B564A", "#C4D373"),
        new ToolbarThemeChoice(ToolbarThemeMode.MistyGreen, "浅雾绿", "#688E73", "#F6E9CE"),
        new ToolbarThemeChoice(ToolbarThemeMode.SunsetRose, "暮霞粉", "#8B3A4D", "#FFE2D5"),
        new ToolbarThemeChoice(ToolbarThemeMode.DistantMountain, "远山黛", "#2B3D4F", "#D5DBE5"),
        new ToolbarThemeChoice(ToolbarThemeMode.Sandalwood, "檀木香", "#5C4033", "#F2E1C2"),
    };
    public string LogDirectoryPath => ConsoleLog.Instance.DirectoryPath;
    public string ConfigDirectoryPath => ConfigPaths.ConfigDir;
    /// <summary>"添加用户自定义动作"图标下拉的可选项。
    /// 严格剔除被内置功能/预定义模板占用的图标，避免不同语义复用同一图形</summary>
    public IReadOnlyList<IconChoice> IconChoices { get; } = IconChoiceCatalog.UserSelectable;

    public IReadOnlyList<AiOutputModeChoice> AiOutputModeChoices { get; } = new[]
    {
        new AiOutputModeChoice("chat", "进入对话窗口"),
        new AiOutputModeChoice("replace", "原地替换选区"),
        new AiOutputModeChoice("clipboard", "写入剪贴板"),
        new AiOutputModeChoice("inlineToast", "浮窗显示结果"),
    };

    /// <summary>智能动作的输出模式选项。用 AiOutputModeChoice 类型复用同款 (Value, Label) 元组，
    /// 避免再造一个等价类型；XAML 端通过 IsBuiltInOutputConfigurable 决定是否显示此下拉</summary>
    public IReadOnlyList<AiOutputModeChoice> BuiltInOutputModeChoices { get; } = new[]
    {
        new AiOutputModeChoice(BuiltInOutputModes.Copy, "仅复制"),
        new AiOutputModeChoice(BuiltInOutputModes.Bubble, "气泡窗口"),
        new AiOutputModeChoice(BuiltInOutputModes.CopyAndBubble, "复制 + 气泡窗口"),
        new AiOutputModeChoice(BuiltInOutputModes.Dialog, "对话框（独立结果窗口）"),
    };

    /// <summary>外观页的浮窗预览示例按钮，模拟用户日常浮窗里可能出现的"复制/粘贴/搜索/翻译/AI"</summary>
    public IReadOnlyList<ToolbarPreviewItem> ToolbarPreviewItems { get; } = new[]
    {
        new ToolbarPreviewItem("复制", Wpf.Ui.Controls.SymbolRegular.Copy24),
        new ToolbarPreviewItem("粘贴", Wpf.Ui.Controls.SymbolRegular.ClipboardPaste24),
        new ToolbarPreviewItem("搜索", Wpf.Ui.Controls.SymbolRegular.Search24),
        new ToolbarPreviewItem("翻译", Wpf.Ui.Controls.SymbolRegular.Translate24),
        new ToolbarPreviewItem("AI", Wpf.Ui.Controls.SymbolRegular.Sparkle24),
    };

    // ===== 浮窗预览：从外观页控件值派生，PropertyChanged 推送给 XAML 数据绑定 =====
    private CornerRadius _previewCornerRadius = new(9);
    private CornerRadius _previewButtonCornerRadius = new(0);
    private Thickness _previewButtonMargin = new(2, 0, 2, 0);
    private Thickness _previewButtonPadding = new(12, 9, 12, 9);
    private Thickness _previewBorderThickness = new(1);
    private double _previewFontSize = 13;
    private double _previewIconFontSize = 15;
    private double _previewShadowDepth = 2;
    private double _previewShadowBlurRadius = 6;
    private double _previewShadowOpacity = 0.32;
    private double _previewOpacity = 1.0;
    private Visibility _previewIconVisibility = Visibility.Visible;
    private Visibility _previewTextVisibility = Visibility.Visible;

    public CornerRadius PreviewCornerRadius { get => _previewCornerRadius; private set => SetField(ref _previewCornerRadius, value); }
    public CornerRadius PreviewButtonCornerRadius { get => _previewButtonCornerRadius; private set => SetField(ref _previewButtonCornerRadius, value); }
    public Thickness PreviewButtonMargin { get => _previewButtonMargin; private set => SetField(ref _previewButtonMargin, value); }
    public Thickness PreviewButtonPadding { get => _previewButtonPadding; private set => SetField(ref _previewButtonPadding, value); }
    public Thickness PreviewBorderThickness { get => _previewBorderThickness; private set => SetField(ref _previewBorderThickness, value); }
    public double PreviewFontSize { get => _previewFontSize; private set => SetField(ref _previewFontSize, value); }
    public double PreviewIconFontSize { get => _previewIconFontSize; private set => SetField(ref _previewIconFontSize, value); }
    public double PreviewShadowDepth { get => _previewShadowDepth; private set => SetField(ref _previewShadowDepth, value); }
    public double PreviewShadowBlurRadius { get => _previewShadowBlurRadius; private set => SetField(ref _previewShadowBlurRadius, value); }
    public double PreviewShadowOpacity { get => _previewShadowOpacity; private set => SetField(ref _previewShadowOpacity, value); }
    public double PreviewOpacity { get => _previewOpacity; private set => SetField(ref _previewOpacity, value); }
    public Visibility PreviewIconVisibility { get => _previewIconVisibility; private set => SetField(ref _previewIconVisibility, value); }
    public Visibility PreviewTextVisibility { get => _previewTextVisibility; private set => SetField(ref _previewTextVisibility, value); }

    public IReadOnlyList<PromptTemplateDefinition> BuiltinPromptTemplates => PromptTemplateLibrary.Builtin;

    /// <summary>"添加内置动作"对话框可选的全部内置动作。
    /// 单一真理源在 BuiltInActionSeeds.All；这里只是浅层映射并保留分组信息以便分组展示</summary>
    public IReadOnlyList<BuiltInChoice> BuiltInChoices { get; } =
        BuiltInActionSeeds.All
            .Select(s => new BuiltInChoice(s.BuiltIn, s.Title, s.IconKey, s.Group, s.Description))
            .ToList();

    public event Action? Saved;

    public SettingsWindow(
        ConfigStore store,
        AppSettings settings,
        string? initialPage = null,
        IConversationStore? historyStore = null,
        IUsageRecorder? usage = null,
        Action<string>? onOpenConversation = null,
        IReadOnlyList<IOcrProvider>? ocrProviders = null)
    {
        _store = store;
        _settings = settings;
        _historyStore = historyStore;
        _usage = usage;
        _onOpenConversation = onOpenConversation;
        _ocrProviders = ocrProviders ?? Array.Empty<IOcrProvider>();
        // 构造期间禁止 commit：Bind() 内大量控件初始化赋值会触发 Changed 事件，
        // 必须由 Loaded 阶段统一释放，避免在数据还没就位时就开始写盘
        _suspendCommit = true;
        // 主题/字体由 ThemeManager 写到 Application.Resources，所有窗口及 WPF-UI 控件共享
        ThemeManager.Apply(_settings);
        InitializeComponent();
        DataContext = this;
        // 仅 Auto 模式才挂 SystemThemeWatcher：用户选了固定主题/彩色预设时
        // 系统亮暗变化不应该反向覆盖用户选择
        if (_settings.ToolbarTheme == ToolbarThemeMode.Auto)
        {
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
        }
        _pages = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["General"] = GeneralPage,
            ["Appearance"] = AppearancePage,
            ["Actions"] = ActionsPage,
            ["Processes"] = ProcessesPage,
            ["Search"] = SearchPage,
            ["Hotkeys"] = HotkeysPage,
            ["AI"] = AIPage,
            ["Templates"] = TemplatesPage,
            ["History"] = HistoryPage,
            ["Usage"] = UsagePage,
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
        Loaded += OnSettingsLoaded;
    }

    private void OnSettingsLoaded(object sender, RoutedEventArgs e)
    {
        HookInstantCommit();
        _suspendCommit = false;

        // 进设置窗口本身视为完成首次配置，关掉自动弹出引导
        if (!_settings.FirstRunCompleted)
        {
            _settings.FirstRunCompleted = true;
            try { _store.SaveSettings(_settings); }
            catch { /* 首次写盘失败也不阻塞 UI */ }
        }
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
        ThemePresetList.SelectedValue = _settings.ToolbarTheme;
        if (ThemePresetList.SelectedValue is null) ThemePresetList.SelectedIndex = 0;
        SurfaceShadow.IsChecked = _settings.ToolbarSurface == ToolbarSurfaceStyle.Shadow;
        SurfaceBorder.IsChecked = _settings.ToolbarSurface == ToolbarSurfaceStyle.Border;
        SurfaceShadowAndBorder.IsChecked = _settings.ToolbarSurface == ToolbarSurfaceStyle.ShadowAndBorder;

        DensityCompact.IsChecked = _settings.ToolbarDensity == ToolbarDensity.Compact;
        DensityStandard.IsChecked = _settings.ToolbarDensity == ToolbarDensity.Standard;
        DensityComfortable.IsChecked = _settings.ToolbarDensity == ToolbarDensity.Comfortable;
        ToolbarLayoutSingle.IsChecked = _settings.ToolbarLayoutMode == ToolbarLayoutMode.Single;
        ToolbarLayoutSmartRow.IsChecked = _settings.ToolbarLayoutMode == ToolbarLayoutMode.SmartOnSeparateRow;
        ToolbarLayoutGroupRows.IsChecked = _settings.ToolbarLayoutMode == ToolbarLayoutMode.GroupRows;
        FollowAccentColor.IsChecked = _settings.FollowAccentColor;
        BindFontFamilyChoices();
        CornerRadiusBox.Value = _settings.ToolbarCornerRadius;
        ButtonSpacingBox.Value = _settings.ToolbarButtonSpacing;
        ToolbarFontSizeBox.Value = _settings.ToolbarFontSize;
        MaxActionsPerRowBox.Value = _settings.ToolbarMaxActionsPerRow;
        ToolbarOpacitySlider.Value = Math.Clamp(_settings.ToolbarIdleOpacity, 0.3, 1.0);
        UpdateToolbarOpacityLabel(ToolbarOpacitySlider.Value);
        EnableToolbarKeyboardShortcutsBox.IsChecked = _settings.EnableToolbarKeyboardShortcuts;
        EnableToolbarTabNavigationBox.IsChecked = _settings.EnableToolbarTabNavigation;
        EnableToolbarNumberShortcutsBox.IsChecked = _settings.EnableToolbarNumberShortcuts;

        SelectComboByTag(PopupModeBox, _settings.PopupMode.ToString());
        SelectComboByTag(RequiredModifierBox, _settings.RequiredModifier.ToString());
        SelectComboByTag(QuickClickModifierBox, _settings.QuickClickModifier.ToString());
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
        DismissOnTimeoutBox.IsChecked = _settings.DismissOnTimeout;
        DismissTimeoutMsBox.Value = _settings.DismissTimeoutMs;
        AboutUiaStatusText.Text = DetectUiaStatus();
        AboutPrivilegeStatusText.Text = DetectPrivilegeStatus();
        LogLevelBox.ItemsSource = LogLevelChoices;
        LogLevelBox.DisplayMemberPath = nameof(LogLevelChoice.Label);
        LogLevelBox.SelectedValuePath = nameof(LogLevelChoice.Value);
        LogLevelBox.SelectedValue = _settings.LogLevel;
        LogDirectoryPathText.Text = string.IsNullOrWhiteSpace(LogDirectoryPath) ? "不可用" : LogDirectoryPath;
        ConfigDirectoryPathText.Text = ConfigDirectoryPath;

        SearchEngineName.Text = _settings.SearchEngineName;
        SearchUrlTemplate.Text = _settings.SearchUrlTemplate;
        SyncPresetSelection();

        PauseHotKeyBox.Text = _settings.PauseHotKey;
        ToolbarHotKeyBox.Text = _settings.ToolbarHotKey;
        OcrHotKeyBox.Text = _settings.OcrHotKey;

        BindOcrProviders();
        BindOcrResultMode();

        BindAiSettings();

        var actions = _store.LoadActions() ?? CreateDefaultActions();
        foreach (var action in actions.Actions)
        {
            ActionItems.Add(ActionEditorItem.FromDescriptor(action));
        }
        RefreshToolbarPreview();
    }

    /// <summary>OCR provider 选择 + 状态展示。
    ///
    /// 数据流：
    /// (1) 把 Registry 已注册的所有 provider 翻成 ComboBox 选项；
    /// (2) 第一项硬编码"自动"，对应空 id（settings.OcrProviderId = ""）；
    /// (3) 后续每项前缀 ✓/✗，让用户在不展开下拉的情况下也能看出可用性；
    /// (4) 详情区列出所有 provider 的当前状态，缺文件 / 初始化失败的会直接给出修复指引。
    ///
    /// 注意：provider.IsAvailable / UnavailableReason 是动态求值的（轻量文件 IO），
    /// 每次打开设置页都会重新看一遍当前状态，不需要 push 通知。</summary>
    private void BindOcrProviders()
    {
        // 自动模式 + 各 provider；id 用空串表示"自动"（settings 也存空串）
        var choices = new List<OcrProviderChoice>
        {
            new("", "自动 (按优先级选可用项)"),
        };
        foreach (var p in _ocrProviders.OrderByDescending(p => p.Priority))
        {
            var mark = p.IsAvailable ? "✓" : "✗";
            choices.Add(new OcrProviderChoice(p.Id, $"{mark}  {p.DisplayName}"));
        }
        OcrProviderBox.ItemsSource = choices;
        OcrProviderBox.SelectedValue = _settings.OcrProviderId ?? "";
        if (OcrProviderBox.SelectedItem is null) OcrProviderBox.SelectedIndex = 0;

        RefreshOcrProviderHint();
    }

    /// <summary>更新右侧"活跃 provider 简介"与底部"详情/缺件指引"。
    /// 选择切换 / 文件复制后调用即可，纯展示无副作用</summary>
    private void RefreshOcrProviderHint()
    {
        var pickedId = (OcrProviderBox.SelectedValue as string) ?? "";
        IOcrProvider? active;
        if (string.IsNullOrWhiteSpace(pickedId))
        {
            active = _ocrProviders.Where(p => p.IsAvailable).OrderByDescending(p => p.Priority).FirstOrDefault();
            OcrActiveProviderHint.Text = active is not null
                ? $"当前活跃: {active.DisplayName}"
                : "当前没有可用 OCR 后端";
        }
        else
        {
            active = _ocrProviders.FirstOrDefault(p => p.Id == pickedId);
            if (active is null)
                OcrActiveProviderHint.Text = $"未注册的 provider id: {pickedId}";
            else if (active.IsAvailable)
                OcrActiveProviderHint.Text = $"当前活跃: {active.DisplayName}";
            else
                OcrActiveProviderHint.Text = $"该 provider 暂不可用，将自动回退到其它可用 provider";
        }

        // 详情区把每个 provider 的状态都列出来，便于用户对照排查
        var sb = new System.Text.StringBuilder();
        foreach (var p in _ocrProviders.OrderByDescending(p => p.Priority))
        {
            sb.Append('•').Append(' ').Append(p.DisplayName);
            if (p.IsAvailable)
            {
                sb.AppendLine("  (可用)");
            }
            else
            {
                sb.Append("  (不可用)");
                sb.AppendLine();
                sb.Append("    ").AppendLine(p.UnavailableReason ?? "未知原因");
            }
        }
        OcrProviderDetailText.Text = sb.ToString().TrimEnd();
    }

    private void OnOcrProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspendCommit) return;
        var picked = (OcrProviderBox.SelectedValue as string) ?? "";
        if (_settings.OcrProviderId == picked) return;
        _settings.OcrProviderId = picked;
        _store.SaveSettings(_settings);
        RefreshOcrProviderHint();
    }

    /// <summary>OCR provider ComboBox 的项目类型：Label 显示，Id 写入 settings。</summary>
    public sealed record OcrProviderChoice(string Id, string Label);

    /// <summary>把当前 settings.OcrResultMode / OcrResultWindowBordered 同步到两个 RadioButton + ToggleSwitch。
    /// 在 _suspendCommit=true 下勾选，避免 Checked 事件回写自己刚读到的值（无副作用，但减少日志噪音）</summary>
    private void BindOcrResultMode()
    {
        var prev = _suspendCommit;
        _suspendCommit = true;
        try
        {
            OcrModeInteractiveRadio.IsChecked = _settings.OcrResultMode == OcrResultMode.Interactive;
            OcrModeQuickRadio.IsChecked = _settings.OcrResultMode == OcrResultMode.Quick;
            OcrResultBorderedToggle.IsChecked = _settings.OcrResultWindowBordered;
        }
        finally { _suspendCommit = prev; }
    }

    private void OnOcrResultModeChanged(object sender, RoutedEventArgs e)
    {
        if (_suspendCommit) return;
        var mode = OcrModeQuickRadio.IsChecked == true ? OcrResultMode.Quick : OcrResultMode.Interactive;
        if (_settings.OcrResultMode == mode) return;
        _settings.OcrResultMode = mode;
        _store.SaveSettings(_settings);
    }

    private void OnOcrResultBorderedChanged(object sender, RoutedEventArgs e)
    {
        if (_suspendCommit) return;
        var bordered = OcrResultBorderedToggle.IsChecked == true;
        if (_settings.OcrResultWindowBordered == bordered) return;
        _settings.OcrResultWindowBordered = bordered;
        _store.SaveSettings(_settings);
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
        AiMaxOutputTokensBox.Value = _settings.AiMaxOutputTokens;
        TranslateInlineBox.IsChecked = _settings.TranslateInlineWhenAiEnabled;
        ExplainEnabledBox.IsChecked = _settings.ExplainActionEnabled;
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
                new() { Id = "paste", Type = "builtin", BuiltIn = BuiltInActionIds.Paste, Title = "粘贴", Icon = "Paste", Enabled = true },
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

        // 数据型页面在切到时再加载，避免每次打开设置都查 SQLite
        if (tag.Equals("History", StringComparison.OrdinalIgnoreCase)) RefreshHistoryList();
        else if (tag.Equals("Usage", StringComparison.OrdinalIgnoreCase)) RefreshUsageList();
    }

    // ============= 对话历史页 =============
    public ObservableCollection<ConversationListItem> HistoryItems { get; } = new();
    public ObservableCollection<UsageRow> UsageItems { get; } = new();

    private void OnHistorySearchChanged(object sender, TextChangedEventArgs e) => RefreshHistoryList();

    private void OnHistoryRefresh(object sender, RoutedEventArgs e) => RefreshHistoryList();

    private void RefreshHistoryList()
    {
        HistoryItems.Clear();
        if (_historyStore is null)
        {
            HistoryListBox.ItemsSource = HistoryItems;
            return;
        }
        var query = HistorySearchBox?.Text?.Trim();
        var list = string.IsNullOrWhiteSpace(query)
            ? _historyStore.Recent(120)
            : _historyStore.Search(query, 120);
        foreach (var s in list)
        {
            HistoryItems.Add(ConversationListItem.From(s));
        }
        HistoryListBox.ItemsSource = HistoryItems;
    }

    private void OnHistoryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryListBox.SelectedItem is not ConversationListItem item) return;
        _onOpenConversation?.Invoke(item.Id);
    }

    private void OnHistoryDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
        if (_historyStore?.Delete(id) == true)
        {
            var existing = HistoryItems.FirstOrDefault(x => x.Id == id);
            if (existing is not null) HistoryItems.Remove(existing);
        }
    }

    // ============= 用量页 =============
    private void RefreshUsageList()
    {
        UsageItems.Clear();
        UsageList.ItemsSource = UsageItems;
        if (_usage is null)
        {
            UsageTotalCalls.Text = UsageTotalPrompt.Text = UsageTotalCompletion.Text = UsageTotalElapsed.Text = "-";
            return;
        }
        var totals = _usage.Totals();
        UsageTotalCalls.Text = totals.Calls.ToString();
        UsageTotalPrompt.Text = FormatTokens(totals.PromptTokens);
        UsageTotalCompletion.Text = FormatTokens(totals.CompletionTokens);
        UsageTotalElapsed.Text = totals.TotalElapsed.TotalSeconds < 60
            ? $"{totals.TotalElapsed.TotalSeconds:0.0}s"
            : $"{totals.TotalElapsed.TotalMinutes:0.0}min";
        foreach (var d in _usage.Daily(30))
        {
            UsageItems.Add(new UsageRow(
                d.Date.ToString("yyyy-MM-dd"),
                $"{d.Calls} 次",
                $"提示 {FormatTokens(d.PromptTokens)}",
                $"补全 {FormatTokens(d.CompletionTokens)}"));
        }
    }

    private static string FormatTokens(int v)
        => v < 10_000 ? v.ToString() : (v / 1000.0).ToString("0.0") + "k";

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
            ("Actions", "动作 添加 内置 URL AI Prompt 模板 图标 排序"),
            ("Processes", "进程 过滤 黑名单 白名单 最近活动窗口"),
            ("Search", "搜索 引擎 Google Bing 百度 URL 模板"),
            ("Hotkeys", "快捷键 热键 暂停 恢复 唤起 工具栏"),
            ("AI", "AI 模型 Provider DeepSeek OpenAI API Key 自定义 测试连接"),
            ("Templates", "模板 Prompt template 改写 翻译 修语法 commit"),
            ("History", "历史 对话 history 会话 记录"),
            ("Usage", "用量 usage token 调用 统计"),
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
        if (hit is not null)
        {
            SearchEngineName.Text = hit.Value.Name;
            SearchUrlTemplate.Text = hit.Value.Url;
        }
        // 点击 Custom 不改文本，但仍要触发一次 commit 让 settings 即时落盘
        CommitAll();
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

    private void OnAddBuiltInAction(object sender, RoutedEventArgs e)
    {
        var alreadyAdded = ActionItems
            .Where(a => string.Equals(a.Type, "builtin", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(a.BuiltIn))
            .Select(a => a.BuiltIn!)
            .ToList();

        var dialog = new AddBuiltInActionDialog(alreadyAdded) { Owner = this };
        var ok = dialog.ShowDialog() == true;
        if (!ok || dialog.Selected.Count == 0) return;

        foreach (var seed in dialog.Selected)
        {
            AddAction(new ActionEditorItem
            {
                Id = UniqueActionId(seed.DescriptorId),
                Type = "builtin",
                BuiltIn = seed.BuiltIn,
                Title = seed.Title,
                Icon = seed.IconKey,
                IconLocked = true,
                Enabled = true,
            });
        }
    }

    /// <summary>"添加用户动作"：弹 AddUserActionDialog 让用户选 URL / AI 自定义 / 从内置 AI 模板派生。
    /// 与"添加内置动作"互补：那个对话框管"系统预置 + 不可重复"，本对话框管"用户自定义 + 可重复"，
    /// 旧版独立的"添加 URL"/"添加 AI 动作"/"快速从模板"三个入口合并到这一个对话框</summary>
    private void OnAddUserAction(object sender, RoutedEventArgs e)
    {
        var existingBuiltInIds = ActionItems
            .Where(a => string.Equals(a.Type, "builtin", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(a.BuiltIn))
            .Select(a => a.BuiltIn!)
            .ToList();
        var dialog = new AddUserActionDialog(BuiltinPromptTemplates, existingBuiltInIds) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.CreatedItem is null) return;

        var created = dialog.CreatedItem;
        var seed = string.Equals(created.Type, "url-template", StringComparison.OrdinalIgnoreCase)
            ? "url"
            : "ai";
        created.Id = UniqueActionId(seed);
        AddAction(created);
    }

    /// <summary>从内置 Prompt 模板派生 ai 动作。
    /// 模板代表"预定义功能"，图标承载语义，因此 IconLocked=true 不允许后续在 UI 中改图标</summary>
    private void OnAddFromTemplate(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not PromptTemplateDefinition tpl) return;
        AddAction(new ActionEditorItem
        {
            Id = UniqueActionId(tpl.Id.Replace("tpl.", "ai-", StringComparison.OrdinalIgnoreCase)),
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

    private void AddAction(ActionEditorItem item)
    {
        ActionItems.Add(item);
    }

    private string UniqueActionId(string seed)
    {
        var id = NormalizeIdSeed(seed);
        if (ActionItems.All(x => !string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))) return id;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{id}-{i}";
            if (ActionItems.All(x => !string.Equals(x.Id, candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
        return $"{id}-{Guid.NewGuid():N}";
    }

    /// <summary>根据内置动作 ID 给出与其语义匹配的图标 key。
    /// 从 BuiltInActionSeeds 取，保持与"添加内置动作"对话框、磁盘 actions.json 三处的图标一致</summary>
    private static string SuggestIcon(string builtIn)
    {
        var seed = BuiltInActionSeeds.All.FirstOrDefault(
            s => string.Equals(s.BuiltIn, builtIn, StringComparison.OrdinalIgnoreCase));
        return seed?.IconKey ?? "Ai";
    }

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

    private void OnToolbarOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateToolbarOpacityLabel(e.NewValue);
        CommitAll();
    }

    /// <summary>颜色主题选择变化：当前选择写入 settings 并立即应用到全局画刷。
    /// CommitAll → AppHost.ApplyRuntimeSettings → ThemeManager.Apply，
    /// 后者会同步更新 Application.Resources，让所有窗口随即换肤</summary>
    private void OnToolbarThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        CommitAll();
    }

    private void UpdateToolbarOpacityLabel(double value)
    {
        if (ToolbarOpacityValue is null) return;
        ToolbarOpacityValue.Text = (value * 100).ToString("0") + "%";
    }

    /// <summary>把系统已安装字体填入"界面字体 / 浮窗字体"两个 ComboBox。
    /// 使用 LocalizedFontNames 优先取中文族名，列表前补一个"默认"代表清空，
    /// 留空配置项是个常见姿势，IsEditable 允许用户键入未列出的字体名</summary>
    private void BindFontFamilyChoices()
    {
        var families = EnumerateSystemFontFamilies();
        // "默认字体"项实际写入的字符串是 ""，CommitAll 时会把它落到 settings；
        // FontFamilyHelper.ResolveUi/ResolveToolbar 看到空字符串后回退到内部链
        // （首选 Windows 当前消息字体 + 中文回退），所以切回此项就等同于"跟随 Windows 主体字体"。
        // 实际生效的字体名在下方"预览"文本中以原文显示，不再叠加"默认"前缀
        var defaultLabel = "默认字体";
        var choices = new List<FontFamilyChoice>(families.Count + 1)
        {
            new("", defaultLabel),
        };
        foreach (var name in families)
        {
            choices.Add(new FontFamilyChoice(name, name));
        }

        UiFontFamilyBox.ItemsSource = choices;
        UiFontFamilyBox.DisplayMemberPath = nameof(FontFamilyChoice.Label);
        UiFontFamilyBox.SelectedValuePath = nameof(FontFamilyChoice.Value);

        ToolbarFontFamilyBox.ItemsSource = choices;
        ToolbarFontFamilyBox.DisplayMemberPath = nameof(FontFamilyChoice.Label);
        ToolbarFontFamilyBox.SelectedValuePath = nameof(FontFamilyChoice.Value);

        SetFontComboValue(UiFontFamilyBox, _settings.UiFontFamily);
        SetFontComboValue(ToolbarFontFamilyBox, _settings.ToolbarFontFamily);
        RefreshFontPreviews();
    }

    private static IReadOnlyList<string> EnumerateSystemFontFamilies()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        try
        {
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            var fallback = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            foreach (var f in System.Windows.Media.Fonts.SystemFontFamilies)
            {
                var name = LocalizedName(f, culture) ?? LocalizedName(f, fallback) ?? f.Source;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!seen.Add(name)) continue;
                list.Add(name);
            }
            list.Sort(StringComparer.CurrentCulture);
        }
        catch
        {
            // 极端环境下字体列表枚举失败，至少给一个空集合让用户可以手动输入
        }
        return list;
    }

    private static string? LocalizedName(System.Windows.Media.FontFamily family, System.Globalization.CultureInfo culture)
    {
        try
        {
            if (family.FamilyNames.TryGetValue(System.Windows.Markup.XmlLanguage.GetLanguage(culture.IetfLanguageTag), out var name))
            {
                return name;
            }
        }
        catch
        {
            // FamilyNames 取值在某些字体上抛 NotSupportedException；忽略后落回 Source
        }
        return null;
    }

    private static void SetFontComboValue(WpfComboBox combo, string fontFamily)
    {
        var trimmed = (fontFamily ?? "").Trim();
        if (combo.ItemsSource is IEnumerable<FontFamilyChoice> choices)
        {
            var hit = choices.FirstOrDefault(c => string.Equals(c.Value, trimmed, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                combo.SelectedValue = hit.Value;
                combo.Text = hit.Label;
                return;
            }
        }
        // 配置中保存了系统并未列出的字体名（例如用户手动改了 json）：保留原文本，
        // 让 ComboBox 通过 IsEditable=true 仍能显示并使用
        combo.SelectedValue = null;
        combo.Text = trimmed;
    }

    private void OnUiFontFamilyChanged(object sender, RoutedEventArgs e)
    {
        RefreshFontPreviews();
        CommitAll();
    }

    private void OnToolbarFontFamilyChanged(object sender, RoutedEventArgs e)
    {
        RefreshFontPreviews();
        CommitAll();
    }

    private void OnResetFontFamilies(object sender, RoutedEventArgs e)
    {
        SetFontComboValue(UiFontFamilyBox, "");
        SetFontComboValue(ToolbarFontFamilyBox, "");
        RefreshFontPreviews();
        CommitAll();
    }

    private void RefreshFontPreviews()
    {
        var uiInput = NormalizeFontFamilyInput(UiFontFamilyBox);
        var toolbarInput = NormalizeFontFamilyInput(ToolbarFontFamilyBox);
        var uiFont = FontFamilyHelper.ResolveUi(uiInput);
        var toolbarFont = FontFamilyHelper.ResolveToolbar(toolbarInput, uiInput);

        // 预览文本格式："字体名 · 示例"。
        // 留空时直接显示实际生效的字体名（Windows 当前消息字体），让用户对照下拉里的"默认字体"
        // 也能一眼看出真实效果
        var uiName = string.IsNullOrWhiteSpace(uiInput) ? FontFamilyHelper.PreferredUiName : uiInput;
        var toolbarName = string.IsNullOrWhiteSpace(toolbarInput)
            ? (string.IsNullOrWhiteSpace(uiInput) ? FontFamilyHelper.PreferredUiName : uiInput)
            : toolbarInput;

        try
        {
            UiFontPreview.FontFamily = new System.Windows.Media.FontFamily(uiFont);
            UiFontPreview.Text = $"{uiName} · 预览 AaBb 你好";
        }
        catch { /* 输入了无效族名也不要抛 */ }
        try
        {
            ToolbarFontPreview.FontFamily = new System.Windows.Media.FontFamily(toolbarFont);
            ToolbarFontPreview.Text = $"{toolbarName} · 预览 AaBb 你好";
        }
        catch { /* 同上 */ }
    }

    /// <summary>从 IsEditable ComboBox 中取用户输入或选项值。
    /// 1) 优先看 SelectedItem 是否就是已知的 FontFamilyChoice：是则使用其 Value（"默认字体"项 Value="" 直接命中）
    /// 2) 否则取 Text：用户键入了未列出的字体名时走这条分支
    /// 3) Text 以"默认 / 系统默认"等中文 Label 开头时归一为空，避免被误当作字体名</summary>
    private static string NormalizeFontFamilyInput(WpfComboBox combo)
    {
        if (combo.SelectedItem is FontFamilyChoice picked)
        {
            return picked.Value?.Trim() ?? "";
        }
        var text = combo.Text?.Trim() ?? "";
        if (text.StartsWith("默认字体", StringComparison.Ordinal)) return "";
        if (text.StartsWith("系统默认", StringComparison.Ordinal)) return "";
        if (text.StartsWith("默认", StringComparison.Ordinal)) return "";
        return text;
    }

    private void OnPopupModeChanged(object sender, SelectionChangedEventArgs e)
    {
        var mode = SelectedTag(PopupModeBox);
        PopupDelayBox.IsEnabled = mode == nameof(SelectionPopupMode.Delayed);
        HoverDelayBox.IsEnabled = mode == nameof(SelectionPopupMode.HoverStill);
        RequiredModifierBox.IsEnabled = mode == nameof(SelectionPopupMode.ModifierRequired);
    }

    /// <summary>把当前 UI 控件的全部值同步到 _settings，并持久化 + 通知 AppHost 应用。
    /// "点击即生效"模式：任何受监听的控件触发变化时都会走这里；
    /// _suspendCommit 期间静默（用于 Bind 阶段批量初始化 IsChecked / SelectedValue），
    /// 避免在数据尚未就位时就开始写盘</summary>
    private void CommitAll()
    {
        if (_suspendCommit) return;
        SyncSettingsFromUi();
        PersistAndNotify();
    }

    /// <summary>统一的事件转接器：任何控件 Changed/LostFocus 都走它，
    /// 内部委托给 CommitAll，签名兼容 RoutedEventArgs 派生的所有事件参数</summary>
    private void OnInstantCommit(object sender, RoutedEventArgs e) => CommitAll();

    /// <summary>DependencyPropertyDescriptor.AddValueChanged 的回调签名（无 sender RoutedEventArgs）</summary>
    private void OnInstantCommitPlain(object? sender, EventArgs e) => CommitAll();

    private void SyncSettingsFromUi()
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
        _settings.ToolbarTheme = ThemePresetList.SelectedValue is ToolbarThemeMode pickedTheme
            ? pickedTheme
            : ToolbarThemeMode.Auto;
        _settings.UiFontFamily = NormalizeFontFamilyInput(UiFontFamilyBox);
        _settings.ToolbarFontFamily = NormalizeFontFamilyInput(ToolbarFontFamilyBox);
        _settings.ToolbarSurface = SurfaceShadow.IsChecked == true
            ? ToolbarSurfaceStyle.Shadow
            : SurfaceBorder.IsChecked == true
                ? ToolbarSurfaceStyle.Border
                : ToolbarSurfaceStyle.ShadowAndBorder;
        _settings.ToolbarDensity = DensityCompact.IsChecked == true
            ? ToolbarDensity.Compact
            : DensityComfortable.IsChecked == true
                ? ToolbarDensity.Comfortable
                : ToolbarDensity.Standard;
        _settings.ToolbarLayoutMode = ToolbarLayoutSmartRow.IsChecked == true
            ? ToolbarLayoutMode.SmartOnSeparateRow
            : ToolbarLayoutGroupRows.IsChecked == true
                ? ToolbarLayoutMode.GroupRows
                : ToolbarLayoutMode.Single;
        _settings.FollowAccentColor = FollowAccentColor.IsChecked == true;
        _settings.ToolbarCornerRadius = NumberBoxDouble(CornerRadiusBox, _settings.ToolbarCornerRadius, 0, 18);
        _settings.ToolbarButtonSpacing = NumberBoxDouble(ButtonSpacingBox, _settings.ToolbarButtonSpacing, 0, 10);
        _settings.ToolbarFontSize = NumberBoxDouble(ToolbarFontSizeBox, _settings.ToolbarFontSize, 10, 18);
        _settings.ToolbarMaxActionsPerRow = NumberBoxInt(MaxActionsPerRowBox, _settings.ToolbarMaxActionsPerRow, 3, 12);
        _settings.ToolbarIdleOpacity = Math.Clamp(ToolbarOpacitySlider.Value, 0.3, 1.0);
        _settings.EnableToolbarKeyboardShortcuts = EnableToolbarKeyboardShortcutsBox.IsChecked == true;
        _settings.EnableToolbarTabNavigation = EnableToolbarTabNavigationBox.IsChecked == true;
        _settings.EnableToolbarNumberShortcuts = EnableToolbarNumberShortcutsBox.IsChecked == true;

        _settings.PopupMode = Enum.TryParse<SelectionPopupMode>(SelectedTag(PopupModeBox), out var popupMode)
            ? popupMode
            : SelectionPopupMode.Immediate;
        _settings.RequiredModifier = Enum.TryParse<SelectionModifierKey>(SelectedTag(RequiredModifierBox), out var modifier)
            ? modifier
            : SelectionModifierKey.Alt;
        _settings.QuickClickModifier = Enum.TryParse<SelectionModifierKey>(SelectedTag(QuickClickModifierBox), out var quickClickModifier)
            ? quickClickModifier
            : SelectionModifierKey.Ctrl;
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
        _settings.DismissOnTimeout = DismissOnTimeoutBox.IsChecked == true;
        _settings.DismissTimeoutMs = NumberBoxInt(DismissTimeoutMsBox, _settings.DismissTimeoutMs, 100, 120000);
        _settings.LogLevel = LogLevelBox.SelectedValue is LogLevel logLevel ? logLevel : LogLevel.Debug;

        var name = SearchEngineName.Text?.Trim() ?? "";
        var url = SearchUrlTemplate.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(name)) _settings.SearchEngineName = name;
        if (!string.IsNullOrEmpty(url) && url.Contains("{q}", StringComparison.Ordinal))
        {
            _settings.SearchUrlTemplate = url;
        }

        _settings.PauseHotKey = PauseHotKeyBox.Text.Trim();
        _settings.ToolbarHotKey = ToolbarHotKeyBox.Text.Trim();
        _settings.OcrHotKey = OcrHotKeyBox.Text.Trim();
        SaveAiSettings();
        RefreshToolbarPreview();
    }

    private void PersistAndNotify()
    {
        // 仅在"AI 首次启用"那一次播种默认动作；后续提交不再触发，
        // 这样用户删了"修语法/润色/三句话总结"再切换 AI 开关也不会被强制补回。
        // EnsureDefaultAiActions 内部会修改 ActionItems，会触发集合变更回调（递归 CommitAll），
        // 因此用 _suspendCommit 包裹一层防递归
        if (_settings.AiEnabled && !_settings.AiDefaultActionsSeeded)
        {
            var prev = _suspendCommit;
            _suspendCommit = true;
            try { EnsureDefaultAiActions(); }
            finally { _suspendCommit = prev; }
            _settings.AiDefaultActionsSeeded = true;
        }
        _store.SaveSettings(_settings);
        _store.SaveActions(new ActionsConfig
        {
            SchemaVersion = 1,
            Actions = ActionItems.Select(x => x.ToDescriptor()).ToList(),
        });
        Saved?.Invoke();
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
        AddDefaultPromptAction("tpl.fix-grammar");
        AddDefaultPromptAction("tpl.polish");
        AddDefaultPromptAction("tpl.summarize");
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

    /// <summary>从内置 Prompt 模板派生动作。
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

    /// <summary>UniqueActionId 内部的归一化逻辑提炼出来，供"查重 + 生成"两条路径共享。
    /// 入参例如 "tpl.fix-grammar" → "tpl-fix-grammar"</summary>
    private static string NormalizeIdSeed(string seed)
        => seed.Replace("builtin-", "", StringComparison.OrdinalIgnoreCase)
               .Replace("builtin.", "", StringComparison.OrdinalIgnoreCase)
               .Replace(".", "-", StringComparison.Ordinal);

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

    private static string DetectUiaStatus()
    {
        try
        {
            _ = AutomationElement.FocusedElement;
            return "可用";
        }
        catch (Exception ex)
        {
            return "不可用：" + ex.Message;
        }
    }

    private static string DetectPrivilegeStatus()
    {
        try
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator) ? "管理员" : "普通用户";
        }
        catch
        {
            return "未知";
        }
    }

    private void OnOpenLogDirectory(object sender, RoutedEventArgs e)
        => OpenPath(LogDirectoryPath);

    private void OnOpenConfigDirectory(object sender, RoutedEventArgs e)
        => OpenPath(ConfigDirectoryPath);

    private void OpenPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("打开目录失败：" + ex.Message, "ClipAura", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private AiThinkingMode SelectedAiThinkingMode()
        => AiThinkingModeBox.SelectedValue is AiThinkingMode mode
            ? mode
            : AiThinkingMode.Auto;

    /// <summary>从外观页控件当前值派生预览的 Margin / Padding / 字号 / 阴影 / 透明度 等参数。
    /// 主题与字体走 DynamicResource 自动跟随，不需要在这里推送</summary>
    private void RefreshToolbarPreview()
    {
        if (DisplayIconAndText is null) return;
        var iconOn = DisplayIconOnly.IsChecked != true && DisplayTextOnly.IsChecked != true
            || DisplayIconOnly.IsChecked == true;
        var textOn = DisplayIconOnly.IsChecked != true && DisplayTextOnly.IsChecked != true
            || DisplayTextOnly.IsChecked == true;
        // 上面两条只决定"是否两者都开"，下面再单独处理 IconOnly / TextOnly 排他
        if (DisplayIconOnly.IsChecked == true) { iconOn = true; textOn = false; }
        else if (DisplayTextOnly.IsChecked == true) { iconOn = false; textOn = true; }
        PreviewIconVisibility = iconOn ? Visibility.Visible : Visibility.Collapsed;
        PreviewTextVisibility = textOn ? Visibility.Visible : Visibility.Collapsed;

        var radius = NumberBoxDouble(CornerRadiusBox, _settings.ToolbarCornerRadius, 0, 18);
        var spacing = NumberBoxDouble(ButtonSpacingBox, _settings.ToolbarButtonSpacing, 0, 10);
        var fontSize = NumberBoxDouble(ToolbarFontSizeBox, _settings.ToolbarFontSize, 10, 18);
        // 与浮窗实例化时一致：外圆角 = 用户值 + 1，给描边留 1px 抗锯齿余量
        PreviewCornerRadius = new CornerRadius(radius + 1);
        PreviewButtonCornerRadius = new CornerRadius(0);
        PreviewButtonMargin = new Thickness(spacing, 0, spacing, 0);
        // 按钮内边距跟随密度档：紧凑 / 标准（默认）/ 宽松，让预览与真实浮窗呈相同观感
        var (padX, padY) = ResolvePreviewPadding();
        PreviewButtonPadding = new Thickness(padX, padY, padX, padY);
        PreviewFontSize = fontSize;
        PreviewIconFontSize = fontSize + 2;
        PreviewOpacity = Math.Clamp(ToolbarOpacitySlider.Value, 0.3, 1.0);

        var surface = SurfaceShadow.IsChecked == true
            ? ToolbarSurfaceStyle.Shadow
            : SurfaceBorder.IsChecked == true
                ? ToolbarSurfaceStyle.Border
                : ToolbarSurfaceStyle.ShadowAndBorder;
        // 阴影参数与 FloatingToolbar.ApplySurfaceStyle 严格对齐，
        // 切换"按钮风格"时预览与浮窗呈现完全相同的"浮起"层次
        switch (surface)
        {
            case ToolbarSurfaceStyle.Shadow:
                PreviewBorderThickness = new Thickness(0);
                PreviewShadowBlurRadius = 10;
                PreviewShadowDepth = 3;
                PreviewShadowOpacity = 0.55;
                break;
            case ToolbarSurfaceStyle.Border:
                PreviewBorderThickness = new Thickness(1);
                PreviewShadowBlurRadius = 0;
                PreviewShadowDepth = 0;
                PreviewShadowOpacity = 0;
                break;
            default:
                PreviewBorderThickness = new Thickness(1);
                PreviewShadowBlurRadius = 8;
                PreviewShadowDepth = 2;
                PreviewShadowOpacity = 0.42;
                break;
        }
    }

    /// <summary>把当前选中的密度档位映射成按钮 padding。
    /// 与 FloatingToolbar.PaddingForDensity 数值严格一致，保持预览与真实浮窗同款观感</summary>
    private (double X, double Y) ResolvePreviewPadding()
    {
        if (DensityCompact is not null && DensityCompact.IsChecked == true) return (8, 5);
        if (DensityComfortable is not null && DensityComfortable.IsChecked == true) return (16, 13);
        return (12, 9);
    }

    /// <summary>把所有"点击/输入即生效"的控件挂上统一的 CommitAll 回调。
    /// 在 Loaded 后调用，避免与 Bind 阶段批量初始化撞车；
    /// 部分控件（ThemePresetList、PopupModeBox、UI/Toolbar 字体下拉等）已经在 XAML 上指向了
    /// 专用 handler，那些 handler 内会自己调 CommitAll，因此这里不再重复挂</summary>
    private void HookInstantCommit()
    {
        // ToggleSwitch / CheckBox / RadioButton 走 Checked/Unchecked
        AttachToggle(FullScreenSuppress, LaunchAtStartup, EnableSelectAllPopupBox,
            EnableToolbarKeyboardShortcutsBox, EnableToolbarTabNavigationBox, EnableToolbarNumberShortcutsBox,
            DismissOnMouseLeaveBox, DismissOnForegroundChangedBox, DismissOnClickOutsideBox,
            DismissOnEscapeKeyBox, DismissOnNewSelectionBox, DismissOnActionInvokedBox,
            DismissOnTimeoutBox, FollowAccentColor, AiEnabledBox,
            TranslateInlineBox, ExplainEnabledBox);
        AttachRadio(BlacklistRadio, WhitelistRadio,
            DisplayIconAndText, DisplayIconOnly, DisplayTextOnly,
            SurfaceShadow, SurfaceBorder, SurfaceShadowAndBorder,
            DensityCompact, DensityStandard, DensityComfortable);

        // ComboBox SelectionChanged
        AttachCombo(RequiredModifierBox, QuickClickModifierBox, AiThinkingModeBox, LogLevelBox);
        // PopupModeBox 已挂 OnPopupModeChanged，再补一个 commit 即可（多 handler 共存不冲突）
        PopupModeBox.SelectionChanged += OnInstantCommit;

        // NumberBox 的 ValueProperty 不暴露 RoutedEventArgs 事件，用 DependencyPropertyDescriptor 监听
        AttachNumber(MinTextLengthBox, MaxTextLengthBox, PopupDelayBox, HoverDelayBox,
            DismissMouseLeaveDelayBox, DismissTimeoutMsBox,
            CornerRadiusBox, ButtonSpacingBox, ToolbarFontSizeBox, MaxActionsPerRowBox,
            AiTimeoutBox, AiMaxOutputTokensBox);

        // TextBox / PasswordBox：用 LostFocus 而不是 TextChanged，避免每个键盘字符都触发写盘
        AttachTextLostFocus(SearchEngineName, SearchUrlTemplate,
            PauseHotKeyBox, ToolbarHotKeyBox, OcrHotKeyBox,
            AiBaseUrlBox, AiModelBox, AiDefaultLanguageBox);
        AiApiKeyBox.LostFocus += OnInstantCommit;

        // 集合：动作列表 / 进程过滤名单
        ProcessFilters.CollectionChanged += (_, _) => CommitAll();
        ActionItems.CollectionChanged += OnActionsCollectionChanged;
        foreach (var existing in ActionItems)
        {
            existing.PropertyChanged += OnActionItemChanged;
        }
    }

    private void AttachToggle(params System.Windows.Controls.Primitives.ToggleButton?[] toggles)
    {
        foreach (var t in toggles)
        {
            if (t is null) continue;
            t.Checked += OnInstantCommit;
            t.Unchecked += OnInstantCommit;
        }
    }

    private void AttachRadio(params System.Windows.Controls.Primitives.ToggleButton?[] radios)
    {
        // RadioButton 复用 ToggleButton；OnCheckChanged 只关心 Checked 即可（同组 Unchecked 总会伴随另一项 Checked）
        foreach (var r in radios)
        {
            if (r is null) continue;
            r.Checked += OnInstantCommit;
        }
    }

    private void AttachCombo(params System.Windows.Controls.Primitives.Selector?[] combos)
    {
        foreach (var c in combos)
        {
            if (c is null) continue;
            c.SelectionChanged += OnInstantCommit;
        }
    }

    private void AttachNumber(params Wpf.Ui.Controls.NumberBox?[] boxes)
    {
        var dpd = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
            Wpf.Ui.Controls.NumberBox.ValueProperty,
            typeof(Wpf.Ui.Controls.NumberBox));
        if (dpd is null) return;
        foreach (var b in boxes)
        {
            if (b is null) continue;
            dpd.AddValueChanged(b, OnInstantCommitPlain);
        }
    }

    private void AttachTextLostFocus(params Control?[] controls)
    {
        foreach (var c in controls)
        {
            if (c is null) continue;
            c.LostFocus += OnInstantCommit;
        }
    }

    private void OnActionsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (ActionEditorItem item in e.NewItems)
            {
                item.PropertyChanged += OnActionItemChanged;
            }
        }
        if (e.OldItems is not null)
        {
            foreach (ActionEditorItem item in e.OldItems)
            {
                item.PropertyChanged -= OnActionItemChanged;
            }
        }
        CommitAll();
    }

    private void OnActionItemChanged(object? sender, PropertyChangedEventArgs e) => CommitAll();
}

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
        // - 内置 smart 动作走 BuiltInOutputMode，默认 "copyAndBubble"
        // - 其它内置（Copy/Paste/Search 等）不读取 OutputMode，留空即可
        var defaultOutputMode = string.Equals(descriptor.Type, "ai", StringComparison.OrdinalIgnoreCase)
            ? "chat"
            : (!string.IsNullOrEmpty(descriptor.BuiltIn) && BuiltInOutputModes.SupportsOutputMode(descriptor.BuiltIn)
                ? BuiltInOutputModes.CopyAndBubble
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
                ? BuiltInOutputModes.CopyAndBubble
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
