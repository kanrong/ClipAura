using System.Runtime.InteropServices;
using PopClip.Core.Logging;
using PopClip.Hooks.Interop;

namespace PopClip.Hooks;

/// <summary>专门跑钩子的后台线程。低级钩子要求注册线程有消息泵，因此这里跑 GetMessage 循环；
/// 同时把钩子回调与 UI 线程隔离，避免 UI 卡顿把钩子从链上摘除。</summary>
public sealed class HookThread : IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);

    private readonly ILog _log;
    private Thread? _thread;
    private uint _threadId;
    private readonly ManualResetEventSlim _ready = new(false);
    private Exception? _startupError;

    private readonly List<Func<nint>> _installers = new();
    private readonly List<nint> _hookHandles = new();

    public HookThread(ILog log)
    {
        _log = log;
    }

    public void RegisterInstaller(Func<nint> installer) => _installers.Add(installer);

    /// <summary>启动线程并等待所有钩子安装完成。
    /// 超时或安装阶段抛错时把异常传播给调用方，避免主线程永久阻塞</summary>
    public void Start()
    {
        if (_thread is not null) return;
        _ready.Reset();
        _startupError = null;
        _thread = new Thread(ThreadProc)
        {
            IsBackground = true,
            Name = "PopClip.HookThread",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(StartupTimeout))
        {
            throw new TimeoutException($"hook thread did not signal ready within {StartupTimeout.TotalSeconds:0}s");
        }
        if (_startupError is not null)
        {
            throw new InvalidOperationException("hook installation failed", _startupError);
        }
    }

    private void ThreadProc()
    {
        try
        {
            _threadId = NativeMethods.GetCurrentThreadId();

            foreach (var installer in _installers)
            {
                var handle = installer();
                if (handle != 0)
                {
                    _hookHandles.Add(handle);
                }
                else
                {
                    var err = Marshal.GetLastWin32Error();
                    var ex = new System.ComponentModel.Win32Exception(err);
                    _startupError = ex;
                    _log.Error("hook install returned 0", ex, ("error", err));
                    _ready.Set();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _log.Error("hook installer threw", ex);
            _ready.Set();
            return;
        }

        // 安装成功后再 Set ready，让 Start 能立即解除阻塞
        _ready.Set();

        try
        {
            while (NativeMethods.GetMessage(out var msg, 0, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            _log.Error("hook pump crashed", ex);
        }
        finally
        {
            foreach (var h in _hookHandles)
            {
                NativeMethods.UnhookWindowsHookEx(h);
            }
            _hookHandles.Clear();
        }
    }

    public void Stop()
    {
        if (_thread is null) return;
        NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public void Dispose() => Stop();
}
