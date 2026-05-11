using System.Collections.ObjectModel;
using System.Threading.Channels;
using System.Windows;
using PopClip.Actions.BuiltIn;
using PopClip.App.Services;
using PopClip.App.UI;
using PopClip.Core.Actions;
using PopClip.Core.Logging;
using PopClip.Core.Model;
using PopClip.Core.Session;
using PopClip.Hooks;
using PopClip.Uia;

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
        PauseState pause)
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
        if (_pause.IsPaused)
        {
            _log.Debug("candidate dropped: paused");
            return;
        }

        var foreground = ForegroundWatcher.Snapshot();
        if (_gate.ShouldSuppress(foreground, out var reason))
        {
            _log.Debug("suppressed", ("reason", reason), ("proc", foreground.ProcessName));
            return;
        }

        var mouseRect = SelectionRect.FromPoint(candidate.X, candidate.Y);
        var outcome = await Task.Run(() => _acquisition.Acquire(foreground, mouseRect)).ConfigureAwait(false);
        if (outcome is null) return;
        if (outcome.Context.IsEmpty) return;

        _replacer.SetCurrentElement(outcome.Element);

        var visible = _catalog.GetVisible(outcome.Context);
        if (visible.Count == 0) return;

        var items = visible.Select(v => new ToolbarItem(
            v.Descriptor.Title.Length > 0 ? v.Descriptor.Title : v.Action.Title,
            !string.IsNullOrEmpty(v.Descriptor.Icon) ? v.Descriptor.Icon : v.Action.IconKey,
            new DelegateCommand(() => RunAction(v.Action, outcome.Context)))).ToList();

        // UI 线程上更新 ItemsControl + 显示窗口
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _toolbar.Items.Clear();
            foreach (var it in items) _toolbar.Items.Add(it);
            _toolbar.ShowAt(outcome.Context.Rect, outcome.Context.Foreground);
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
