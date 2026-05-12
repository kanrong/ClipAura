using System.Drawing;
using PopClip.App.Config;
using PopClip.App.Hosting;
using PopClip.Core.Actions;
using PopClip.Core.Logging;
using WinFormsApp = System.Windows.Forms.Application;
using WinFormsContextMenu = System.Windows.Forms.ContextMenuStrip;
using WinFormsMenuItem = System.Windows.Forms.ToolStripMenuItem;
using WinFormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using WinFormsSeparator = System.Windows.Forms.ToolStripSeparator;
using WinFormsToolTipIcon = System.Windows.Forms.ToolTipIcon;
using WpfApplication = System.Windows.Application;

namespace PopClip.App.UI;

/// <summary>托盘图标 + 菜单。
/// 直接使用 System.Windows.Forms.NotifyIcon 而非第三方 wrapper，避免 wrapper 在 .NET 8 WPF
/// 下偶发的"创建成功但不可见"问题——NotifyIcon 走的是几十年稳定的 Shell_NotifyIcon Win32 API</summary>
internal sealed class TrayController : INotificationSink, IDisposable
{
    private readonly ILog _log;
    private readonly ConfigStore _store;
    private readonly AppSettings _settings;
    private readonly PauseState _pause;
    private WinFormsNotifyIcon? _icon;
    private WinFormsContextMenu? _menu;
    private WinFormsMenuItem? _pauseItem;

    public event Action<bool>? OnPauseChanged;
    public event Action? OnExitRequested;

    public TrayController(ILog log, ConfigStore store, AppSettings settings, PauseState pause)
    {
        _log = log;
        _store = store;
        _settings = settings;
        _pause = pause;
    }

    public void Show()
    {
        // EnableVisualStyles 保证 ContextMenuStrip 在 WPF 进程内也使用现代主题字体/边距
        WinFormsApp.EnableVisualStyles();
        WinFormsApp.SetCompatibleTextRenderingDefault(false);

        _menu = BuildMenu();

        _icon = new WinFormsNotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "ClipAura",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _log.Info("tray icon created");
    }

    private WinFormsContextMenu BuildMenu()
    {
        var menu = new WinFormsContextMenu();

        _pauseItem = new WinFormsMenuItem(_pause.IsPaused ? "继续" : "暂停");
        _pauseItem.Click += (_, _) =>
        {
            var paused = _pause.Toggle();
            _pauseItem!.Text = paused ? "继续" : "暂停";
            OnPauseChanged?.Invoke(paused);
        };
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new WinFormsSeparator());

        var settingsItem = new WinFormsMenuItem("设置...");
        settingsItem.Click += (_, _) =>
        {
            // 设置窗口是 WPF Window，必须在 WPF Dispatcher 上构造与展示
            WpfApplication.Current?.Dispatcher.Invoke(() =>
            {
                var w = new SettingsWindow(_store, _settings);
                w.ShowDialog();
            });
        };
        menu.Items.Add(settingsItem);

        menu.Items.Add(new WinFormsSeparator());

        var exitItem = new WinFormsMenuItem("退出 ClipAura");
        exitItem.Click += (_, _) => OnExitRequested?.Invoke();
        menu.Items.Add(exitItem);

        return menu;
    }

    /// <summary>优先加载内置品牌图标；资源异常时回退到系统图标</summary>
    private static Icon LoadTrayIcon()
    {
        try
        {
            var resource = WpfApplication.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new Icon(stream);
            }
        }
        catch
        {
        }

        return SystemIcons.Application;
    }

    /// <summary>展示一次性提示。Windows 10+ 上 ShowBalloonTip 会被系统转为 toast 通知，
    /// 即便用户禁用了 toast 也至少能在通知中心看到记录</summary>
    void INotificationSink.Notify(string text)
    {
        if (_icon is null) return;
        try
        {
            _icon.BalloonTipTitle = "ClipAura";
            _icon.BalloonTipText = text;
            _icon.BalloonTipIcon = WinFormsToolTipIcon.Info;
            _icon.ShowBalloonTip(2500);
        }
        catch (Exception ex)
        {
            _log.Warn("notify failed", ("err", ex.Message));
        }
    }

    public void Dispose()
    {
        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }
        _menu?.Dispose();
    }
}
