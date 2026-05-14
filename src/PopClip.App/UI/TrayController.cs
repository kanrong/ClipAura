using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using PopClip.App.Hosting;
using PopClip.Core.Actions;
using PopClip.Core.Logging;
using PopClip.Hooks.Interop;

namespace PopClip.App.UI;

/// <summary>托盘图标 + 右键菜单。
/// 底层使用 <see cref="Win32TrayIcon"/> 直接调用 Shell_NotifyIcon，避免 WinForms 依赖。
/// 菜单走原生 WPF <see cref="ContextMenu"/>，自动复用窗口主题样式。
///
/// 焦点处理：因为我们的进程平时无前台窗口，弹菜单前要把一个隐形宿主窗口
/// 设为前台，否则会出现"菜单点旁边不消失"的经典 Win32 行为（参见 MS KB135788）</summary>
internal sealed class TrayController : INotificationSink, IDisposable
{
    private const long SettingsOpenCooldownMs = 300;

    private readonly ILog _log;
    private readonly PauseState _pause;
    private readonly Win32TrayIcon _trayIcon;
    private ContextMenu? _menu;
    private MenuItem? _pauseItem;
    private ContextMenuHostWindow? _menuHost;
    private long _lastSettingsRequestTick;

    public event Action<bool>? OnPauseChanged;
    /// <summary>请求显示设置窗口。参数为初始页 tag，传 null 表示打开默认页</summary>
    public event Action<string?>? OnSettingsRequested;
    public event Action? OnExitRequested;
    public event Action? OnClipboardHistoryRequested;

    public TrayController(ILog log, PauseState pause)
    {
        _log = log;
        _pause = pause;
        _trayIcon = new Win32TrayIcon(log, "ClipAura");
    }

    public void Show()
    {
        _menuHost = new ContextMenuHostWindow();
        _menuHost.EnsureCreated();
        _menu = BuildMenu();

        _trayIcon.RightClicked += OnTrayRightClicked;
        _trayIcon.LeftClicked += OnTrayLeftClicked;
        _trayIcon.Install();
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        _pauseItem = CreateMenuItem(_pause.IsPaused ? "继续" : "暂停", _pause.IsPaused ? Wpf.Ui.Controls.SymbolRegular.Play24 : Wpf.Ui.Controls.SymbolRegular.Pause24);
        _pauseItem.Click += (_, _) =>
        {
            var paused = _pause.Toggle();
            ApplyPauseMenuState(paused);
            OnPauseChanged?.Invoke(paused);
        };
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Separator());

        // 设置 / 外观 / 动作 三项平铺常用入口，其他面板（进程过滤、搜索引擎、快捷键、AI、关于）从"设置"进入
        var settingsItem = CreateMenuItem("设置...", Wpf.Ui.Controls.SymbolRegular.Settings24);
        settingsItem.Click += (_, _) => OnSettingsRequested?.Invoke(null);
        menu.Items.Add(settingsItem);

        var appearanceItem = CreateMenuItem("外观...", Wpf.Ui.Controls.SymbolRegular.Color24);
        appearanceItem.Click += (_, _) => OnSettingsRequested?.Invoke("Appearance");
        menu.Items.Add(appearanceItem);

        var actionsItem = CreateMenuItem("动作...", Wpf.Ui.Controls.SymbolRegular.Apps24);
        actionsItem.Click += (_, _) => OnSettingsRequested?.Invoke("Actions");
        menu.Items.Add(actionsItem);

        var clipboardItem = CreateMenuItem("剪贴板历史...", Wpf.Ui.Controls.SymbolRegular.History24);
        clipboardItem.Click += (_, _) => OnClipboardHistoryRequested?.Invoke();
        menu.Items.Add(clipboardItem);

        menu.Items.Add(new Separator());

        var exitItem = CreateMenuItem("退出 ClipAura", Wpf.Ui.Controls.SymbolRegular.Dismiss24);
        exitItem.Click += (_, _) => OnExitRequested?.Invoke();
        menu.Items.Add(exitItem);

        // 菜单关闭后立刻把宿主窗口藏起来，避免遗留可激活元素
        menu.Closed += (_, _) => _menuHost?.HideQuiet();
        return menu;
    }

    private void OnTrayLeftClicked()
    {
        var now = Environment.TickCount64;
        if (now - _lastSettingsRequestTick < SettingsOpenCooldownMs) return;
        _lastSettingsRequestTick = now;
        OnSettingsRequested?.Invoke(null);
    }

    private void OnTrayRightClicked(int screenX, int screenY)
    {
        if (_menu is null || _menuHost is null) return;
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            _menuHost.ShowForActivation();
            _menu.PlacementTarget = _menuHost;
            _menu.Placement = PlacementMode.MousePoint;
            _menu.IsOpen = true;
        });
    }

    public void SetPausedLabel(bool paused)
    {
        ApplyPauseMenuState(paused);
    }

    private void ApplyPauseMenuState(bool paused)
    {
        if (_pauseItem is null) return;
        _pauseItem.Header = paused ? "继续" : "暂停";
        _pauseItem.Icon = CreateMenuIcon(paused ? Wpf.Ui.Controls.SymbolRegular.Play24 : Wpf.Ui.Controls.SymbolRegular.Pause24);
    }

    private static MenuItem CreateMenuItem(string header, Wpf.Ui.Controls.SymbolRegular symbol)
        => new() { Header = header, Icon = CreateMenuIcon(symbol) };

    private static Wpf.Ui.Controls.SymbolIcon CreateMenuIcon(Wpf.Ui.Controls.SymbolRegular symbol)
        => new()
        {
            Symbol = symbol,
            FontSize = 14,
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 2, 0),
        };

    void INotificationSink.Notify(string text)
    {
        try
        {
            _trayIcon.ShowBalloon("ClipAura", text);
        }
        catch (Exception ex)
        {
            _log.Warn("notify failed", ("err", ex.Message));
        }
    }

    public void Dispose()
    {
        _trayIcon.Dispose();
        _menuHost?.Close();
        _menuHost = null;
    }
}

/// <summary>不可见的 1x1 宿主窗口，作为托盘 ContextMenu 的 PlacementTarget + 前台焦点持有者。
/// AllowsTransparency + Opacity=0 + 屏幕外坐标，肉眼不可见</summary>
internal sealed class ContextMenuHostWindow : Window
{
    private nint _hwnd;

    public ContextMenuHostWindow()
    {
        ShowInTaskbar = false;
        Width = 1;
        Height = 1;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Opacity = 0;
        Topmost = true;
        Left = -32000;
        Top = -32000;
        SourceInitialized += (_, _) => _hwnd = new WindowInteropHelper(this).Handle;
    }

    public void EnsureCreated()
    {
        Show();
        Hide();
    }

    public void ShowForActivation()
    {
        Show();
        Activate();
        if (_hwnd != 0) NativeMethods.SetForegroundWindow(_hwnd);
    }

    public void HideQuiet() => Hide();
}
