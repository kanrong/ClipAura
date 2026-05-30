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

    /// <summary>FTS5 trigram 是否可用；不可用（老 SQLite 不带 trigram）时搜索全程退回 LIKE</summary>
    public bool FtsAvailable { get; private set; }

    public HistoryDatabase(ILog log)
    {
        _log = log;
        var dbPath = ConfigPaths.HistoryDbFile;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            // 刻意不使用 SqliteCacheMode.Shared：本库有多个连接跨 UI 线程与后台线程并发访问
            // （剪贴板文本/图片入库、历史查询、对话与用量记录），共享缓存在这种并发下曾进入
            // "database disk image is malformed" 的不一致状态——磁盘文件其实完好、仅缓存损坏，
            // 故重启清空连接池后即恢复。改回每连接私有缓存 + WAL 提供读写并发，
            // 这也是 Microsoft.Data.Sqlite 的推荐用法
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

            EnsureClipboardFtsTrigram(conn);

            IsAvailable = true;
            _log.Info("history db ready", ("path", ConfigPaths.HistoryDbFile), ("fts5_trigram", FtsAvailable));
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
        // WAL：读写不互锁，适合 UI 偶尔查询 + 后台串行写。
        // busy_timeout：多连接并发写时，让后到的写等待而非立刻抛 SQLITE_BUSY(database is locked)
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=3000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    /// <summary>建立 trigram 分词的剪贴板 FTS5 虚表 + 同步触发器，并 backfill 现有数据。
    /// 选 trigram 而非 unicode61：unicode61 把连续中文当成单个 token，搜"内容"命中不了
    /// "很多内容"；trigram 按 3 字符滑窗切分，中英文都能子串匹配（代价是查询串需 >=3 字符，
    /// 更短的由调用方退回 LIKE）。若检测到现存 FTS 表不是 trigram（早期 unicode61 版本），
    /// 先拆掉再以 trigram 重建。失败只记日志并置 FtsAvailable=false，搜索退回 LIKE，不阻塞主流程</summary>
    private void EnsureClipboardFtsTrigram(SqliteConnection conn)
    {
        try
        {
            // 现存 FTS 表若非 trigram（建表 SQL 里不含 trigram 关键字），先拆掉以便重建
            string? existing;
            using (var probe = conn.CreateCommand())
            {
                probe.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='clipboard_history_fts'";
                existing = probe.ExecuteScalar() as string;
            }
            if (existing is not null && existing.IndexOf("trigram", StringComparison.OrdinalIgnoreCase) < 0)
            {
                DropClipboardFtsArtifacts(conn);
            }

            using var ft = conn.CreateCommand();
            ft.CommandText = @"
                CREATE VIRTUAL TABLE IF NOT EXISTS clipboard_history_fts USING fts5(
                    text,
                    content='clipboard_history',
                    content_rowid='id',
                    tokenize='trigram');

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

            // 现有历史一次性回填到 fts；EXCEPT 子查询保证重启时不重复插入已同步的行
            using var seed = conn.CreateCommand();
            seed.CommandText = @"
                INSERT INTO clipboard_history_fts(rowid, text)
                SELECT id, text FROM clipboard_history
                WHERE id NOT IN (SELECT rowid FROM clipboard_history_fts);";
            seed.ExecuteNonQuery();

            FtsAvailable = true;
        }
        catch (Exception ex)
        {
            FtsAvailable = false;
            _log.Warn("fts trigram init failed; search falls back to LIKE", ("err", ex.Message));
        }
    }

    /// <summary>删除剪贴板 FTS 虚表与同步触发器。用于重建：检测到现存 FTS 表不是期望的
    /// trigram 分词时，先拆掉再重建。DROP 只动 FTS 对象，不触碰 clipboard_history 主表数据</summary>
    private void DropClipboardFtsArtifacts(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            DROP TRIGGER IF EXISTS clipboard_history_ai;
            DROP TRIGGER IF EXISTS clipboard_history_ad;
            DROP TRIGGER IF EXISTS clipboard_history_au;
            DROP TABLE IF EXISTS clipboard_history_fts;";
        cmd.ExecuteNonQuery();
    }
}
