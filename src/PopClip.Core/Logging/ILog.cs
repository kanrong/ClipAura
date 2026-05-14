namespace PopClip.Core.Logging;

public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
}

/// <summary>极简结构化日志接口，便于后续接入诊断面板</summary>
public interface ILog
{
    void Trace(string message, params (string Key, object? Value)[] fields);
    void Debug(string message, params (string Key, object? Value)[] fields);
    void Info(string message, params (string Key, object? Value)[] fields);
    void Warn(string message, params (string Key, object? Value)[] fields);
    void Error(string message, Exception? ex = null, params (string Key, object? Value)[] fields);
}

/// <summary>默认实现：同时写控制台、Debug 通道、按天滚动的日志文件
/// (%LOCALAPPDATA%\ClipAura\logs\diag-yyyyMMdd.log)。
/// 任意启动方式（dotnet run / 双击 exe / 自启）均能事后排查问题</summary>
public sealed class ConsoleLog : ILog
{
    public static readonly ConsoleLog Instance = new();

    private readonly string _filePath;
    private readonly string _directoryPath;
    private readonly object _gate = new();
    private volatile LogLevel _minimumLevel = LogLevel.Debug;

    public string FilePath => _filePath;
    public string DirectoryPath => _directoryPath;
    public LogLevel MinimumLevel => _minimumLevel;

    public ConsoleLog()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClipAura", "logs");
            Directory.CreateDirectory(dir);
            _directoryPath = dir;
            _filePath = Path.Combine(dir, $"diag-{DateTime.Now:yyyyMMdd}.log");
        }
        catch
        {
            _directoryPath = "";
            _filePath = "";
        }
    }

    public void SetMinimumLevel(LogLevel level) => _minimumLevel = level;

    private void Write(string level, string message, (string Key, object? Value)[] fields, Exception? ex = null)
    {
        if (!ShouldWrite(level)) return;
        var fieldsText = fields.Length > 0
            ? " " + string.Join(" ", fields.Select(f => $"{f.Key}={f.Value}"))
            : "";
        var line = $"[{DateTime.Now:HH:mm:ss.fff}][{level}] {message}{fieldsText}";

        try { Console.WriteLine(line); } catch { }
        try { System.Diagnostics.Debug.WriteLine(line); } catch { }

        if (!string.IsNullOrEmpty(_filePath))
        {
            lock (_gate)
            {
                try
                {
                    File.AppendAllText(_filePath, line + Environment.NewLine);
                    if (ex is not null)
                    {
                        File.AppendAllText(_filePath, ex + Environment.NewLine);
                    }
                }
                catch { }
            }
        }
    }

    private bool ShouldWrite(string level)
    {
        var current = level switch
        {
            "TRC" => LogLevel.Trace,
            "DBG" => LogLevel.Debug,
            "INF" => LogLevel.Info,
            "WRN" => LogLevel.Warn,
            "ERR" => LogLevel.Error,
            _ => LogLevel.Info,
        };
        return current >= _minimumLevel;
    }

    public void Trace(string m, params (string Key, object? Value)[] f) => Write("TRC", m, f);
    public void Debug(string m, params (string Key, object? Value)[] f) => Write("DBG", m, f);
    public void Info(string m, params (string Key, object? Value)[] f) => Write("INF", m, f);
    public void Warn(string m, params (string Key, object? Value)[] f) => Write("WRN", m, f);
    public void Error(string m, Exception? ex = null, params (string Key, object? Value)[] f) => Write("ERR", m, f, ex);
}
