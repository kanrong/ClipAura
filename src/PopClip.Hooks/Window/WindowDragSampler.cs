using PopClip.Hooks.Interop;

namespace PopClip.Hooks.Window;

/// <summary>在鼠标 down/up 之间采样目标顶层窗口位置，识别"拖动窗体本身"。
/// 目的：避免在拖窗体场景下对前台进程合成 Ctrl+C 触发剪贴板兜底。
/// 物理意义：拖窗体 → 顶层窗口 RECT 会偏移/变形；拖选文本 → 窗口不动。
/// 自绘窗口（Zed、Chrome 等）整个 client 区都返回 HTCLIENT，靠 WM_NCHITTEST 无法识别，
/// 但本采样器只看坐标差，对自绘窗口同样有效</summary>
public sealed class WindowDragSampler
{
    /// <summary>顶层窗口位置/大小累计偏移阈值（像素）。
    /// 选 3 是为了让用户在鼠标按下瞬间的微小抖动不被误判为窗体被拖动</summary>
    private const int MovedThresholdPx = 3;

    private nint _hwnd;
    private NativeMethods.RECT _initialRect;
    private bool _hasSample;

    /// <summary>记录鼠标按下点所在的顶层窗口及其初始 RECT。
    /// 如果取不到（坐标空洞、窗口已销毁等），后续 IsLikelyWindowDrag 会保守返回 false</summary>
    public void OnMouseDown(int x, int y)
    {
        _hasSample = false;
        _hwnd = 0;

        var pt = new NativeMethods.POINT { X = x, Y = y };
        var hwnd = NativeMethods.WindowFromPoint(pt);
        if (hwnd == 0) return;

        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (root == 0) return;

        if (!NativeMethods.GetWindowRect(root, out var rect)) return;

        _hwnd = root;
        _initialRect = rect;
        _hasSample = true;
    }

    /// <summary>对照 down 时的 RECT，判断窗体是否发生了位移或尺寸变化。
    /// 任一维度（左/上/右/下）变化超过阈值即视为窗体被拖动/缩放</summary>
    public bool OnMouseUpDetectMoved()
    {
        if (!_hasSample) return false;
        if (_hwnd == 0) return false;

        try
        {
            if (!NativeMethods.GetWindowRect(_hwnd, out var current)) return false;

            var dLeft = Math.Abs(current.Left - _initialRect.Left);
            var dTop = Math.Abs(current.Top - _initialRect.Top);
            var dRight = Math.Abs(current.Right - _initialRect.Right);
            var dBottom = Math.Abs(current.Bottom - _initialRect.Bottom);

            return dLeft > MovedThresholdPx
                || dTop > MovedThresholdPx
                || dRight > MovedThresholdPx
                || dBottom > MovedThresholdPx;
        }
        finally
        {
            _hasSample = false;
            _hwnd = 0;
        }
    }
}
