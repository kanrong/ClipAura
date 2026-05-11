namespace PopClip.Core.Logging;

/// <summary>极简结构化日志接口，便于后续接入诊断面板</summary>
public interface ILog
{
    void Trace(string message, params (string Key, object? Value)[] fields);
    void Debug(string message, params (string Key, object? Value)[] fields);
    void Info(string message, params (string Key, object? Value)[] fields);
    void Warn(string message, params (string Key, object? Value)[] fields);
    void Error(string message, Exception? ex = null, params (string Key, object? Value)[] fields);
}

/// <summary>当未注入实现时退化为控制台输出，避免空引用</summary>
public sealed class ConsoleLog : ILog
{
    public static readonly ConsoleLog Instance = new();

    private static string Format(string level, string message, (string Key, object? Value)[] fields)
    {
        if (fields.Length == 0)
        {
            return $"[{DateTime.Now:HH:mm:ss.fff}][{level}] {message}";
        }
        var parts = string.Join(" ", fields.Select(f => $"{f.Key}={f.Value}"));
        return $"[{DateTime.Now:HH:mm:ss.fff}][{level}] {message} {parts}";
    }

    public void Trace(string message, params (string Key, object? Value)[] fields)
        => System.Diagnostics.Debug.WriteLine(Format("TRC", message, fields));

    public void Debug(string message, params (string Key, object? Value)[] fields)
        => System.Diagnostics.Debug.WriteLine(Format("DBG", message, fields));

    public void Info(string message, params (string Key, object? Value)[] fields)
        => Console.WriteLine(Format("INF", message, fields));

    public void Warn(string message, params (string Key, object? Value)[] fields)
        => Console.WriteLine(Format("WRN", message, fields));

    public void Error(string message, Exception? ex = null, params (string Key, object? Value)[] fields)
    {
        Console.Error.WriteLine(Format("ERR", message, fields));
        if (ex is not null) Console.Error.WriteLine(ex);
    }
}
