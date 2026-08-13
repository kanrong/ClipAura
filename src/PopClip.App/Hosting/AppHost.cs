using PopClip.Actions.BuiltIn;
using PopClip.App.Config;
using PopClip.App.Services;
using PopClip.App.UI;
using PopClip.Core.Logging;
using PopClip.Hooks;
using PopClip.Uia;
using PopClip.Uia.Clipboard;
using Microsoft.Win32;
using System.Diagnostics;
using System.Windows;
using WpfApplication = System.Windows.Application;

namespace PopClip.App.Hosting;

/// <summary>整个应用的对象装配点。手动 DI 即可，无需 IServiceCollection 的复杂度。
/// 历史 / OCR 各自收进 Composition，避免宿主字段继续膨胀。</summary>
internal sealed class AppHost : IDisposable
{
    private readonly ILog _log = ConsoleLog.Instance;

    private SingleInstance? _instance;
    private ConfigStore? _store;
    private AppSettings? _settings;
    private ClipboardThread? _clipboardThread;
    private InputWatcher? _watcher;
    private TextAcquisitionService? _acquisition;
    private TextReplacerService? _replacer;
    private ActionCatalog? _catalog;
    private ActionHost? _actionHost;
    private SuppressionGate? _gate;
    private FloatingToolbar? _toolbar;
    private SelectionSessionManager? _session;
    private TrayController? _tray;
    private PauseState? _pause;
    private HotKeyManager? _hotkeys;
    private SettingsWindow? _settingsWindow;
    private HistoryComposition? _history;
    private OcrComposition? _ocr;
    private OfflineDictionaryService? _dictionary;
    private HotKeyApplyResult? _lastHotKeyApplyResult;

    public bool TryAcquireSingleInstance()
    {
        _instance = new SingleInstance(_log);
        return _instance.TryAcquire();
    }

    public void SignalRunningInstance() => SingleInstance.Signal("show-settings");

    public void Start()
    {
        StartIpcAndConfig();
        var pipeline = BuildTextPipeline();
        BuildUiShell();
        _history = HistoryComposition.Create(
            _log, pipeline.ClipboardAccess, pipeline.ClipboardWriter, _replacer!, pipeline.ClipboardPaste);
        BuildSession(pipeline);
        BuildOcr(pipeline);
        BindHotKeys();

        _watcher!.Start();
        _session!.Start();
        if (_settings is { FirstRunCompleted: false })
        {
            WpfApplication.Current.Dispatcher.BeginInvoke(new Action(() => ShowSettingsWindow(null)));
        }

        _log.Info("ClipAura started");
    }

    private void StartIpcAndConfig()
    {
        _instance!.CommandReceived += OnIpcCommand;
        _instance.StartIpcServer();

        _store = new ConfigStore(_log);
        _settings = _store.LoadSettings();
        ConsoleLog.Instance.SetMinimumLevel(_settings.LogLevel);
    }

    private TextPipeline BuildTextPipeline()
    {
        _clipboardThread = new ClipboardThread();
        _clipboardThread.Start();
        var clipboardAccess = new ClipboardAccess(_clipboardThread);
        // 兜底采集到的选区多格式快照在采集端写入、复制动作端读取，二者共享同一实例
        var capturedSelection = new CapturedSelectionCache();

        _watcher = new InputWatcher(_log);

        var uiaAcquirer = new UiaTextAcquirer(_log);
        var clipboardFallback = new ClipboardFallback(_log, clipboardAccess, capturedSelection);
        _acquisition = new TextAcquisitionService(_log, uiaAcquirer, clipboardFallback);

        var uiaReplacer = new UiaTextReplacer(_log);
        var clipboardPaste = new ClipboardPaste(_log, clipboardAccess);
        _replacer = new TextReplacerService(_log, uiaReplacer, clipboardPaste);

        // PasteService 必须在 ActionCatalog 之前构造：PasteAction.CanRun 通过它判定
        // "剪贴板是否有文本"，因此目录在装配每个内置动作时就需要这个能力
        var pasteService = new PasteService(_log, clipboardAccess, clipboardPaste, capturedSelection);
        _dictionary = new OfflineDictionaryService(_log);
        _catalog = new ActionCatalog(_log, pasteService, _dictionary);
        var cfg = _store!.LoadActions();
        if (cfg is not null)
        {
            _catalog.Load(cfg);
        }
        else
        {
            _catalog.Load(DefaultActionsFactory.Create());
        }

        var clipboardWriter = new ClipboardWriter(_log, clipboardAccess);
        return new TextPipeline(clipboardAccess, clipboardPaste, clipboardWriter, pasteService);
    }

