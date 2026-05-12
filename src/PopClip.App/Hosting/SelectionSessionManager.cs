using System.Threading.Channels;
using PopClip.Actions.BuiltIn;
using PopClip.App.Config;
using PopClip.App.Services;
using PopClip.App.UI;
using PopClip.Core.Actions;
using PopClip.Core.Logging;
using PopClip.Core.Model;
using PopClip.Core.Session;
using PopClip.Hooks;
using PopClip.Hooks.Interop;
using PopClip.Uia;
using PopClip.Uia.Clipboard;
using WpfApplication = System.Windows.Application;

namespace PopClip.App.Hosting;

/// <summary>把 Hooks → 状态机 → 文本获取 → 工具栏弹出 全链路串起来的中枢</summary>
internal sealed class SelectionSessionManager : IDisposable
{
    private readonly ILog _log;
    private readonly InputWatcher _watcher;
    private readonly SelectionStateMachine _machine;
    private readonly TextAcquisitionService _acquisition;
    private readonly TextReplacerService _replacer;
    private readonly ActionCatalog _catalog;
    private readonly IActionHost _actionHost;
    private readonly SuppressionGate _gate;
    private readonly FloatingToolbar _toolbar;
    private readonly PauseState _pause;
    private readonly AppSettings _settings;
    private readonly ClipboardAccess _clipboard;
    private readonly ClipboardPaste _pasteInjector;
    private readonly Channel<SelectionCandidate> _candidateChannel;
    private CancellationTokenSource? _cts;

    // 剪贴板兜底指纹：仅对 AcquisitionSource.ClipboardFallback 生效。
    // UIA 路径拿到的是当前真实选区，即使重复也是用户主动选的；剪贴板兜底则可能在
    // 用户实际没有选中任何内容时被宿主程序回填旧文本（部分 IDE/浏览器 Ctrl+C 默认行为）。
    // 这种情况"用户视角没有选中任何内容，但浮窗却显示了选项"，对用户毫无意义 → 直接丢弃。
    // 不需要时间窗口：即使过了很久再次出现"同样的剪贴板兜底文本"，依然是用户视角无意义的弹窗
    private string? _lastClipboardFallbackText;
    private nint _lastClipboardFallbackHwnd;

