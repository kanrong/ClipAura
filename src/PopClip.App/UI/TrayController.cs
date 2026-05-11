using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using PopClip.App.Config;
using PopClip.App.Hosting;
using PopClip.Core.Logging;

namespace PopClip.App.UI;

/// <summary>托盘图标 + 菜单。MVP 提供：暂停 / 设置 / 退出</summary>
internal sealed class TrayController : IDisposable
{
    private readonly ILog _log;
    private readonly ConfigStore _store;
    private readonly AppSettings _settings;
    private readonly PauseState _pause;
    private TaskbarIcon? _icon;

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
        _icon = new TaskbarIcon
        {
            ToolTipText = "PopClip Win",
            Icon = BuildIcon(),
            ContextMenu = BuildMenu(),
            NoLeftClickDelay = true,
        };
    }

    private static Icon BuildIcon()
    {
        // MVP 阶段不引入额外图标资源，使用系统提示图标占位
        return SystemIcons.Application;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        var pauseItem = new MenuItem { Header = _pause.IsPaused ? "继续" : "暂停" };
        pauseItem.Click += (_, _) =>
        {
            var paused = _pause.Toggle();
            pauseItem.Header = paused ? "继续" : "暂停";
            OnPauseChanged?.Invoke(paused);
        };
        menu.Items.Add(pauseItem);
        menu.Items.Add(new Separator());

        var settingsItem = new MenuItem { Header = "设置..." };
        settingsItem.Click += (_, _) =>
        {
            var w = new SettingsWindow(_store, _settings);
            w.ShowDialog();
        };
        menu.Items.Add(settingsItem);

        menu.Items.Add(new Separator());
        var exitItem = new MenuItem { Header = "退出 PopClip" };
        exitItem.Click += (_, _) => OnExitRequested?.Invoke();
        menu.Items.Add(exitItem);
        return menu;
    }

    public void Dispose() => _icon?.Dispose();
}
