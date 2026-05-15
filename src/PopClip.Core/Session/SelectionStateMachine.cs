using PopClip.Core.Logging;

namespace PopClip.Core.Session;

/// <summary>选区状态机：把噪声很大的输入流压缩成有限的"出现了一次新选区"事件。
/// 鼠标用拖选距离判定，键盘用 Shift+方向键累积判定，二者都跟 300/150ms 防抖。</summary>
public sealed class SelectionStateMachine
{
    private const int MouseDragThresholdPx = 4;
    private const int DoubleClickDistancePx = 5;
    private static readonly TimeSpan DoubleClickWindow = TimeSpan.FromMilliseconds(500);

    private readonly ILog _log;
    private readonly Action<SelectionCandidate> _onCandidate;
    private readonly Func<SelectionStateOptions> _optionsProvider;

    private int _mouseDownX, _mouseDownY;
    private bool _leftDown;
    private bool _movedFarEnough;
    private bool _downWithCtrl;
    private bool _downWithShift;
    private bool _downWithAlt;

    // 双击识别状态：记录上一次 mouse-up 的时间与位置
    private DateTime _lastUpAtUtc = DateTime.MinValue;
    private int _lastUpX, _lastUpY;

    private CancellationTokenSource? _debounceCts;

    public SelectionStateMachine(
        ILog log,
        Action<SelectionCandidate> onCandidate,
        Func<SelectionStateOptions>? optionsProvider = null)
    {
        _log = log;
        _onCandidate = onCandidate;
        _optionsProvider = optionsProvider ?? (() => new SelectionStateOptions());
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
                _downWithAlt = md.Alt;
                CancelPending();
                break;

            case MouseMoveEvent mm:
                if (!_leftDown && GetOptions().PopupMode == SelectionPopupMode.HoverStill)
                {
                    CancelPending();
                }
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
                // 配置的修饰键 + 原地点击优先级最高：直接当作"剪贴板操作意图"，不参与双击/选区识别
                if (!_movedFarEnough && MatchesModifier(GetOptions().QuickClickModifier,
                        _downWithShift || mu.Shift,
                        _downWithCtrl || mu.Ctrl,
                        _downWithAlt || mu.Alt))
                {
                    SchedulePending(new SelectionCandidate(SelectionTrigger.MouseModifierClick, mu.X, mu.Y, mu.TimestampUtc), GetDelay(mu));
                    _lastUpAtUtc = DateTime.MinValue;
                }
                else if (_movedFarEnough)
                {
                    // Shift+拖动也归入此分支（扩展选区是用户的主要意图）。
                    // 仅此分支传递 IsLikelyWindowDrag：只有"鼠标移动超过阈值"的拖动才可能是拖窗体，
                    // 其他分支（修饰键点击、Shift 原地点击、双击）鼠标几乎没动，必然不是窗体拖动
                    TryScheduleSelection(new SelectionCandidate(SelectionTrigger.MouseDrag, mu.X, mu.Y, mu.TimestampUtc, mu.IsLikelyWindowDrag), mu);
                    _lastUpAtUtc = DateTime.MinValue;
                }
                else if (_downWithShift || mu.Shift)
                {
                    // Shift+原地点击：编辑器中通常表示"从光标延伸到此位置"，按拖选语义走文本采集
                    TryScheduleSelection(new SelectionCandidate(SelectionTrigger.MouseDrag, mu.X, mu.Y, mu.TimestampUtc), mu);
                    _lastUpAtUtc = DateTime.MinValue;
                }
                else if (IsDoubleClick(mu))
                {
                    TryScheduleSelection(new SelectionCandidate(SelectionTrigger.MouseDoubleClick, mu.X, mu.Y, mu.TimestampUtc), mu);
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
                _downWithAlt = false;
                break;

            case KeyEvent k:
                HandleKey(k);
                break;

            case ForegroundChangedEvent:
                CancelPending();
                break;
        }
    }

    // 产品决定：纯键盘操作下默认不弹浮窗，避免对键盘流的输入造成干扰。
    // 例外：Ctrl+A 全选有明确意图（用户希望对全文做点什么），保留弹窗能力，
    // 并通过 SelectionStateOptions.EnableSelectAllPopup 让用户在设置里随时关掉
    private void HandleKey(KeyEvent k)
    {
        const int VK_ESCAPE = 0x1B;
        const int VK_A = 0x41;

        if (!k.IsDown) return;

        if (k.VirtualKey == VK_ESCAPE)
        {
            CancelPending();
            return;
        }

        if (k.Shift && IsArrowOrNavKey(k.VirtualKey))
        {
            // Shift+方向键扩展选区：取消鼠标触发的待发弹窗，且不创建新候选
            CancelPending();
            return;
        }

        if (k.Ctrl && k.VirtualKey == VK_A)
        {
            var options = GetOptions();
            if (options.EnableSelectAllPopup && PassesModifierPolicy(false, true, false))
            {
                SchedulePending(
                    new SelectionCandidate(SelectionTrigger.KeyboardSelection, -1, -1, k.TimestampUtc),
                    GetDelay(k));
            }
            else
            {
                CancelPending();
            }
            return;
        }

        // 任何字符输入（含 Shift+字母大写）都视为选区被替换/取消
        if (!k.Ctrl && !k.Alt && IsTypingKey(k.VirtualKey))
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

    private void TryScheduleSelection(SelectionCandidate candidate, MouseUpEvent ev)
    {
        if (!PassesModifierPolicy(_downWithShift || ev.Shift, _downWithCtrl || ev.Ctrl, _downWithAlt || ev.Alt)) return;
        SchedulePending(candidate, GetDelay(ev));
    }

    private TimeSpan GetDelay(InputEvent ev)
    {
        var options = GetOptions();
        return options.PopupMode switch
        {
            SelectionPopupMode.Delayed => TimeSpan.FromMilliseconds(Math.Max(0, options.PopupDelayMs)),
            SelectionPopupMode.HoverStill => TimeSpan.FromMilliseconds(Math.Max(0, options.HoverDelayMs)),
            _ => TimeSpan.Zero,
        };
    }

    private bool PassesModifierPolicy(bool shift, bool ctrl, bool alt)
    {
        var options = GetOptions();
        if (options.PopupMode != SelectionPopupMode.ModifierRequired) return true;
        return MatchesModifier(options.RequiredModifier, shift, ctrl, alt);
    }

    private static bool MatchesModifier(SelectionModifierKey modifier, bool shift, bool ctrl, bool alt)
        => modifier switch
        {
            SelectionModifierKey.Ctrl => ctrl,
            SelectionModifierKey.Shift => shift,
            _ => alt,
        };

    private SelectionStateOptions GetOptions()
    {
        try
        {
            return _optionsProvider() ?? new SelectionStateOptions();
        }
        catch (Exception ex)
        {
            _log.Warn("selection options unavailable", ("err", ex.Message));
            return new SelectionStateOptions();
        }
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
