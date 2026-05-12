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
        _machine = new SelectionStateMachine(log, c => _candidateChannel.Writer.TryWrite(c));
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
                    _toolbar.DismissExternal("foreground-changed");
                }
                else if (ev is MouseDownEvent md && _toolbar.IsShown)
                {
                    // 浮窗显示期间用户在浮窗外按下鼠标即关闭。命中浮窗内部留给 WPF 路由
                    if (!_toolbar.ContainsScreenPoint(md.X, md.Y))
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

        var foreground = ForegroundWatcher.Snapshot();
        _log.Info("foreground", ("proc", foreground.ProcessName), ("class", foreground.WindowClassName));

        if (_gate.ShouldSuppress(foreground, out var reason))
        {
            _log.Info("suppressed", ("reason", reason), ("proc", foreground.ProcessName));
            return;
        }

        var mouseRect = SelectionRect.FromPoint(candidate.X, candidate.Y);

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
            return new ToolbarItem(title, icon, new DelegateCommand(() => RunAction(v.Action, outcome.Context)));
        }).ToList();

        // UI 线程上更新 ItemsControl + 显示窗口
        await WpfApplication.Current.Dispatcher.InvokeAsync(() =>
        {
            _toolbar.ApplyDisplayMode(_settings.ToolbarDisplay);
            _toolbar.Items.Clear();
            foreach (var it in items) _toolbar.Items.Add(it);
            _toolbar.ShowAt(outcome.Context.Rect, outcome.Context.Foreground);
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
            _toolbar.ApplyDisplayMode(_settings.ToolbarDisplay);
            _toolbar.Items.Clear();
            _toolbar.Items.Add(item);
            _toolbar.ShowAt(mouseRect, foreground);
        });
    }

    private void RunAction(IAction action, SelectionContext context)
    {
        _toolbar.DismissExternal("action-invoked");
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await action.RunAsync(context, _actionHost, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("action run failed", ex, ("id", action.Id));
            }
        });
    }

    public void Dispose()
    {
        _cts?.Cancel();
    }
}
