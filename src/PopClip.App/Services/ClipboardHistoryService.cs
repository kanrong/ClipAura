using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Data.Sqlite;
using PopClip.Core.Logging;
using PopClip.Hooks.Interop;
using PopClip.Uia.Clipboard;

namespace PopClip.App.Services;

public sealed record ClipboardEntry(
    long Id,
    string Text,
    string TextHash,
    string? SourceProcess,
    DateTime CreatedAtUtc,
    bool Pinned);

/// <summary>剪贴板历史：用一个隐藏窗口挂 WM_CLIPBOARDUPDATE，
/// 文本变化时按 hash 去重并落库；保留最近 N 条 + 钉选条目。
///
/// 为什么不轮询：轮询会跟应用自己的 SetText 互相干扰（应用复制结果又被自己抓回来）；
/// WM_CLIPBOARDUPDATE 配合 hash 去重能识别并忽略"刚刚我们自己写出去的内容"。
///
/// 容量：默认 50 条普通项 + 不限钉选项。
/// 安全：长度>32KB 截断；长度<2 直接丢弃（多半是误操作）</summary>
internal sealed class ClipboardHistoryService : IDisposable
{
    private const int RetainEntries = 50;
    private const int MaxEntryLength = 32 * 1024;
    private const int MinEntryLength = 2;

    private readonly HistoryDatabase _db;
    private readonly ClipboardAccess _clipboard;
    private readonly ILog _log;
    private ClipboardListenerWindow? _listener;
    private string _lastWrittenHash = "";

    public event Action<ClipboardEntry>? EntryAdded;

    public ClipboardHistoryService(HistoryDatabase db, ClipboardAccess clipboard, ILog log)
    {
        _db = db;
        _clipboard = clipboard;
        _log = log;
    }