    public SelectionSessionManager(
        ILog log,
        InputWatcher watcher,
        TextAcquisitionService acquisition,
        TextReplacerService replacer,
        ActionCatalog catalog,
        IActionHost actionHost,
        SuppressionGate gate,
        FloatingToolbar toolbar,
        PauseState pause,
        AppSettings settings,
        ClipboardAccess clipboard,
        ClipboardPaste pasteInjector)
    {
        _log = log;
        _watcher = watcher;
        _acquisition = acquisition;
        _replacer = replacer;
        _catalog = catalog;
        _actionHost = actionHost;
        _gate = gate;
        _toolbar = toolbar;
        _pause = pause;
        _settings = settings;
        _clipboard = clipboard;
        _pasteInjector = pasteInjector;
        _candidateChannel = Channel.CreateBounded<SelectionCandidate>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _machine = new SelectionStateMachine(log, c => _candidateChannel.Writer.TryWrite(c), CreateSelectionOptions);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => InputPumpAsync(ct));
        _ = Task.Run(() => CandidatePumpAsync(ct));
    }

    private async Task InputPumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var ev in _watcher.Events.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (ev is ForegroundChangedEvent)
                {
                    if (_settings.DismissOnForegroundChanged)
                    {
                        _toolbar.DismissExternal("foreground-changed");
                    }
                }
                else if (ev is MouseDownEvent md && _toolbar.IsShown)
                {
                    // 浮窗显示期间用户在浮窗外按下鼠标即关闭。命中浮窗内部留给 WPF 路由
                    if (_settings.DismissOnClickOutside && !_toolbar.ContainsScreenPoint(md.X, md.Y))
                    {
                        _toolbar.DismissExternal("click-outside");
                    }
                }
                _machine.Process(ev);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Error("input pump crashed", ex); }
    }

    private async Task CandidatePumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var cand in _candidateChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await ProcessCandidateAsync(cand).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Error("candidate processing failed", ex);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessCandidateAsync(SelectionCandidate candidate)
    {
        _log.Info("candidate", ("trigger", candidate.Trigger), ("x", candidate.X), ("y", candidate.Y));

        if (_pause.IsPaused)
        {
            _log.Info("candidate dropped: paused");
            return;
        }

        // 出现新候选即关闭旧浮窗：成功时新浮窗会立即替换，失败时也不会留下"过时"的旧工具栏
        if (_toolbar.IsShown && _settings.DismissOnNewSelection)
        {
            _toolbar.DismissExternal("new-selection");
        }

        var foreground = ForegroundWatcher.Snapshot();
        _log.Info("foreground", ("proc", foreground.ProcessName), ("class", foreground.WindowClassName));

        if (_gate.ShouldSuppress(foreground, out var reason))
        {
            _log.Info("suppressed", ("reason", reason), ("proc", foreground.ProcessName));
            return;
        }

        var mouseRect = ResolveAnchorRect(candidate);

        // Ctrl+Click：用户明确表达"想粘贴"，跳过文本采集，直接弹"粘贴"按钮
        if (candidate.Trigger == SelectionTrigger.MouseCtrlClick)
        {
            await ShowPasteOnlyAsync(foreground, mouseRect).ConfigureAwait(false);
            return;
        }

        var outcome = await Task.Run(() => _acquisition.Acquire(foreground, mouseRect)).ConfigureAwait(false);
        if (outcome is null)
        {
            _log.Info("acquisition failed: no text from UIA nor clipboard");
            return;
        }
        if (outcome.Context.IsEmpty)
        {
            _log.Info("acquisition empty text", ("source", outcome.Context.Source));
            return;
        }
        if (outcome.Context.Text.Length < _settings.MinTextLength || outcome.Context.Text.Length > _settings.MaxTextLength)
        {
            _log.Info("acquisition dropped by length",
                ("len", outcome.Context.Text.Length),
                ("min", _settings.MinTextLength),
                ("max", _settings.MaxTextLength));
            return;
        }

        // 仅对剪贴板兜底路径做指纹去重：UIA 路径来的是用户真实选区，不参与。
        // 同一前台窗口下、剪贴板兜底文本与上次完全相同时跳过——这种情况几乎一定是
        // 宿主程序在用户没有真实选区时被 Ctrl+C 回填了旧值，弹窗对用户毫无意义
        if (outcome.Context.Source == AcquisitionSource.ClipboardFallback)
        {
            if (_lastClipboardFallbackText is not null
                && _lastClipboardFallbackHwnd == foreground.Hwnd
                && string.Equals(_lastClipboardFallbackText, outcome.Context.Text, StringComparison.Ordinal))
            {
                _log.Info("candidate dropped: duplicate clipboard fallback text",
                    ("len", outcome.Context.Text.Length),
                    ("hwnd", foreground.Hwnd));
                return;
            }
            _lastClipboardFallbackText = outcome.Context.Text;
            _lastClipboardFallbackHwnd = foreground.Hwnd;
        }

        var preview = outcome.Context.Text.Length > 40
            ? outcome.Context.Text.Substring(0, 40) + "..."
            : outcome.Context.Text;
        _log.Info("acquired",
            ("source", outcome.Context.Source),
            ("len", outcome.Context.Text.Length),
            ("preview", preview));

        _replacer.SetCurrentElement(outcome.Element);

        var visible = _catalog.GetVisible(outcome.Context);
        _log.Info("visible actions", ("count", visible.Count));
        if (visible.Count == 0) return;

        var items = visible.Select(v =>
        {
            // 搜索按钮跟随设置中的引擎名动态显示，无需在 actions.json 里手动改 title
            var title = v.Action.Id == BuiltInActionIds.Search
                ? _settings.SearchEngineName
                : v.Descriptor.Title.Length > 0 ? v.Descriptor.Title : v.Action.Title;
            var icon = !string.IsNullOrEmpty(v.Descriptor.Icon) ? v.Descriptor.Icon : v.Action.IconKey;
            return new ToolbarItem(title, icon, new DelegateCommand(() => RunAction(v.Action, outcome.Context, title)));
        }).ToList();

        // UI 线程上更新 ItemsControl + 显示窗口
        await WpfApplication.Current.Dispatcher.InvokeAsync(() =>
        {
            _toolbar.ApplyAppearance(_settings);
            _toolbar.Items.Clear();
            foreach (var it in items) _toolbar.Items.Add(it);
            _toolbar.ShowAt(mouseRect, outcome.Context.Foreground);
        });
    }

    /// <summary>Ctrl+Click 触发的简化工具条：只暴露"粘贴"。
    /// 剪贴板为空时直接放弃，避免给用户一个永远不响应的按钮</summary>
    private async Task ShowPasteOnlyAsync(ForegroundWindowInfo foreground, SelectionRect mouseRect)
    {
        var clipboardText = _clipboard.GetText();
        if (string.IsNullOrEmpty(clipboardText))
        {
            _log.Info("shift-click ignored: clipboard empty");
            return;
        }

        var hwnd = foreground.Hwnd;
        var item = new ToolbarItem("粘贴", "Paste", new DelegateCommand(() =>
        {
            // 必须先关闭浮窗（释放焦点状态），再 SetForegroundWindow + Ctrl+V
            _toolbar.DismissExternal("paste-invoked");
            _ = Task.Run(() =>
            {
                try { _pasteInjector.PasteCurrent(hwnd); }
                catch (Exception ex) { _log.Error("paste failed", ex); }
            });
        }));

        await WpfApplication.Current.Dispatcher.InvokeAsync(() =>
        {
            _toolbar.ApplyAppearance(_settings);
            _toolbar.Items.Clear();
            _toolbar.Items.Add(item);
            _toolbar.ShowAt(mouseRect, foreground);
        });
    }

    public void ShowLauncherAtCursor()
    {
        if (_pause.IsPaused) return;
        var foreground = ForegroundWatcher.Snapshot();
        if (!NativeMethods.GetCursorPos(out var pt))
        {
            pt = new NativeMethods.POINT { X = 0, Y = 0 };
        }
        _ = ShowPasteOnlyAsync(foreground, SelectionRect.FromPoint(pt.X, pt.Y));
    }

    private SelectionRect ResolveAnchorRect(SelectionCandidate candidate)
    {
        if (candidate.X >= 0 && candidate.Y >= 0)
        {
            return SelectionRect.FromPoint(candidate.X, candidate.Y);
        }

        // 键盘触发没有鼠标 hook 坐标，显示位置改用当前 cursor 物理像素坐标。
        // 避免把 (-1, -1) 交给 MonitorFromPoint 后在多显示器环境贴到错误屏幕边角。
        if (NativeMethods.GetCursorPos(out var pt))
        {
            _log.Debug("candidate anchor resolved from cursor",
                ("trigger", candidate.Trigger),
                ("x", pt.X),
                ("y", pt.Y));
            return SelectionRect.FromPoint(pt.X, pt.Y);
        }

        _log.Warn("candidate anchor unavailable; falling back to origin",
            ("trigger", candidate.Trigger),
            ("x", candidate.X),
            ("y", candidate.Y));
        return SelectionRect.FromPoint(0, 0);
    }

    private SelectionStateOptions CreateSelectionOptions()
    {
        return new SelectionStateOptions
        {
            PopupMode = _settings.PopupMode,
            PopupDelayMs = _settings.PopupDelayMs,
            HoverDelayMs = _settings.HoverDelayMs,
            RequiredModifier = _settings.RequiredModifier,
            EnableSelectAllPopup = _settings.EnableSelectAllPopup,
        };
    }

    private void RunAction(IAction action, SelectionContext context, string title)
    {
        var toastBefore = _toolbar.LastToastAtUtc;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await action.RunAsync(context, _actionHost, cts.Token).ConfigureAwait(false);
                await WpfApplication.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_toolbar.LastToastAtUtc <= toastBefore)
                    {
                        var text = action.Id == BuiltInActionIds.Copy ? "已复制 ✓" : $"{title} ✓";
                        _toolbar.ShowInlineToast(text);
                    }
                });
                await Task.Delay(700).ConfigureAwait(false);
                if (_settings.DismissOnActionInvoked)
                {
                    _toolbar.DismissExternal("action-completed");
                }
            }
            catch (Exception ex)
            {
                _log.Error("action run failed", ex, ("id", action.Id));
                await WpfApplication.Current.Dispatcher.InvokeAsync(() =>
                {
                    _toolbar.ShowInlineToast("失败：" + ex.Message, isError: true, copyText: ex.ToString(), durationMs: 5000);
                });
            }
        });
    }

    public void Dispose()
    {
        _cts?.Cancel();
    }
}
