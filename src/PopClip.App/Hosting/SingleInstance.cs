using System.IO;
using System.IO.Pipes;
using PopClip.Core.Logging;

namespace PopClip.App.Hosting;

/// <summary>Mutex 单实例 + 命名管道唤起。后启动的进程把"打开设置"等命令推给已有实例</summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Global\PopClip.Win.SingleInstance.B1F3";
    private const string PipeName = "PopClip.Win.IPC.B1F3";

    private readonly ILog _log;
    private Mutex? _mutex;
    private CancellationTokenSource? _serverCts;

    public event Action<string>? CommandReceived;

    public SingleInstance(ILog log) => _log = log;

    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
        if (!created)
        {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        return true;
    }

    public void StartIpcServer()
    {
        _serverCts = new CancellationTokenSource();
        _ = Task.Run(() => ServerLoopAsync(_serverCts.Token));
    }

    private async Task ServerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                var cmd = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(cmd))
                {
                    CommandReceived?.Invoke(cmd.Trim());
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.Warn("ipc server loop error", ("err", ex.Message));
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
    }

    public static void Signal(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(500);
            using var writer = new StreamWriter(client);
            writer.Write(command);
        }
        catch
        {
            // 对方进程可能已经退出，忽略
        }
    }

    public void Dispose()
    {
        _serverCts?.Cancel();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }
}