    public void Start()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            try
            {
                _listener = new ClipboardListenerWindow(OnClipboardChanged);
                _listener.EnsureCreated();
                _log.Info("clipboard history listener started");
            }
            catch (Exception ex)
            {
                _log.Warn("clipboard listener start failed", ("err", ex.Message));
            }
        });
    }

    /// <summary>给"我们自己刚写入的剪贴板"打标记，避免下一次 WM_CLIPBOARDUPDATE 把它当作用户输入再次入库</summary>
    public void NoteSelfWritten(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _lastWrittenHash = HashText(text);
    }

    private void OnClipboardChanged()
    {
        // 切到 STA 线程读剪贴板，避免在 UI 线程上抛 ExternalException
        try
        {
            var text = _clipboard.GetText();
            if (string.IsNullOrEmpty(text)) return;
            if (text.Length < MinEntryLength) return;
            if (text.Length > MaxEntryLength) text = text[..MaxEntryLength];

            var hash = HashText(text);
            if (hash == _lastWrittenHash)
            {
                // 应用自己写出去的内容回环，不入库
                _lastWrittenHash = "";
                return;
            }

            var entry = AppendInternal(text, hash, sourceProc: null);
            if (entry is not null)
            {
                EntryAdded?.Invoke(entry);
            }
        }
        catch (Exception ex)
        {
            _log.Debug("clipboard change handle failed", ("err", ex.Message));
        }
    }

    private ClipboardEntry? AppendInternal(string text, string hash, string? sourceProc)
    {
        using var conn = _db.Open();
        if (conn is null) return null;
        try
        {
            // 已存在则把它的 created_at 更新为现在（"翻新")，不重复插入
            using (var update = conn.CreateCommand())
            {
                update.CommandText = "UPDATE clipboard_history SET created_at = $now WHERE text_hash = $h";
                update.Parameters.AddWithValue("$h", hash);
                update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                if (update.ExecuteNonQuery() > 0)
                {
                    return null;
                }
            }

            using var insert = conn.CreateCommand();
            insert.CommandText = @"
                INSERT INTO clipboard_history (text, text_hash, source_proc, created_at, pinned)
                VALUES ($text, $hash, $proc, $now, 0);
                SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("$text", text);
            insert.Parameters.AddWithValue("$hash", hash);
            insert.Parameters.AddWithValue("$proc", (object?)sourceProc ?? DBNull.Value);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var id = Convert.ToInt64(insert.ExecuteScalar() ?? 0L);

            // 修剪：只保留 RetainEntries 条非钉选 + 全部钉选
            using (var prune = conn.CreateCommand())
            {
                prune.CommandText = @"
                    DELETE FROM clipboard_history
                    WHERE pinned = 0
                      AND id NOT IN (
                        SELECT id FROM clipboard_history
                        WHERE pinned = 0
                        ORDER BY created_at DESC LIMIT $keep);";
                prune.Parameters.AddWithValue("$keep", RetainEntries);
                prune.ExecuteNonQuery();
            }

            return new ClipboardEntry(id, text, hash, sourceProc, DateTime.UtcNow, false);
        }
        catch (Exception ex)
        {
            _log.Warn("clipboard append failed", ("err", ex.Message));
            return null;
        }
    }

    public IReadOnlyList<ClipboardEntry> List(int limit, string? query = null)
    {
        using var conn = _db.Open();
        if (conn is null) return Array.Empty<ClipboardEntry>();
        var list = new List<ClipboardEntry>();
        try
        {
            using var cmd = conn.CreateCommand();
            var hasQuery = !string.IsNullOrWhiteSpace(query);
            cmd.CommandText = hasQuery
                ? "SELECT id, text, text_hash, source_proc, created_at, pinned FROM clipboard_history WHERE text LIKE $q ORDER BY pinned DESC, created_at DESC LIMIT $limit"
                : "SELECT id, text, text_hash, source_proc, created_at, pinned FROM clipboard_history ORDER BY pinned DESC, created_at DESC LIMIT $limit";
            if (hasQuery) cmd.Parameters.AddWithValue("$q", "%" + query!.Trim() + "%");
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new ClipboardEntry(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)).UtcDateTime,
                    reader.GetInt32(5) != 0));
            }
        }
        catch (Exception ex)
        {
            _log.Debug("clipboard list failed", ("err", ex.Message));
        }
        return list;
    }

    public bool Delete(long id)
    {
        using var conn = _db.Open();
        if (conn is null) return false;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM clipboard_history WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch { return false; }
    }

    public void TogglePinned(long id)
    {
        using var conn = _db.Open();
        if (conn is null) return;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE clipboard_history SET pinned = 1 - pinned WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        catch { /* ignore */ }
    }

    public void Clear()
    {
        using var conn = _db.Open();
        if (conn is null) return;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM clipboard_history WHERE pinned = 0";
            cmd.ExecuteNonQuery();
        }
        catch { /* ignore */ }
    }

    private static string HashText(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 12);
    }

    public void Dispose()
    {
        try { _listener?.Close(); } catch { /* ignore */ }
    }
}

/// <summary>不可见的 1x1 窗口，承载 WM_CLIPBOARDUPDATE。
/// 在 WPF 的 UI 线程上创建，HwndSource 自动给 Dispatcher 投递消息</summary>
internal sealed class ClipboardListenerWindow : Window
{
    private readonly Action _onChanged;
    private nint _hwnd;
    private HwndSource? _source;

    public ClipboardListenerWindow(Action onChanged)
    {
        _onChanged = onChanged;
        ShowInTaskbar = false;
        Width = 1;
        Height = 1;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Opacity = 0;
        Topmost = false;
        Left = -32000;
        Top = -32000;
        SourceInitialized += OnSourceInitialized;
    }

    public void EnsureCreated()
    {
        Show();
        Hide();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        _hwnd = helper.Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        NativeMethods.AddClipboardFormatListener(_hwnd);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            try { _onChanged(); }
            catch { /* ignore */ }
            handled = true;
        }
        return 0;
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            if (_hwnd != 0) NativeMethods.RemoveClipboardFormatListener(_hwnd);
            _source?.RemoveHook(WndProc);
        }
        catch { /* ignore */ }
        base.OnClosed(e);
    }
}
