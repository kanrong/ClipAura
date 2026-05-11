using PopClip.Core.Model;

namespace PopClip.Core.Session;

/// <summary>Manager 把"已完成文本获取的会话"通知给 UI 层。失败也通知，便于诊断面板</summary>
public interface ISessionSink
{
    void OnSessionShown(SelectionContext context);
    void OnSessionDismissed(string reason);
    void OnAcquisitionFailed(SelectionCandidate candidate, string reason);
}
