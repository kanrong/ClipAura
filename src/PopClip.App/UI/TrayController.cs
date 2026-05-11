using System.Drawing;
using PopClip.App.Config;
using PopClip.App.Hosting;
using PopClip.Core.Logging;
using WinFormsApp = System.Windows.Forms.Application;
using WinFormsContextMenu = System.Windows.Forms.ContextMenuStrip;
using WinFormsMenuItem = System.Windows.Forms.ToolStripMenuItem;
using WinFormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using WinFormsSeparator = System.Windows.Forms.ToolStripSeparator;
using WpfApplication = System.Windows.Application;

namespace PopClip.App.UI;

/// <summary>托盘图标 + 菜单。
/// 直接使用 System.Windows.Forms.NotifyIcon 而非第三方 wrapper，避免 wrapper 在 .NET 8 WPF
/// 下偶发的"创建成功但不可见"问题——NotifyIcon 走的是几十年稳定的 Shell_NotifyIcon Win32 API</summary>
internal sealed class TrayController : IDisposable
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

    /// <summary>用 SystemIcons.Application 作为占位图标。
    /// 后续替换为品牌 ico 时只需放到 Resources 并改这里的加载路径</summary>
    private static Icon LoadTrayIcon()
    {
        try
        {
            return SystemIcons.Application;
        }
        catch
        {
            // 极少数环境拿不到系统图标时构造一个 16x16 单色占位
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.SteelBlue);
            }
            return Icon.FromHandle(bmp.GetHicon());
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