    private void BuildUiShell()
    {
        _pause = new PauseState();
        _tray = new TrayController(_log, _pause);
        _tray.OnSettingsRequested += tag => ShowSettingsWindow(tag);
        _tray.OnClipboardHistoryRequested += OpenClipboardHistory;
        _tray.OnLeftClickRequested += HandleTrayLeftClick;
        _tray.OnExitRequested += () => WpfApplication.Current.Shutdown();
        _tray.OnDelayedScreenshotRequested += seconds => _ocr?.Coordinator.TriggerScreenshotDelayed(seconds);
        _tray.Show();

        _toolbar = new FloatingToolbar(_log);
        _toolbar.PrewarmLayout();
        // 系统主题/强调色变化时，由浮窗 WndProc 转发到这里重新跑一次全局主题应用
        _toolbar.SystemThemeChanged += OnSystemThemeChanged;
    }

    private void BuildSession(TextPipeline pipeline)
    {
        var settingsProvider = new SettingsProvider(_settings!);
        var aiTextService = new AiTextService(
            _log, _settings!, _replacer!, pipeline.ClipboardWriter, _toolbar!,
            clipboardAccess: pipeline.ClipboardAccess,
            historyStore: _history!.Conversations,
            usage: _history.Usage,
            saveSettings: SaveSettings);
        var resultDialog = new SmartResultDialogPresenter();
        var bubblePresenter = new FloatingToolbarBubblePresenter(_log, _toolbar!);
        var urlLauncher = new UrlLauncher(_log);
        _actionHost = new ActionHost(
            _log, _replacer!, urlLauncher, pipeline.ClipboardWriter, _toolbar!,
            settingsProvider, aiTextService, _dictionary!, pipeline.PasteService, _history.Launcher,
            resultDialog: resultDialog, bubble: bubblePresenter);

        _gate = new SuppressionGate(_log, _settings!);
        _session = new SelectionSessionManager(
            _log, _watcher!, _acquisition!, _replacer!, _catalog!, _actionHost, _gate, _toolbar!, _pause!, _settings!,
            pipeline.ClipboardAccess, pipeline.ClipboardPaste);

        ApplyRuntimeSettings(reloadActions: false);

        _watcher!.GlobalKeyHandler = key =>
        {
            // 优先级：浮窗 → AI 气泡。浮窗能处理（如 ESC 关浮窗、数字键触发动作）时短路；
            // 浮窗未处理且气泡可见时，ESC 关闭气泡（VK_ESCAPE = 0x1B）
            if (_toolbar!.TryHandleGlobalKey(key)) return true;
            if (key.IsDown && key.VirtualKey == 0x1B) return AiBubbleWindow.TryHandleEscape();
            return false;
        };

        _session.OcrLauncher = () => _ocr?.Coordinator.Trigger();
        _session.ScreenshotLauncher = () => _ocr?.Coordinator.TriggerScreenshot();
        _session.ClipboardImageOcrLauncher = anchor => _ocr?.Coordinator.TriggerClipboardImage(anchor);
        pipeline.AiText = aiTextService;
        pipeline.Bubble = bubblePresenter;
    }

    private void BuildOcr(TextPipeline pipeline)
    {
        _ocr = OcrComposition.Create(
            _log, _settings!, _session!, pipeline.ClipboardWriter, pipeline.ClipboardAccess,
            _toolbar!, pipeline.AiText!, pipeline.Bubble!, _history?.ClipboardHistory,
            openSettings: tag => ShowSettingsWindow(tag),
            saveSettings: SaveSettings);
    }

