using PopClip.Actions.BuiltIn;
using PopClip.App.Config;
using PopClip.App.Services;
using PopClip.App.UI;
using PopClip.Core.Logging;
using PopClip.Hooks;
using PopClip.Uia;
using PopClip.Uia.Clipboard;
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

        _catalog = new ActionCatalog(_log);
        var cfg = _store.LoadActions();
        if (cfg is not null) _catalog.Load(cfg);
        else _catalog.LoadDefaults();

        var urlLauncher = new UrlLauncher(_log);
        var clipboardWriter = new ClipboardWriter(_log, clipboardAccess);

        _pause = new PauseState();
        // TrayController 同时担任 INotificationSink，必须先构造并 Show（建好 NotifyIcon）
        // 才能在 ActionHost 中作为 Notifier 注入；否则 BalloonTip 调用时 _icon 仍为 null
        _tray = new TrayController(_log, _store, _settings, _pause);
        _tray.OnExitRequested += () => WpfApplication.Current.Shutdown();
        _tray.Show();

        var settingsProvider = new SettingsProvider(_settings);
        _actionHost = new ActionHost(_log, _replacer, urlLauncher, clipboardWriter, _tray, settingsProvider);

        _gate = new SuppressionGate(_log, _settings);
        _toolbar = new FloatingToolbar(_log);
        _toolbar.PrewarmLayout();

        _session = new SelectionSessionManager(
            _log, _watcher, _acquisition, _replacer, _catalog, _actionHost, _gate, _toolbar, _pause, _settings,
            clipboardAccess, clipboardPaste);

        // 启动时也把当前显示模式同步给浮窗，避免首屏使用默认模式
        _toolbar.ApplyDisplayMode(_settings.ToolbarDisplay);
        _toolbar.ApplyThemeMode(_settings.ToolbarTheme);

        // toolbar 构造完成后才能注册依赖它的事件
        _tray.OnPauseChanged += paused =>
        {
            if (paused) _toolbar.DismissExternal("paused");
        };

        _watcher.Start();
        _session.Start();

        _log.Info("PopClip started");
    }

    private void OnIpcCommand(string command)
    {
        WpfApplication.Current?.Dispatcher.Invoke(() =>
        {
            switch (command)
            {
                case "show-settings":
                    new SettingsWindow(_store!, _settings!).Show();
                    break;
                default:
                    _log.Warn("unknown ipc command", ("cmd", command));
                    break;
            }
        });
    }

    public void Dispose()
    {
        _session?.Dispose();
        _watcher?.Dispose();
        _tray?.Dispose();
        _clipboardThread?.Dispose();
        _instance?.Dispose();
    }
}
