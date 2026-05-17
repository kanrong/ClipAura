using PopClip.Actions.BuiltIn;
using PopClip.App.Config;
using PopClip.App.Ocr;
using PopClip.App.Ocr.Providers;
using PopClip.App.Services;
using PopClip.App.UI;
using PopClip.Core.Logging;
using PopClip.Core.Session;
using PopClip.Hooks;
using PopClip.Uia;
using PopClip.Uia.Clipboard;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WpfApplication = System.Windows.Application;

namespace PopClip.App.Hosting;

/// <summary>整个应用的对象装配点。手动 DI 即可，无需 IServiceCollection 的复杂度</summary>
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
    private HistoryDatabase? _historyDb;
    private SqliteConversationStore? _historyStore;
    private SqliteUsageRecorder? _usage;
    private ClipboardHistoryService? _clipHistory;
    private ClipboardHistoryLauncher? _clipHistoryLauncher;
    private OcrProviderRegistry? _ocrRegistry;
    private OcrCaptureCoordinator? _ocrCoordinator;

    public bool TryAcquireSingleInstance()
    {
        _instance = new SingleInstance(_log);
        return _instance.TryAcquire();
    }

    public void SignalRunningInstance() => SingleInstance.Signal("show-settings");

    public void Start()
    {
        _instance!.CommandReceived += OnIpcCommand;
        _instance.StartIpcServer();

        _store = new ConfigStore(_log);
        _settings = _store.LoadSettings();
        ConsoleLog.Instance.SetMinimumLevel(_settings.LogLevel);

        _clipboardThread = new ClipboardThread();
        _clipboardThread.Start();
        var clipboardAccess = new ClipboardAccess(_clipboardThread);

        _watcher = new InputWatcher(_log);

        var uiaAcquirer = new UiaTextAcquirer(_log);
        var clipboardFallback = new ClipboardFallback(_log, clipboardAccess);
        _acquisition = new TextAcquisitionService(_log, uiaAcquirer, clipboardFallback);

        var uiaReplacer = new UiaTextReplacer(_log);
        var clipboardPaste = new ClipboardPaste(_log, clipboardAccess);
        _replacer = new TextReplacerService(_log, uiaReplacer, clipboardPaste);

        // PasteService 必须在 ActionCatalog 之前构造：PasteAction.CanRun 通过它判定
        // "剪贴板是否有文本"，因此目录在装配每个内置动作时就需要这个能力
        var pasteService = new PasteService(_log, clipboardAccess, clipboardPaste);
        _catalog = new ActionCatalog(_log, pasteService);
        var cfg = _store.LoadActions();
        if (cfg is not null)
        {
            // 启动期做一次幂等 seed：把新版本新增的内置动作（智能识别 / AI 解释）追加到老用户 actions.json，
            // 默认 enabled=false 不打扰；SeededBuiltInIds 保证用户删除后下次不会复活
            if (_store.SeedMissingBuiltInActions(cfg, _settings))
            {
                _store.SaveActions(cfg);
                _store.SaveSettings(_settings);
            }
            _catalog.Load(cfg);
        }
        else
        {
            _catalog.LoadDefaults();
        }

        var urlLauncher = new UrlLauncher(_log);
        var clipboardWriter = new ClipboardWriter(_log, clipboardAccess);

        _pause = new PauseState();
        _tray = new TrayController(_log, _pause);
        _tray.OnSettingsRequested += tag => ShowSettingsWindow(tag);
        _tray.OnClipboardHistoryRequested += OpenClipboardHistory;
        _tray.OnExitRequested += () => WpfApplication.Current.Shutdown();
        _tray.Show();

        _toolbar = new FloatingToolbar(_log);
        _toolbar.PrewarmLayout();
        // 系统主题/强调色变化时，由浮窗 WndProc 转发到这里重新跑一次全局主题应用
        _toolbar.SystemThemeChanged += OnSystemThemeChanged;

        // SQLite-backed 历史 / 用量 / 剪贴板存储；初始化失败时退化为 no-op，不阻塞 AI 主流程
        _historyDb = new HistoryDatabase(_log);
        _historyDb.Initialize();
        _historyStore = new SqliteConversationStore(_historyDb, _log);
        _usage = new SqliteUsageRecorder(_historyDb, _log);
        _clipHistory = new ClipboardHistoryService(_historyDb, clipboardAccess, _log);
        clipboardWriter.AttachHistory(_clipHistory);
        _clipHistory.Start();

        var settingsProvider = new SettingsProvider(_settings);
        var aiTextService = new AiTextService(
            _log, _settings, _replacer, clipboardWriter, _toolbar,
            clipboardAccess: clipboardAccess,
            historyStore: _historyStore,
            usage: _usage);
        _clipHistoryLauncher = new ClipboardHistoryLauncher(_clipHistory, clipboardWriter, _replacer, clipboardPaste);
        var resultDialog = new SmartResultDialogPresenter();
        var bubblePresenter = new FloatingToolbarBubblePresenter(_log, _toolbar);
        _actionHost = new ActionHost(
            _log, _replacer, urlLauncher, clipboardWriter, _toolbar,
            settingsProvider, aiTextService, pasteService, _clipHistoryLauncher,
            resultDialog: resultDialog, bubble: bubblePresenter);

        _gate = new SuppressionGate(_log, _settings);

        _session = new SelectionSessionManager(
            _log, _watcher, _acquisition, _replacer, _catalog, _actionHost, _gate, _toolbar, _pause, _settings,
            clipboardAccess, clipboardPaste);

        ApplyRuntimeSettings(reloadActions: false);

        _watcher.GlobalKeyHandler = key =>
        {
            // 优先级：浮窗 → AI 气泡。浮窗能处理（如 ESC 关浮窗、数字键触发动作）时短路；
            // 浮窗未处理且气泡可见时，ESC 关闭气泡（VK_ESCAPE = 0x1B）
            if (_toolbar!.TryHandleGlobalKey(key)) return true;
            if (key.IsDown && key.VirtualKey == 0x1B) return AiBubbleWindow.TryHandleEscape();
            return false;
        };
        // OCR provider 注册：WeChat 编译进主程序（代码极少，依赖 dll 用户提供），
        // RapidOcr 走 plugin 目录动态加载（onnxruntime / SkiaSharp 共 ~25 MB 拆出主程序）。
        // 所有 provider 都遵循"按需 native 加载"：构造不做任何重活，第一次 PrewarmInBackground / RecognizeAsync 才碰底层
        var pluginRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins");
        var rapidProviders = OcrPluginLoader.LoadAll(_log, pluginRoot);
        var wechatProvider = new WeChatOcrProvider(_log);
        _ocrRegistry = new OcrProviderRegistry(_log,
            preferredIdReader: () => _settings?.OcrProviderId,
            providers: rapidProviders.Concat(new IOcrProvider[] { wechatProvider }));
        _ocrCoordinator = new OcrCaptureCoordinator(_log, _ocrRegistry, _session, clipboardWriter, clipboardAccess, _toolbar,
            _settings, aiTextService,
            bubble: bubblePresenter);
        // 暴露给"剪贴板启动器"用作 OCR 按钮的点击回调
        _session.OcrLauncher = () => _ocrCoordinator?.Trigger();
        _session.ClipboardImageOcrLauncher = anchor => _ocrCoordinator?.TriggerClipboardImage(anchor);

        _hotkeys = new HotKeyManager(_log);
        _hotkeys.PauseRequested += TogglePauseFromHotKey;
        _hotkeys.ToolbarRequested += () => _session?.ShowLauncherAtCursor();
        _hotkeys.OcrRequested += () => _ocrCoordinator?.Trigger();
        _hotkeys.Apply(_settings);

        // toolbar 构造完成后才能注册依赖它的事件
        _tray.OnPauseChanged += paused =>
        {
            if (paused) _toolbar.DismissExternal("paused");
        };

        _watcher.Start();
        _session.Start();
        if (!_settings.FirstRunCompleted)
        {
            WpfApplication.Current.Dispatcher.BeginInvoke(new Action(() => ShowSettingsWindow(null)));
        }

        _log.Info("ClipAura started");
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
                historyStore: _historyStore,
                usage: _usage,
                onOpenConversation: ReopenConversation,
                ocrProviders: _ocrRegistry?.All);
            _settingsWindow.Saved += () => ApplyRuntimeSettings(reloadActions: true);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        });
    }

    /// <summary>"对话历史"列表中双击某条 → 把消息重放进新 AiResultWindow。
    /// 不复活流式状态，只把消息以静态形式展示并允许继续追问</summary>
    private void ReopenConversation(string conversationId)
    {
        if (_historyStore is null) return;
        var record = _historyStore.Load(conversationId);
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
            else _catalog.LoadDefaults();
        }
        _hotkeys?.Apply(_settings);
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

    private void OpenClipboardHistory()
    {
        _clipHistoryLauncher?.Open(null);
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
        _clipHistory?.Dispose();
        _clipboardThread?.Dispose();
        // OCR Registry 负责释放所有 provider：RapidOcr 持三个 ONNX InferenceSession，
        // WeChat 还会调 stop_ocr 终止驻留的 WeChatOCR.exe 子进程；
        // 都集中在 Registry.Dispose 里串行处理，避免 process tear-down 阶段 native finalizer 乱序
        _ocrRegistry?.Dispose();
        _instance?.Dispose();
    }
}
