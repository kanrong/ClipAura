using PopClip.Core.Logging;

namespace PopClip.Core.Session;

/// <summary>选区状态机：把噪声很大的输入流压缩成有限的"出现了一次新选区"事件。
/// 鼠标用拖选距离判定，键盘用 Shift+方向键累积判定，二者都跟 300/150ms 防抖。</summary>
public sealed class SelectionStateMachine
{
    private const int MouseDragThresholdPx = 4;
    private const int DoubleClickDistancePx = 5;
    // 0 延迟：候选事件下一刻就投递。仍走异步是为了不在钩子线程里直接触发 UI 路径，
    // 而是回到工作线程消费，避免拉慢钩子返回触发系统 LowLevelHooksTimeout
    private static readonly TimeSpan MouseDebounce = TimeSpan.Zero;
    private static readonly TimeSpan KeyboardDebounce = TimeSpan.Zero;
    private static readonly TimeSpan DoubleClickWindow = TimeSpan.FromMilliseconds(500);

    private readonly ILog _log;
    private readonly Action<SelectionCandidate> _onCandidate;

    private int _mouseDownX, _mouseDownY;
    private bool _leftDown;
    private bool _movedFarEnough;
    private bool _downWithCtrl;
    private bool _downWithShift;

    // 双击识别状态：记录上一次 mouse-up 的时间与位置
    private DateTime _lastUpAtUtc = DateTime.MinValue;
    private int _lastUpX, _lastUpY;

    private CancellationTokenSource? _debounceCts;

    public SelectionStateMachine(ILog log, Action<SelectionCandidate> onCandidate)
    {
        _log = log;
        _onCandidate = onCandidate;
    }

    public void Process(InputEvent ev)
    {
        switch (ev)
        {
            case MouseDownEvent md:
                _leftDown = true;
                _mouseDownX = md.X;
                _mouseDownY = md.Y;
                _movedFarEnough = false;
                _downWithCtrl = md.Ctrl;
                _downWithShift = md.Shift;
                CancelPending();
                break;

            case MouseMoveEvent mm:
                if (_leftDown && !_movedFarEnough)
                {
                    var dx = mm.X - _mouseDownX;
                    var dy = mm.Y - _mouseDownY;
                    if (dx * dx + dy * dy > MouseDragThresholdPx * MouseDragThresholdPx)
                    {
                        _movedFarEnough = true;
                    }
                }
                break;

            case MouseUpEvent mu:
                _leftDown = false;
                // Ctrl+原地点击优先级最高：直接当作"粘贴意图"，不参与拖选/双击识别
                if (!_movedFarEnough && (_downWithCtrl || mu.Ctrl))
                {
                    SchedulePending(new SelectionCandidate(SelectionTrigger.MouseCtrlClick, mu.X, mu.Y, mu.TimestampUtc), MouseDebounce);
                    _lastUpAtUtc = DateTime.MinValue;
                }
                else if (_movedFarEnough)
                {
                    // Shift+拖动也归入此分支（扩展选区是用户的主要意图）
                    SchedulePending(new SelectionCandidate(SelectionTrigger.MouseDrag, mu.X, mu.Y, mu.TimestampUtc), MouseDebounce);
                    _lastUpAtUtc = DateTime.MinValue;
                }
                else if (_downWithShift || mu.Shift)
                {
                    // Shift+原地点击：编辑器中通常表示"从光标延伸到此位置"，按拖选语义走文本采集
                    SchedulePending(new SelectionCandidate(SelectionTrigger.MouseDrag, mu.X, mu.Y, mu.TimestampUtc), MouseDebounce);
                    _lastUpAtUtc = DateTime.MinValue;
                }
                else if (IsDoubleClick(mu))
                {
                    SchedulePending(new SelectionCandidate(SelectionTrigger.MouseDoubleClick, mu.X, mu.Y, mu.TimestampUtc), MouseDebounce);
                    _lastUpAtUtc = DateTime.MinValue; // 防止三连击退化为二次双击
                }
                else
                {
                    _lastUpAtUtc = mu.TimestampUtc;
                    _lastUpX = mu.X;
                    _lastUpY = mu.Y;
                }
                _movedFarEnough = false;
                _downWithCtrl = false;
                _downWithShift = false;
                break;

            case KeyEvent k:
                HandleKey(k);
                break;

            case ForegroundChangedEvent:
                CancelPending();
                break;
        }
    }

    private void HandleKey(KeyEvent k)
    {
        const int VK_ESCAPE = 0x1B;
        if (k.IsDown && k.VirtualKey == VK_ESCAPE)
        {
            CancelPending();
            return;
        }

        if (k.IsDown && k.Shift && IsArrowOrNavKey(k.VirtualKey))
        {
            SchedulePending(
                new SelectionCandidate(SelectionTrigger.KeyboardSelection, -1, -1, k.TimestampUtc),
                KeyboardDebounce);
            return;
        }

        if (k.IsDown && k.Ctrl && k.VirtualKey == 0x41)
        {
            SchedulePending(
                new SelectionCandidate(SelectionTrigger.KeyboardSelection, -1, -1, k.TimestampUtc),
                KeyboardDebounce);
            return;
        }

        // 任何非 Shift 修饰的字符输入，认定选区被替换/取消
        if (k.IsDown && !k.Shift && !k.Ctrl && !k.Alt && IsTypingKey(k.VirtualKey))
        {
            CancelPending();
        }
    }

    private bool IsDoubleClick(MouseUpEvent mu)
    {
        if (_lastUpAtUtc == DateTime.MinValue) return false;
        if (mu.TimestampUtc - _lastUpAtUtc > DoubleClickWindow) return false;
        var dx = mu.X - _lastUpX;
        var dy = mu.Y - _lastUpY;
        return dx * dx + dy * dy <= DoubleClickDistancePx * DoubleClickDistancePx;
    }

    private static bool IsArrowOrNavKey(int vk) => vk switch
    {
        0x25 or 0x26 or 0x27 or 0x28 => true,  // 方向键
        0x21 or 0x22 or 0x23 or 0x24 => true,  // PgUp/PgDn/End/Home
        _ => false,
    };

    private static bool IsTypingKey(int vk)
    {
        if (vk >= 0x30 && vk <= 0x5A) return true;        // 0-9 A-Z
        if (vk >= 0x60 && vk <= 0x6F) return true;        // 数字小键盘 + 运算符
        return vk == 0x20 || vk == 0x0D || vk == 0x08;    // 空格/回车/退格
    }

    private void SchedulePending(SelectionCandidate cand, TimeSpan delay)
    {
        CancelPending();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                _onCandidate(cand);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.Error("debounce dispatch failed", ex);
            }
        });
    }

    private void CancelPending()
    {
        var cts = Interlocked.Exchange(ref _debounceCts, null);
        cts?.Cancel();
        cts?.Dispose();
    }
}
