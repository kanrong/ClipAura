namespace PopClip.Uia.Clipboard;

/// <summary>缓存"最近一次剪贴板兜底采集到的选区"，供内置复制动作回写。
/// 解决终端 / Firefox 等 UIA 取不到文本的应用的复制难题：采集阶段那次 Ctrl+C 已让源应用
/// 把多格式数据写进剪贴板，这里把那一刻的快照（含 Firefox 的 CF_HTML）连同纯文本一起留存。
/// 用户点"复制"时直接回写这份快照——既保住富文本格式，又不必再合成一次 Ctrl+C
/// （二次 Ctrl+C 在终端会因选区已失效而被当成中断信号 ^C，干扰正在运行的命令）。
///
/// 只保留"最近一次"：一次选区会话从采集到点击复制之间不会插入新的兜底采集，时序上足够；
/// 取用时以纯文本比对防串场，万一已被新会话覆盖则比对失败，调用方回退纯文本兜底。</summary>
public sealed class CapturedSelectionCache
{
    private readonly object _gate = new();
    private string? _text;
    private ClipboardSnapshot? _snapshot;

    public void Store(string text, ClipboardSnapshot snapshot)
    {
        lock (_gate)
        {
            _text = text;
            _snapshot = snapshot;
        }
    }

    /// <summary>取回与 expectedText 一致的快照；不匹配或无缓存返回 null。
    /// 命中后不清除，允许用户对同一选区多次点"复制"都成功</summary>
    public ClipboardSnapshot? TryGet(string expectedText)
    {
        lock (_gate)
        {
            if (_snapshot is not null && string.Equals(_text, expectedText, StringComparison.Ordinal))
            {
                return _snapshot;
            }
            return null;
        }
    }
}
