using PopClip.Actions.BuiltIn;
using PopClip.App.Config;
using PopClip.App.Services;
using PopClip.App.UI;
using PopClip.Core.Logging;
using PopClip.Core.Session;
using PopClip.Hooks;
using PopClip.Uia;
using PopClip.Uia.Clipboard;
using Microsoft.Win32;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Automation;
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
        _tray = new TrayController(_log, _pause);
        _tray.OnSettingsRequested += tag => ShowSettingsWindow(tag);
        _tray.OnDiagnosticsRequested += ShowDiagnostics;
        _tray.OnExitRequested += () => WpfApplication.Current.Shutdown();
        _tray.Show();

        _toolbar = new FloatingToolbar(_log);
        _toolbar.PrewarmLayout();

        var settingsProvider = new SettingsProvider(_settings);
        var aiTextService = new AiTextService(_log, _settings, _replacer, clipboardWriter);
        _actionHost = new ActionHost(_log, _replacer, urlLauncher, clipboardWriter, _toolbar, settingsProvider, aiTextService);

        _gate = new SuppressionGate(_log, _settings);

        _session = new SelectionSessionManager(
            _log, _watcher, _acquisition, _replacer, _catalog, _actionHost, _gate, _toolbar, _pause, _settings,
            clipboardAccess, clipboardPaste);

        ApplyRuntimeSettings(reloadActions: false);

        _watcher.GlobalKeyHandler = _toolbar.TryHandleGlobalKey;
        _hotkeys = new HotKeyManager(_log);
        _hotkeys.PauseRequested += TogglePauseFromHotKey;
        _hotkeys.ToolbarRequested += () => _session?.ShowLauncherAtCursor();
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

        _log.Info("PopClip started");
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

            _settingsWindow = new SettingsWindow(_store!, _settings!, initialPage);
            _settingsWindow.Saved += () => ApplyRuntimeSettings(reloadActions: true);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        });
    }

    private void ApplyRuntimeSettings(bool reloadActions)
    {
        if (_settings is null) return;
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

    private void ShowDiagnostics()
    {
        WpfApplication.Current?.Dispatcher.Invoke(() =>
        {
            var uia = "不可用";
            try
            {
                _ = AutomationElement.FocusedElement;
                uia = "可用";
            }
            catch (Exception ex)
            {
                uia = "不可用：" + ex.Message;
            }

            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            var admin = principal.IsInRole(WindowsBuiltInRole.Administrator) ? "管理员" : "普通用户";
            System.Windows.MessageBox.Show(
                $"UIA：{uia}\n进程权限：{admin}\n全局钩子：已注册低级鼠标/键盘监听",
                "ClipAura 诊断",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        });
    }

    public void Dispose()
    {
        _session?.Dispose();
        _hotkeys?.Dispose();
        _watcher?.Dispose();
        _tray?.Dispose();
        _clipboardThread?.Dispose();
        _instance?.Dispose();
    }
}
