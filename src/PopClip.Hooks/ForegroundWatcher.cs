using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using PopClip.Core.Logging;
using PopClip.Core.Model;
using PopClip.Core.Session;
using PopClip.Hooks.Interop;

namespace PopClip.Hooks;

/// <summary>前台窗口监听：用 SetWinEventHook(EVENT_SYSTEM_FOREGROUND) 比轮询省电；
/// 同时提供同步的 Snapshot() 接口供 SessionManager 在文本获取时定格上下文</summary>
public sealed class ForegroundWatcher : IDisposable
{
    private const int RecentLimit = 50;
    private static readonly object RecentLock = new();
    private static readonly List<ForegroundWindowInfo> RecentWindows = new();

    private readonly ILog _log;
    private readonly Channel<InputEvent> _channel;
    private nint _hook;
    private NativeMethods.WinEventDelegate? _delegate;

    public ForegroundWatcher(ILog log, Channel<InputEvent> channel)
    {
        _log = log;
        _channel = channel;
    }

    public void Start()
    {
        _delegate = OnWinEvent;
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            0, _delegate, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        if (_hook == 0)
        {
            _log.Warn("SetWinEventHook EVENT_SYSTEM_FOREGROUND failed");
        }
    }

    private void OnWinEvent(nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == 0) return;
        Remember(BuildSnapshot(hwnd));
        _channel.Writer.TryWrite(new ForegroundChangedEvent(hwnd, DateTime.UtcNow));
    }

    public static ForegroundWindowInfo Snapshot()
    {
        var snapshot = BuildSnapshot(NativeMethods.GetForegroundWindow());
        if (snapshot.Hwnd != 0)
        {
            Remember(snapshot);
        }
        return snapshot;
    }

    public static IReadOnlyList<ForegroundWindowInfo> RecentProcesses()
    {
        lock (RecentLock)
        {
            return RecentWindows.ToList();
        }
    }

    private static ForegroundWindowInfo BuildSnapshot(nint hwnd)
    {
        if (hwnd == 0)
        {
            return new ForegroundWindowInfo(0, 0, "", "", "");
        }

        var tid = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        _ = tid;

        var clsBuf = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, clsBuf, clsBuf.Capacity);

        var titleBuf = new StringBuilder(512);
        NativeMethods.GetWindowText(hwnd, titleBuf, titleBuf.Capacity);

        var processName = GetProcessName((int)pid);
        return new ForegroundWindowInfo(hwnd, (int)pid, processName, clsBuf.ToString(), titleBuf.ToString());
    }

    private static void Remember(ForegroundWindowInfo info)
    {
        if (info.Hwnd == 0 || string.IsNullOrWhiteSpace(info.ProcessName)) return;
        lock (RecentLock)
        {
            var existing = RecentWindows.FindIndex(x =>
                string.Equals(x.ProcessName, info.ProcessName, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) RecentWindows.RemoveAt(existing);
            RecentWindows.Insert(0, info);
            if (RecentWindows.Count > RecentLimit)
            {
                RecentWindows.RemoveRange(RecentLimit, RecentWindows.Count - RecentLimit);
            }
        }
    }

    private static string GetProcessName(int pid)
    {
        // 进程名优先用 QueryFullProcessImageName，避免 Process 类引入 .NET 反射开销
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == 0)
        {
            try { return Path.GetFileName(Process.GetProcessById(pid).MainModule?.FileName ?? ""); }
            catch { return ""; }
        }
        try
        {
            var buf = new StringBuilder(1024);
            uint size = (uint)buf.Capacity;
            if (NativeMethods.QueryFullProcessImageName(handle, 0, buf, ref size))
            {
                return Path.GetFileName(buf.ToString());
            }
            return "";
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    public void Dispose()
    {
        if (_hook != 0)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = 0;
        }
    }
}
