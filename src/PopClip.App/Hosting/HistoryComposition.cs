using PopClip.App.Services;
using PopClip.Core.Logging;
using PopClip.Uia.Clipboard;

namespace PopClip.App.Hosting;

/// <summary>历史 / 用量 / 剪贴板历史这一组服务。SQLite 初始化失败时内部降级为 no-op，不阻塞主流程。</summary>
internal sealed class HistoryComposition : IDisposable
{
    public HistoryDatabase Database { get; }
    public SqliteConversationStore Conversations { get; }
    public SqliteUsageRecorder Usage { get; }
    public ClipboardHistoryService ClipboardHistory { get; }
    public ClipboardHistoryLauncher Launcher { get; }

    private HistoryComposition(
        HistoryDatabase database,
        SqliteConversationStore conversations,
        SqliteUsageRecorder usage,
        ClipboardHistoryService clipboardHistory,
        ClipboardHistoryLauncher launcher)
    {
        Database = database;
        Conversations = conversations;
        Usage = usage;
        ClipboardHistory = clipboardHistory;
        Launcher = launcher;
    }

    public static HistoryComposition Create(
        ILog log,
        ClipboardAccess clipboardAccess,
        ClipboardWriter clipboardWriter,
        TextReplacerService replacer,
        ClipboardPaste clipboardPaste)
    {
        var database = new HistoryDatabase(log);
        database.Initialize();
        var conversations = new SqliteConversationStore(database, log);
        var usage = new SqliteUsageRecorder(database, log);
        var clipboardHistory = new ClipboardHistoryService(database, clipboardAccess, log);
        clipboardWriter.AttachHistory(clipboardHistory);
        clipboardHistory.Start();
        var launcher = new ClipboardHistoryLauncher(
            clipboardHistory, clipboardWriter, replacer, clipboardPaste, clipboardAccess);
        return new HistoryComposition(database, conversations, usage, clipboardHistory, launcher);
    }

    public void Dispose() => ClipboardHistory.Dispose();
}
