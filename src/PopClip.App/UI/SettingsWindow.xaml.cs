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
    private readonly Func<HotKeyApplyResult?>? _hotKeyStatusProvider;
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
        new AiThinkingModeChoice(AiThinkingMode.Auto, "服务商默认", "客户端不传 thinking / reasoning 参数，由 AI 服务决定"),
        new AiThinkingModeChoice(AiThinkingMode.Fast, "快速", "默认选择。DeepSeek 关闭 thinking；OpenAI 使用 low reasoning"),
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
        new AiOutputModeChoice("clipboard", "复制到剪贴板"),
        new AiOutputModeChoice("toast", "Toast"),
        new AiOutputModeChoice("bubble", "气泡显示"),
    };

    /// <summary>智能动作的输出模式选项。用 AiOutputModeChoice 类型复用同款 (Value, Label) 元组，
    /// 避免再造一个等价类型；XAML 端通过 IsBuiltInOutputConfigurable 决定是否显示此下拉</summary>
    public IReadOnlyList<AiOutputModeChoice> BuiltInOutputModeChoices { get; } = new[]
    {
        new AiOutputModeChoice(BuiltInOutputModes.Toast, "Toast"),
        new AiOutputModeChoice(BuiltInOutputModes.Bubble, "显示气泡"),
        new AiOutputModeChoice(BuiltInOutputModes.ClipboardAndBubble, "显示气泡并复制"),
        new AiOutputModeChoice(BuiltInOutputModes.Dialog, "显示对话框"),
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
    private CornerRadius _previewCornerRadius = new(8);
    private CornerRadius _previewButtonCornerRadius = new(0);
    private Thickness _previewButtonMargin = new(2, 0, 2, 0);
    private Thickness _previewButtonPadding = new(12, 9, 12, 9);
    private Thickness _previewBorderThickness = new(1);
    private double _previewFontSize = 13;
    private double _previewIconFontSize = 15;
    private double _previewShadowDepth = 1;
    private double _previewShadowBlurRadius = 6;
    private double _previewShadowOpacity = 0.20;
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
        IReadOnlyList<IOcrProvider>? ocrProviders = null,
        Func<HotKeyApplyResult?>? hotKeyStatusProvider = null)
    {
        _store = store;
        _settings = settings;
        _historyStore = historyStore;
        _usage = usage;
        _onOpenConversation = onOpenConversation;
        _ocrProviders = ocrProviders ?? Array.Empty<IOcrProvider>();
        _hotKeyStatusProvider = hotKeyStatusProvider;
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
            ["Ocr"] = OcrPage,
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
        SelectComboByTag(TrayClickActionBox, _settings.TrayClickAction.ToString());
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
        ScreenshotHotKeyBox.Text = _settings.ScreenshotHotKey;
        EnablePauseHotKeyBox.IsChecked = _settings.EnablePauseHotKey;
        EnableToolbarHotKeyBox.IsChecked = _settings.EnableToolbarHotKey;
        EnableOcrHotKeyBox.IsChecked = _settings.EnableOcrHotKey;
        EnableScreenshotHotKeyBox.IsChecked = _settings.EnableScreenshotHotKey;
        EnablePrintScreenScreenshotBox.IsChecked = _settings.EnablePrintScreenScreenshot;
        RefreshHotKeyRegistrationStatus();

        BindOcrProviders();
        BindOcrResultMode();

        BindAiSettings();

        var actions = _store.LoadActions() ?? DefaultActionsFactory.Create();
        foreach (var action in actions.Actions)
        {
            ActionItems.Add(ActionEditorItem.FromDescriptor(action));
        }
        RefreshToolbarPreview();
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
        _settings.TrayClickAction = Enum.TryParse<TrayClickAction>(SelectedTag(TrayClickActionBox), out var trayClickAction)
            ? trayClickAction
            : TrayClickAction.Menu;
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
        _settings.EnablePauseHotKey = EnablePauseHotKeyBox.IsChecked == true;
        _settings.EnableToolbarHotKey = EnableToolbarHotKeyBox.IsChecked == true;
        _settings.EnableOcrHotKey = EnableOcrHotKeyBox.IsChecked == true;
        _settings.EnableScreenshotHotKey = EnableScreenshotHotKeyBox.IsChecked == true;
        _settings.EnablePrintScreenScreenshot = EnablePrintScreenScreenshotBox.IsChecked == true;

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
        _settings.ScreenshotHotKey = ScreenshotHotKeyBox.Text.Trim();
        _settings.ScreenshotAfterCaptureMode = ScreenshotModeClipboardRadio.IsChecked == true
            ? ScreenshotAfterCaptureMode.Clipboard
            : ScreenshotAfterCaptureMode.Toolbar;
        _settings.ScreenshotAutoOcr = ScreenshotAutoOcrBox.IsChecked == true;
        _settings.ScreenshotAutoSaveEnabled = ScreenshotAutoSaveEnabledBox.IsChecked == true;
        var dirText = ScreenshotSaveDirectoryBox.Text?.Trim();
        if (!string.IsNullOrEmpty(dirText)) _settings.ScreenshotSaveDirectory = dirText;
        var nameTpl = ScreenshotFileNameTemplateBox.Text?.Trim();
        if (!string.IsNullOrEmpty(nameTpl)) _settings.ScreenshotFileNameTemplate = nameTpl;
        UpdateScreenshotFileNamePreview();

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

    /// <summary>把所有"点击/输入即生效"的控件挂上统一的 CommitAll 回调。
    /// 在 Loaded 后调用，避免与 Bind 阶段批量初始化撞车；
    /// 部分控件（ThemePresetList、PopupModeBox、UI/Toolbar 字体下拉等）已经在 XAML 上指向了
    /// 专用 handler，那些 handler 内会自己调 CommitAll，因此这里不再重复挂</summary>
    private void HookInstantCommit()
    {
        // ToggleSwitch / CheckBox / RadioButton 走 Checked/Unchecked
        AttachToggle(FullScreenSuppress, LaunchAtStartup, EnableSelectAllPopupBox,
            EnablePauseHotKeyBox, EnableToolbarHotKeyBox, EnableOcrHotKeyBox, EnableScreenshotHotKeyBox,
            EnablePrintScreenScreenshotBox,
            EnableToolbarKeyboardShortcutsBox, EnableToolbarTabNavigationBox, EnableToolbarNumberShortcutsBox,
            DismissOnMouseLeaveBox, DismissOnForegroundChangedBox, DismissOnClickOutsideBox,
            DismissOnEscapeKeyBox, DismissOnNewSelectionBox, DismissOnActionInvokedBox,
            DismissOnTimeoutBox, FollowAccentColor, AiEnabledBox,
            TranslateInlineBox, ExplainEnabledBox, ScreenshotAutoOcrBox,
            ScreenshotAutoSaveEnabledBox);
        AttachRadio(BlacklistRadio, WhitelistRadio,
            DisplayIconAndText, DisplayIconOnly, DisplayTextOnly,
            SurfaceShadow, SurfaceBorder, SurfaceShadowAndBorder,
            DensityCompact, DensityStandard, DensityComfortable,
            ScreenshotModeToolbarRadio, ScreenshotModeClipboardRadio);

        // ComboBox SelectionChanged
        AttachCombo(TrayClickActionBox, RequiredModifierBox, QuickClickModifierBox, AiThinkingModeBox, LogLevelBox);
        // PopupModeBox 已挂 OnPopupModeChanged，再补一个 commit 即可（多 handler 共存不冲突）
        PopupModeBox.SelectionChanged += OnInstantCommit;

        // NumberBox 的 ValueProperty 不暴露 RoutedEventArgs 事件，用 DependencyPropertyDescriptor 监听
        AttachNumber(MinTextLengthBox, MaxTextLengthBox, PopupDelayBox, HoverDelayBox,
            DismissMouseLeaveDelayBox, DismissTimeoutMsBox,
            CornerRadiusBox, ButtonSpacingBox, ToolbarFontSizeBox, MaxActionsPerRowBox,
            AiTimeoutBox, AiMaxOutputTokensBox);

        // TextBox / PasswordBox：用 LostFocus 而不是 TextChanged，避免每个键盘字符都触发写盘
        AttachTextLostFocus(SearchEngineName, SearchUrlTemplate,
            PauseHotKeyBox, ToolbarHotKeyBox, OcrHotKeyBox, ScreenshotHotKeyBox,
            AiBaseUrlBox, AiModelBox, AiDefaultLanguageBox,
            ScreenshotSaveDirectoryBox, ScreenshotFileNameTemplateBox);
        // 文件名模板预览随 TextChanged 实时刷新（CommitAll 内会再校准一次）
        ScreenshotFileNameTemplateBox.TextChanged += (_, _) => UpdateScreenshotFileNamePreview();
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

    private void OnActionItemChanged(object? sender, PropertyChangedEventArgs e) => CommitAll();
}
