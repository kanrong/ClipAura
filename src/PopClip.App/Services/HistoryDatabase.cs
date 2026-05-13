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
            ";
            cmd.ExecuteNonQuery();
            IsAvailable = true;
            _log.Info("history db ready", ("path", ConfigPaths.HistoryDbFile));
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
}
