using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Data.Sqlite;
using PopClip.Core.Logging;
using PopClip.Hooks;
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

/// <summary>剪贴板图片历史的元数据。PngBlob 仅在 ListImages 调用方明确需要时才回填，
/// 其他场景为 null（避免 ListBox 一次拿 N 张大图导致 UI 卡顿）。
/// 缩略图按需走 GetImageBlob() 单条拉</summary>
public sealed record ClipboardImageEntry(
    long Id,
    string PngHash,
    int Width,
    int Height,
    int Bytes,
    string? SourceProcess,
    string? OcrText,
    DateTime CreatedAtUtc,
    bool Pinned,
    byte[]? PngBlob);

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

    /// <summary>图片历史保留张数；图片占用大，不能像文本那样保留 50+。
    /// 钉选不计入这个上限</summary>
    private const int RetainImageEntries = 30;

    /// <summary>单张图片大小硬上限：> 10MB 拒绝入库，避免 GB 截图把库撑爆。
    /// 现实中 4K 屏全屏 PNG 一般 3~6 MB，10 MB 足够覆盖典型截图</summary>
    private const int MaxImageBytes = 10 * 1024 * 1024;

    private readonly HistoryDatabase _db;
    private readonly ClipboardAccess _clipboard;
    private readonly ILog _log;
    private ClipboardListenerWindow? _listener;
    private string _lastWrittenHash = "";
    private string _lastWrittenImageHash = "";

    public event Action<ClipboardEntry>? EntryAdded;
    public event Action<ClipboardImageEntry>? ImageEntryAdded;
    /// <summary>任何增删改 / 翻新都会触发，供历史窗在打开时实时刷新。</summary>
    public event Action? Mutated;

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

    /// <summary>"我们自己刚写入的剪贴板图片"的等价标记，避免 WM_CLIPBOARDUPDATE 回环</summary>
    public void NoteSelfWrittenImage(byte[] pngBytes)
    {
        if (pngBytes is null || pngBytes.Length == 0) return;
        _lastWrittenImageHash = HashBytes(pngBytes);
    }

    private void OnClipboardChanged()
    {
        // 切到 STA 线程读剪贴板，避免在 UI 线程上抛 ExternalException
        try
        {
            // 优先尝试图片：图片场景的剪贴板更新主要由"截图复制 / 应用复制图片"产生，
            // 文本场景则由文字复制产生。两种格式互斥，先尝试图片避免对图片走"GetText 返回空 → 直接 return"路径
            byte[]? imageBytes = null;
            try { imageBytes = _clipboard.GetImagePngBytes(); }
            catch { /* 旧 Office / 浏览器有时复制时格式残缺，直接走文本路径兜底 */ }
            if (imageBytes is not null && imageBytes.Length > 0)
            {
                if (imageBytes.Length > MaxImageBytes)
                {
                    _log.Info("clipboard image too large, skip", ("bytes", imageBytes.Length));
                    return;
                }
                var imgHash = HashBytes(imageBytes);
                if (imgHash == _lastWrittenImageHash)
                {
                    _lastWrittenImageHash = "";
                    return;
                }
                _ = AppendImageAsync(imageBytes, imgHash, CurrentSourceProcess());
                return;
            }

            var text = _clipboard.GetText();
            if (string.IsNullOrEmpty(text)) return;
            if (text.Length < MinEntryLength) return;
            if (text.Length > MaxEntryLength) text = text[..MaxEntryLength];

            var hash = HashText(text);
            if (hash == _lastWrittenHash)
            {
                _lastWrittenHash = "";
                return;
            }

            var entry = AppendInternal(text, hash, CurrentSourceProcess());
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

    private Task AppendImageAsync(byte[] pngBytes, string hash, string? sourceProc)
        => Task.Run(() => AppendImageInternal(pngBytes, hash, sourceProc));

    /// <summary>对外暴露的"图片入库"入口：钉图 / 截图自动保存等路径调用。
    /// 与监听路径不同的是这里 sourceProc 通常已知（前台进程名），便于日后做"按应用筛选"</summary>
    public ClipboardImageEntry? AppendImage(byte[] pngBytes, string? sourceProc)
    {
        if (pngBytes is null || pngBytes.Length == 0) return null;
        if (pngBytes.Length > MaxImageBytes)
        {
            _log.Info("AppendImage too large, skip", ("bytes", pngBytes.Length));
            return null;
        }
        var hash = HashBytes(pngBytes);
        return AppendImageInternal(pngBytes, hash, sourceProc);
    }

    /// <summary>把已经知道 png_hash 的图片落库；hash 重复时只翻新 created_at 不重复 BLOB。
    /// 同 AppendInternal 的"翻新"语义，避免库里同一张图重复占空间</summary>
    private ClipboardImageEntry? AppendImageInternal(byte[] pngBytes, string hash, string? sourceProc)
    {
        using var conn = _db.Open();
        if (conn is null) return null;
        try
        {
            int width = 0, height = 0;
            try
            {
                using var ms = new MemoryStream(pngBytes);
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    ms,
                    System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnDemand);
                if (decoder.Frames.Count > 0)
                {
                    width = decoder.Frames[0].PixelWidth;
                    height = decoder.Frames[0].PixelHeight;
                }
            }
            catch { /* 解 size 失败仍允许入库；图片 ListBox 会按 0×0 过滤掉异常项 */ }

            using (var update = conn.CreateCommand())
            {
                update.CommandText = @"
                    UPDATE clipboard_image_history
                    SET created_at = $now,
                        source_proc = CASE WHEN $proc IS NULL THEN source_proc ELSE $proc END
                    WHERE png_hash = $h";
                update.Parameters.AddWithValue("$h", hash);
                update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                update.Parameters.AddWithValue("$proc", (object?)sourceProc ?? DBNull.Value);
                if (update.ExecuteNonQuery() > 0)
                {
                    NotifyMutated();
                    return null;
                }
            }

            using var insert = conn.CreateCommand();
            insert.CommandText = @"
                INSERT INTO clipboard_image_history
                    (png_blob, png_hash, width, height, bytes, source_proc, ocr_text, created_at, pinned)
                VALUES ($blob, $hash, $w, $h, $sz, $proc, NULL, $now, 0);
                SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("$blob", pngBytes);
            insert.Parameters.AddWithValue("$hash", hash);
            insert.Parameters.AddWithValue("$w", width);
            insert.Parameters.AddWithValue("$h", height);
            insert.Parameters.AddWithValue("$sz", pngBytes.Length);
            insert.Parameters.AddWithValue("$proc", (object?)sourceProc ?? DBNull.Value);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var id = Convert.ToInt64(insert.ExecuteScalar() ?? 0L);

            using (var prune = conn.CreateCommand())
            {
                prune.CommandText = @"
                    DELETE FROM clipboard_image_history
                    WHERE pinned = 0
                      AND id NOT IN (
                        SELECT id FROM clipboard_image_history
                        WHERE pinned = 0
                        ORDER BY created_at DESC LIMIT $keep);";
                prune.Parameters.AddWithValue("$keep", RetainImageEntries);
                prune.ExecuteNonQuery();
            }

            var entry = new ClipboardImageEntry(
                id, hash, width, height, pngBytes.Length, sourceProc, null,
                DateTime.UtcNow, false, null);
            ImageEntryAdded?.Invoke(entry);
            NotifyMutated();
            return entry;
        }
        catch (Exception ex)
        {
            _log.Warn("clipboard image append failed", ("err", ex.Message));
            return null;
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
                update.CommandText = @"
                    UPDATE clipboard_history
                    SET created_at = $now,
                        source_proc = CASE WHEN $proc IS NULL THEN source_proc ELSE $proc END
                    WHERE text_hash = $h";
                update.Parameters.AddWithValue("$h", hash);
                update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                update.Parameters.AddWithValue("$proc", (object?)sourceProc ?? DBNull.Value);
                if (update.ExecuteNonQuery() > 0)
                {
                    NotifyMutated();
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

            var created = new ClipboardEntry(id, text, hash, sourceProc, DateTime.UtcNow, false);
            NotifyMutated();
            return created;
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
        var trimmed = query?.Trim();
        var hasQuery = !string.IsNullOrEmpty(trimmed);
        try
        {
            // 查询 >=3 字符走 FTS5 trigram MATCH：trigram 按 3 字符滑窗建索引，中英文都能子串匹配，
            // 历史量大时也快。<3 字符无法生成 trigram（如中文"内容"两字），退回 LIKE；
            // TryListByFts 内部异常时会清空结果并返回 false，同样落到下面的 LIKE 兜底
            if (hasQuery && trimmed!.Length >= 3 && _db.FtsAvailable
                && TryListByFts(conn, trimmed, limit, list))
            {
                return list;
            }

            using var cmd = conn.CreateCommand();
            // LIKE 子串匹配：短查询（<3 字符）或 FTS 不可用时的兜底路径。
            // ESCAPE 让查询里的 % _ \ 按字面匹配，避免用户搜这些字符时被当成通配符
            cmd.CommandText = hasQuery
                ? "SELECT id, text, text_hash, source_proc, created_at, pinned FROM clipboard_history WHERE text LIKE $q ESCAPE '\\' ORDER BY pinned DESC, created_at DESC LIMIT $limit"
                : "SELECT id, text, text_hash, source_proc, created_at, pinned FROM clipboard_history ORDER BY pinned DESC, created_at DESC LIMIT $limit";
            if (hasQuery) cmd.Parameters.AddWithValue("$q", "%" + EscapeLike(trimmed!) + "%");
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

    /// <summary>转义 LIKE 通配符，使查询里的 % _ \ 按字面参与匹配。
    /// 必须先转义反斜杠本身，否则会把后续转义补进的反斜杠二次转义。
    /// 调用方需配合 SQL 的 ESCAPE '\' 子句</summary>
    private static string EscapeLike(string s)
        => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>FTS5 trigram 搜索：把整段输入当 phrase（双引号包裹 + 转义内部双引号）匹配，
    /// JOIN 回 clipboard_history 取完整字段并按 pinned / created_at 排序。
    /// 仅在查询 >=3 字符时调用（trigram 的最小匹配单元是连续 3 字符）。
    /// 失败时清空 list 并返回 false，让调用方退回 LIKE。
    /// phrase 包裹是为了让 OR / AND / NEAR / * 等 FTS 语法字符按字面参与匹配，
    /// 而不被解释成查询操作符</summary>
    private bool TryListByFts(SqliteConnection conn, string query, int limit, List<ClipboardEntry> list)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT h.id, h.text, h.text_hash, h.source_proc, h.created_at, h.pinned
                FROM clipboard_history_fts f
                JOIN clipboard_history h ON h.id = f.rowid
                WHERE f.text MATCH $q
                ORDER BY h.pinned DESC, h.created_at DESC
                LIMIT $limit";
            cmd.Parameters.AddWithValue("$q", "\"" + query.Replace("\"", "\"\"") + "\"");
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
            return true;
        }
        catch (Exception ex)
        {
            _log.Debug("fts trigram query failed; fallback to LIKE", ("err", ex.Message));
            list.Clear();
            return false;
        }
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
            var ok = cmd.ExecuteNonQuery() > 0;
            if (ok) NotifyMutated();
            return ok;
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
            if (cmd.ExecuteNonQuery() > 0) NotifyMutated();
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
            NotifyMutated();
        }
        catch { /* ignore */ }
    }

    private static string HashText(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 12);
    }

    private static string HashBytes(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    public IReadOnlyList<ClipboardImageEntry> ListImages(int limit, bool includeBlob = false, string? query = null)
    {
        using var conn = _db.Open();
        if (conn is null) return Array.Empty<ClipboardImageEntry>();
        var list = new List<ClipboardImageEntry>();
        var trimmed = query?.Trim();
        var hasQuery = !string.IsNullOrEmpty(trimmed);
        try
        {
            using var cmd = conn.CreateCommand();
            var columns = includeBlob
                ? "id, png_hash, width, height, bytes, source_proc, ocr_text, created_at, pinned, png_blob"
                : "id, png_hash, width, height, bytes, source_proc, ocr_text, created_at, pinned";
            var where = hasQuery ? " WHERE ocr_text LIKE $q ESCAPE '\\'" : "";
            cmd.CommandText = $"SELECT {columns} FROM clipboard_image_history{where} ORDER BY pinned DESC, created_at DESC LIMIT $limit";
            if (hasQuery) cmd.Parameters.AddWithValue("$q", "%" + EscapeLike(trimmed!) + "%");
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                byte[]? blob = null;
                if (includeBlob && !reader.IsDBNull(9))
                {
                    using var stream = reader.GetStream(9);
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    blob = ms.ToArray();
                }
                list.Add(new ClipboardImageEntry(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7)).UtcDateTime,
                    reader.GetInt32(8) != 0,
                    blob));
            }
        }
        catch (Exception ex)
        {
            _log.Debug("clipboard image list failed", ("err", ex.Message));
        }
        return list;
    }

    /// <summary>按 id 取出单张图片的原始 PNG 字节。
    /// 用于"用户在历史窗点击图片项 → 复制到剪贴板"等需要 BLOB 的延迟路径，
    /// 避免 ListImages 一次性把所有 BLOB 都拉出来撑爆内存</summary>
    public byte[]? GetImageBlob(long id)
    {
        using var conn = _db.Open();
        if (conn is null) return null;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT png_blob FROM clipboard_image_history WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0)) return null;
            using var stream = reader.GetStream(0);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    public bool DeleteImage(long id)
    {
        using var conn = _db.Open();
        if (conn is null) return false;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM clipboard_image_history WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            var ok = cmd.ExecuteNonQuery() > 0;
            if (ok) NotifyMutated();
            return ok;
        }
        catch { return false; }
    }

    /// <summary>把已识别的 OCR 文本回写到 clipboard_image_history.ocr_text，
    /// 让历史窗"图片"标签页能展示截图内的文字摘要，且后续可以做"按 OCR 文本搜索图片"。
    /// 若库内没有该 hash（未入库的临时截图）静默 no-op</summary>
    public void SetImageOcrTextByHash(string pngHash, string ocrText)
    {
        if (string.IsNullOrEmpty(pngHash)) return;
        using var conn = _db.Open();
        if (conn is null) return;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE clipboard_image_history SET ocr_text = $t WHERE png_hash = $h";
            cmd.Parameters.AddWithValue("$t", (object?)ocrText ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$h", pngHash);
            if (cmd.ExecuteNonQuery() > 0) NotifyMutated();
        }
        catch (Exception ex)
        {
            _log.Debug("set image ocr text failed", ("err", ex.Message));
        }
    }

    /// <summary>把 PNG 字节计算成与入库一致的 hash 字符串，便于上层 OCR 完成后用同一份字节回写。
    /// 与 HashBytes 私有方法等价，但暴露为静态便于 Coordinator 直接调</summary>
    public static string HashImagePngBytes(byte[] pngBytes)
    {
        if (pngBytes is null || pngBytes.Length == 0) return "";
        var hash = SHA256.HashData(pngBytes);
        return Convert.ToHexString(hash);
    }

    public void TogglePinnedImage(long id)
    {
        using var conn = _db.Open();
        if (conn is null) return;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE clipboard_image_history SET pinned = 1 - pinned WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            if (cmd.ExecuteNonQuery() > 0) NotifyMutated();
        }
        catch { }
    }

    private void NotifyMutated()
    {
        try { Mutated?.Invoke(); }
        catch { /* 订阅方异常不能打断入库 */ }
    }

    /// <summary>复制发生时前台通常还是源应用。本进程窗口（历史窗 / 隐藏监听窗）记成无来源。</summary>
    private static string? CurrentSourceProcess()
    {
        try
        {
            var snap = ForegroundWatcher.Snapshot();
            if (snap.Hwnd == 0 || snap.ProcessId == Environment.ProcessId) return null;
            return string.IsNullOrWhiteSpace(snap.ProcessName) ? null : snap.ProcessName;
        }
        catch
        {
            return null;
        }
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
