using System.IO;
using Microsoft.Data.Sqlite;
using PopClip.App.Config;
using PopClip.Core.Logging;

namespace PopClip.App.Services;

/// <summary>SQLite 数据库的集中管理点。
/// 对话历史 / 用量看板 / 剪贴板历史共用同一 history.db 文件，避免多个 .db 散落。
/// 所有写入走单线程串行：每个 GetConnection() 都返回一个独立的 open 连接，
/// 由调用方在 using 内完成事务；SQLite 内部用文件锁保证可见性。
///
/// 启动时调用 Initialize 一次完成所有 schema 迁移。
/// 失败时降级为"内存模式" —— Stores 仍能调用但操作变成 no-op，保证 AI 主流程不受历史落库影响</summary>
internal sealed class HistoryDatabase
{
    private readonly ILog _log;
    private readonly string _connectionString;
    public bool IsAvailable { get; private set; }

    /// <summary>SQLite 编译版本是否启用了 FTS5；不可用时调用方应退到 LIKE 路径。
    /// Microsoft.Data.Sqlite 默认包含的 SQLitePCLRaw.bundle_e_sqlite3 是带 FTS5 编译的，
    /// 但用户自定义部署或精简版本可能缺，因此运行期探测一次更稳妥</summary>
    public bool FtsAvailable { get; private set; }

    public HistoryDatabase(ILog log)
    {
        _log = log;
        var dbPath = ConfigPaths.HistoryDbFile;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    public void Initialize()
    {
        try
        {
            using var conn = OpenInternal();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS conversations (
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    reference_text TEXT NOT NULL,
                    model TEXT NOT NULL,
                    provider TEXT NOT NULL,
                    messages_json TEXT NOT NULL,
                    prompt_tokens INTEGER NOT NULL,
                    completion_tokens INTEGER NOT NULL,
                    created_at INTEGER NOT NULL,
                    message_count INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_conv_created ON conversations(created_at DESC);

                CREATE TABLE IF NOT EXISTS usage_daily (
                    day TEXT NOT NULL,
                    provider TEXT NOT NULL,
                    model TEXT NOT NULL,
                    calls INTEGER NOT NULL,
                    prompt_tokens INTEGER NOT NULL,
                    completion_tokens INTEGER NOT NULL,
                    elapsed_ms INTEGER NOT NULL,
                    PRIMARY KEY (day, provider, model)
                );

                CREATE TABLE IF NOT EXISTS clipboard_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    text TEXT NOT NULL,
                    text_hash TEXT NOT NULL,
                    source_proc TEXT,
                    created_at INTEGER NOT NULL,
                    pinned INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_clip_hash ON clipboard_history(text_hash);
                CREATE INDEX IF NOT EXISTS idx_clip_created ON clipboard_history(created_at DESC);

                CREATE TABLE IF NOT EXISTS clipboard_image_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    png_blob BLOB NOT NULL,
                    png_hash TEXT NOT NULL UNIQUE,
                    width INTEGER, height INTEGER, bytes INTEGER,
                    source_proc TEXT,
                    ocr_text TEXT,
                    created_at INTEGER NOT NULL,
                    pinned INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_clip_img_created ON clipboard_image_history(created_at DESC);
                CREATE INDEX IF NOT EXISTS idx_clip_img_hash ON clipboard_image_history(png_hash);
            ";
            cmd.ExecuteNonQuery();

            TryEnableFts5(conn);

            IsAvailable = true;
            FtsAvailable = ProbeFtsAvailable(conn);
            _log.Info("history db ready", ("path", ConfigPaths.HistoryDbFile), ("fts5", FtsAvailable));
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            _log.Warn("history db init failed; running without persistence", ("err", ex.Message));
        }
    }

    public SqliteConnection? Open()
    {
        if (!IsAvailable) return null;
        try { return OpenInternal(); }
        catch (Exception ex)
        {
            _log.Warn("history db open failed", ("err", ex.Message));
            return null;
        }
    }

    private SqliteConnection OpenInternal()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        // WAL：读写不互锁，适合 UI 偶尔查询 + 后台串行写
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    /// <summary>探测当前 SQLite 是否带 FTS5 编译。失败一次后调用方走 LIKE 路径，
    /// 但表的 schema 仍存在（不影响下次升级或换 SQLite 后启用 FTS5）</summary>
    private static bool ProbeFtsAvailable(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='clipboard_history_fts'";
            var name = cmd.ExecuteScalar() as string;
            return !string.IsNullOrEmpty(name);
        }
        catch { return false; }
    }

    /// <summary>建立 FTS5 虚表 + 触发器，并把现有 clipboard_history 一次性 reindex。
    /// 使用 unicode61 分词器：默认按 Unicode codepoint 切分，对中英混合 90% 场景够用；
    /// CJK 字段被切到字符级，搜索时仍能命中（虽然没有"分词"语义，但用户搜的是包含字而非短语，
    /// codepoint 级匹配实际很贴近期望）。
    ///
    /// 失败时只写日志：本应用所有持久化能力都遵循"DB 故障不阻塞主流程"原则，
    /// FTS5 故障只是搜索路径退化为 LIKE，使用体验可接受</summary>
    private void TryEnableFts5(SqliteConnection conn)
    {
        try
        {
            using var ft = conn.CreateCommand();
            ft.CommandText = @"
                CREATE VIRTUAL TABLE IF NOT EXISTS clipboard_history_fts USING fts5(
                    text,
                    content='clipboard_history',
                    content_rowid='id',
                    tokenize='unicode61');

                -- 同步触发器：clipboard_history 增 / 删 / 改时把记录写到 fts 镜像
                CREATE TRIGGER IF NOT EXISTS clipboard_history_ai AFTER INSERT ON clipboard_history BEGIN
                    INSERT INTO clipboard_history_fts(rowid, text) VALUES (new.id, new.text);
                END;
                CREATE TRIGGER IF NOT EXISTS clipboard_history_ad AFTER DELETE ON clipboard_history BEGIN
                    INSERT INTO clipboard_history_fts(clipboard_history_fts, rowid, text)
                    VALUES('delete', old.id, old.text);
                END;
                CREATE TRIGGER IF NOT EXISTS clipboard_history_au AFTER UPDATE ON clipboard_history BEGIN
                    INSERT INTO clipboard_history_fts(clipboard_history_fts, rowid, text)
                    VALUES('delete', old.id, old.text);
                    INSERT INTO clipboard_history_fts(rowid, text) VALUES (new.id, new.text);
                END;";
            ft.ExecuteNonQuery();

            // 已有的历史数据一次性 backfill 到 fts。INSERT IGNORE 风格用 EXCEPT 避免重插：
            // 第一次建表时 fts 为空，全部塞入；下次启动时 fts 已与 clipboard_history 同步，
            // EXCEPT 子查询返回空集，实际写入 0 行
            using var seed = conn.CreateCommand();
            seed.CommandText = @"
                INSERT INTO clipboard_history_fts(rowid, text)
                SELECT id, text FROM clipboard_history
                WHERE id NOT IN (SELECT rowid FROM clipboard_history_fts);";
            seed.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log.Warn("fts5 init failed; falling back to LIKE", ("err", ex.Message));
        }
    }
}