    private void BindHotKeys()
    {
        _hotkeys = new HotKeyManager(_log);
        _hotkeys.PauseRequested += TogglePauseFromHotKey;
        _hotkeys.ToolbarRequested += () => _session?.ShowLauncherAtCursor();
        _hotkeys.OcrRequested += () => _ocr?.Coordinator.Trigger();
        _hotkeys.ScreenshotRequested += () => _ocr?.Coordinator.TriggerScreenshot();
        _hotkeys.ScreenshotDelayRequested += () =>
            _ocr?.Coordinator.TriggerScreenshotDelayed(_settings?.ScreenshotDelayDefaultSeconds ?? 3);
        _lastHotKeyApplyResult = _hotkeys.Apply(_settings!);

        // toolbar 构造完成后才能注册依赖它的事件
        _tray!.OnPauseChanged += paused =>
        {
            if (paused) _toolbar!.DismissExternal("paused");
        };
    }

    private sealed class TextPipeline
    {
        public TextPipeline(
            ClipboardAccess clipboardAccess,
            ClipboardPaste clipboardPaste,
            ClipboardWriter clipboardWriter,
            PasteService pasteService)
        {
            ClipboardAccess = clipboardAccess;
            ClipboardPaste = clipboardPaste;
            ClipboardWriter = clipboardWriter;
            PasteService = pasteService;
        }

        public ClipboardAccess ClipboardAccess { get; }
        public ClipboardPaste ClipboardPaste { get; }
        public ClipboardWriter ClipboardWriter { get; }
        public PasteService PasteService { get; }
        public AiTextService? AiText { get; set; }
        public FloatingToolbarBubblePresenter? Bubble { get; set; }
    }

    private void SaveSettings()
    {
        if (_store is null || _settings is null) return;
        _store.SaveSettings(_settings);
    }

    private void OnIpcCommand(string command)
    {
        WpfApplication.Current?.Dispatcher.Invoke(() =>
        {
            switch (command)
            {
                case "show-settings":
                    ShowSettingsWindow(null);
                    break;
                default:
                    _log.Warn("unknown ipc command", ("cmd", command));
                    break;
            }
        });
    }

