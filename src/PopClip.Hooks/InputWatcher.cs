using System.Threading.Channels;
using PopClip.Core.Logging;
using PopClip.Core.Session;

namespace PopClip.Hooks;

/// <summary>整合三类监听，提供单一异步事件流。
/// 通过 _lastMouseEventUtc 心跳触发 watchdog 重装，应对低级钩子被系统超时摘除</summary>
public sealed class InputWatcher : IDisposable
{
    private readonly ILog _log;
    private readonly Channel<InputEvent> _channel;
    private HookThread _thread;
    private readonly LowLevelMouseHook _mouseHook;
    private readonly LowLevelKeyboardHook _kbdHook;
    private readonly ForegroundWatcher _foreground;

    private CancellationTokenSource? _watchdogCts;

    public ChannelReader<InputEvent> Events { get; }

    public InputWatcher(ILog log)
    {
        _log = log;
        _channel = Channel.CreateBounded<InputEvent>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _mouseHook = new LowLevelMouseHook(log, _channel);
        _kbdHook = new LowLevelKeyboardHook(log, _channel);
        _foreground = new ForegroundWatcher(log, _channel);
        _thread = NewThread();
        Events = _channel.Reader;
    }

    private HookThread NewThread()
    {
        var t = new HookThread(_log);
        t.RegisterInstaller(_mouseHook.Install);
        t.RegisterInstaller(_kbdHook.Install);
        return t;
    }

    public void Start()
    {
        _thread.Start();
        _foreground.Start();
        StartWatchdog();
    }

    private void StartWatchdog()
    {
        _watchdogCts = new CancellationTokenSource();
        var ct = _watchdogCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                    var idle = DateTime.UtcNow - _mouseHook.LastEventUtc;
                    if (idle > TimeSpan.FromMinutes(2))
                    {
                        _log.Warn("hook watchdog: idle too long, reinstalling",
                            ("idleSec", (int)idle.TotalSeconds));
                        Reinstall();
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { _log.Error("watchdog tick failed", ex); }
            }
        }, ct);
    }

    private void Reinstall()
    {
        try
        {
            _thread.Stop();
            _thread = NewThread();
            _thread.Start();
            _mouseHook.ResetHeartbeat();
            _log.Info("hooks reinstalled");
        }
        catch (Exception ex)
        {
            _log.Error("hook reinstall failed", ex);
        }
    }

    public void Dispose()
    {
        _watchdogCts?.Cancel();
        _foreground.Dispose();
        _thread.Dispose();
        _channel.Writer.TryComplete();
    }
}
