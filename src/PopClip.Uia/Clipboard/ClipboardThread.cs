using System.Windows.Threading;

namespace PopClip.Uia.Clipboard;

/// <summary>专用 STA + Dispatcher 线程，所有 System.Windows.Clipboard 调用都路由到此处，
/// 避免被在 MTA 工作线程上调用而抛 ThreadStateException，也避免阻塞 UI Dispatcher</summary>
public sealed class ClipboardThread : IDisposable
{
    private readonly Thread _thread;
    private Dispatcher? _dispatcher;
    private readonly ManualResetEventSlim _ready = new(false);

    public ClipboardThread()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "PopClip.ClipboardSta",
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public void Start()
    {
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("clipboard STA thread did not start within 5s");
        }
    }

    private void Run()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _ready.Set();
        Dispatcher.Run();
    }

    public T Invoke<T>(Func<T> func)
    {
        var d = _dispatcher ?? throw new InvalidOperationException("clipboard thread not started");
        return d.Invoke(func);
    }

    public void Invoke(Action action)
    {
        var d = _dispatcher ?? throw new InvalidOperationException("clipboard thread not started");
        d.Invoke(action);
    }

    public void Dispose()
    {
        _dispatcher?.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