    private void ShowSettingsWindow(string? initialPage = null)
    {
        WpfApplication.Current?.Dispatcher.Invoke(() =>
        {
            if (_settingsWindow is { IsVisible: true })
            {
                if (!string.IsNullOrEmpty(initialPage)) _settingsWindow.NavigateTo(initialPage);
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow(
                _store!, _settings!, initialPage,
                historyStore: _history?.Conversations,
                usage: _history?.Usage,
                onOpenConversation: ReopenConversation,
                ocrProviders: _ocr?.Registry.All,
                hotKeyStatusProvider: () => _lastHotKeyApplyResult);
            _settingsWindow.Saved += () =>
            {
                ApplyRuntimeSettings(reloadActions: true);
                _settingsWindow?.RefreshHotKeyRegistrationStatus();
            };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        });
    }

    /// <summary>"对话历史"列表中双击某条 → 把消息重放进新 AiChatWindow。
    /// 不复活流式状态，只把消息以静态形式展示并允许继续追问</summary>
    private void ReopenConversation(string conversationId)
    {
        if (_history is null) return;
        var record = _history.Conversations.Load(conversationId);
        if (record is null) return;
        WpfApplication.Current?.Dispatcher.Invoke(() =>
        {
            try
            {
                var window = new ConversationReplayWindow(record);
                window.Show();
                window.Activate();
            }
            catch (Exception ex)
            {
                _log.Warn("reopen conversation failed", ("err", ex.Message), ("id", conversationId));
            }
        });
    }

    private void ApplyRuntimeSettings(bool reloadActions)
    {
        if (_settings is null) return;
        ConsoleLog.Instance.SetMinimumLevel(_settings.LogLevel);
        // 主题/字体先于浮窗外观应用：浮窗 DynamicResource 才能在第一次 ApplyAppearance 拿到最新画刷
        ThemeManager.Apply(_settings);
        _toolbar?.ApplyAppearance(_settings);
        if (reloadActions && _store is not null && _catalog is not null)
        {
            var cfg = _store.LoadActions();
            if (cfg is not null) _catalog.Load(cfg);
            else _catalog.Load(DefaultActionsFactory.Create());
        }
        _lastHotKeyApplyResult = _hotkeys?.Apply(_settings);
        ApplyStartupSetting();
    }

    private void OnSystemThemeChanged()
    {
        if (_settings is null) return;
        WpfApplication.Current?.Dispatcher.Invoke(() => ThemeManager.Apply(_settings));
    }

    private void ApplyStartupSetting()
    {
        if (_settings is null) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null) return;
            if (_settings.LaunchAtStartup)
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    key.SetValue("ClipAura", "\"" + exe + "\"");
                }
            }
            else
            {
                key.DeleteValue("ClipAura", throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            _log.Warn("startup setting apply failed", ("err", ex.Message));
        }
    }

    private void TogglePauseFromHotKey()
    {
        WpfApplication.Current?.Dispatcher.Invoke(() =>
        {
            if (_pause is null) return;
            var paused = _pause.Toggle();
            _tray?.SetPausedLabel(paused);
            if (paused) _toolbar?.DismissExternal("pause-hotkey");
            else _toolbar?.ShowInlineToast("已恢复 ✓");
        });
    }

    private void HandleTrayLeftClick()
    {
        WpfApplication.Current?.Dispatcher.Invoke(() =>
        {
            switch (_settings?.TrayClickAction ?? TrayClickAction.Menu)
            {
                case TrayClickAction.Ocr:
                    _ocr?.Coordinator.Trigger();
                    break;
                case TrayClickAction.Screenshot:
                    _ocr?.Coordinator.TriggerScreenshot();
                    break;
                // 剪贴板历史已从设置 UI 移除，但保留运行时分发以兼容老版本 settings.json
                // 中持久化的 ClipboardHistory 值；下次用户保存设置时 UI 会自动迁移到默认 Menu
                case TrayClickAction.ClipboardHistory:
                    OpenClipboardHistory();
                    break;
                case TrayClickAction.Launcher:
                    _session?.ShowLauncherAtCursor();
                    break;
                case TrayClickAction.Settings:
                    ShowSettingsWindow(null);
                    break;
                case TrayClickAction.TogglePause:
                    TogglePauseFromHotKey();
                    break;
                case TrayClickAction.None:
                    break;
                case TrayClickAction.Menu:
                default:
                    _tray?.ShowMenuAtCursor();
                    break;
            }
        });
    }

    private void OpenClipboardHistory()
    {
        // 托盘/快捷键唤起历史面板时没有选区上下文，但用户多半想把历史项粘回"刚才那个窗口"。
        // 传入实时取值器而非固定句柄：历史窗口可常驻，用户会切到目标窗口再切回面板，
        // 必须在点击"粘贴"那一刻才取最近外部前台窗口。WinEvent hook 用 SKIPOWNPROCESS 不记本进程，
        // 但 ForegroundWatcher.Snapshot() 走 OCR/选区流程时可能无过滤地记入本进程窗口，故再排除本进程，
        // 取第一个非本进程窗口即为用户想粘回的目标
        var self = Environment.ProcessId;
        _history?.Launcher.Open(null, () => ForegroundWatcher.RecentProcesses()
            .FirstOrDefault(w => w.Hwnd != 0 && w.ProcessId != self)?.Hwnd ?? 0);
    }

    public void Dispose()
    {
        if (_toolbar is not null)
        {
            _toolbar.SystemThemeChanged -= OnSystemThemeChanged;
        }
        _session?.Dispose();
        _hotkeys?.Dispose();
        _watcher?.Dispose();
        _tray?.Dispose();
        _history?.Dispose();
        _clipboardThread?.Dispose();
        // OCR Registry 负责释放所有 provider：RapidOcr 持三个 ONNX InferenceSession，
        // WeChat 还会调 stop_ocr 终止驻留的 WeChatOCR.exe 子进程；
        // 都集中在 Registry.Dispose 里串行处理，避免 process tear-down 阶段 native finalizer 乱序
        _ocr?.Dispose();
        _instance?.Dispose();
    }
}
