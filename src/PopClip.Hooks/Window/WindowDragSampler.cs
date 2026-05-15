using PopClip.Hooks.Interop;

namespace PopClip.Hooks.Window;

/// <summary>down/up 之间一次性输出两类启发式判定，供上层短路剪贴板兜底</summary>
/// <param name="WindowMoved">顶层窗口在拖动期间发生了位移或缩放（拖窗体本身）</param>
/// <param name="ScrollBarLikely">起点位于窗口边缘 + 拖动严格轴对齐，疑似拖动自绘滚动条</param>
public readonly record struct DragSample(bool WindowMoved, bool ScrollBarLikely);

/// <summary>在鼠标 down/up 之间采样目标顶层窗口位置/拖动轨迹，识别"拖窗体"与"拖滚动条"。
/// 目的：避免在这两类非文本场景下对前台进程合成 Ctrl+C 触发剪贴板兜底。
/// 拖窗体物理意义：拖窗体 → 顶层窗口 RECT 偏移/变形；拖选文本 → 窗口不动。
/// 拖滚动条启发式：起点在窗口右/左/下边缘 N px 内 + 拖动严格垂直/水平 → 几乎一定是滚动条。
/// 自绘窗口（Zed/Chrome/VSCode）也适用，因为只看坐标差与 RECT，不依赖 WM_NCHITTEST</summary>
public sealed class WindowDragSampler
{
    /// <summary>顶层窗口位置/大小累计偏移阈值（像素）。
    /// 选 3 是为了让用户在鼠标按下瞬间的微小抖动不被误判为窗体被拖动</summary>
    private const int WindowMovedThresholdPx = 3;

    /// <summary>起点距窗口边缘多少像素以内算"在边缘"。30 覆盖大多数原生与自绘滚动条宽度</summary>
    private const int EdgeThresholdPx = 30;

    /// <summary>严格轴对齐时另一轴允许的抖动容差。物理鼠标硬件本身有 1~2 像素微抖</summary>
    private const int AxisAlignedCrossAxisTolerancePx = 3;

    /// <summary>严格轴对齐时主轴的最小拖动距离，避免极短的非滚动微拖被误识别</summary>
    private const int AxisAlignedMinTravelPx = 15;

    private nint _hwnd;
    private NativeMethods.RECT _initialRect;
    private int _downX;
    private int _downY;
    private bool _hasSample;

    /// <summary>记录鼠标按下点所在的顶层窗口、其初始 RECT、按下坐标。
    /// 取不到（坐标空洞、窗口已销毁等）时 OnMouseUp 会保守返回全 false</summary>
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
        _downX = x;
        _downY = y;
        _hasSample = true;
    }

    /// <summary>一次性输出窗体移动与滚动条疑似两个标志，调用后状态被消费</summary>
    public DragSample OnMouseUp(int x, int y)
    {
        try
        {
            if (!_hasSample || _hwnd == 0) return default;

            if (!NativeMethods.GetWindowRect(_hwnd, out var current))
            {
                return default;
            }

            var windowMoved = DetectWindowMoved(in current);
            var scrollBarLikely = !windowMoved && DetectScrollBarLikely(x, y);
            return new DragSample(windowMoved, scrollBarLikely);
        }
        finally
        {
            _hasSample = false;
            _hwnd = 0;
        }
    }

    private bool DetectWindowMoved(in NativeMethods.RECT current)
    {
        var dLeft = Math.Abs(current.Left - _initialRect.Left);
        var dTop = Math.Abs(current.Top - _initialRect.Top);
        var dRight = Math.Abs(current.Right - _initialRect.Right);
        var dBottom = Math.Abs(current.Bottom - _initialRect.Bottom);

        return dLeft > WindowMovedThresholdPx
            || dTop > WindowMovedThresholdPx
            || dRight > WindowMovedThresholdPx
            || dBottom > WindowMovedThresholdPx;
    }

    /// <summary>边缘 + 严格轴对齐组合判定。
    /// 真实文本选区即使在边缘附近也极少呈现严格直线（手部抖动 + 字符 metrics 非整像素），
    /// 反过来滚动条 thumb 被应用约束在单轴上，dx 或 dy 必然接近 0，因此误伤率极低。
    /// 不识别顶部边缘：那里通常是标题栏，不会有滚动条</summary>
    private bool DetectScrollBarLikely(int upX, int upY)
    {
        var dx = upX - _downX;
        var dy = upY - _downY;
        var absDx = Math.Abs(dx);
        var absDy = Math.Abs(dy);

        var isVertical = absDx <= AxisAlignedCrossAxisTolerancePx && absDy >= AxisAlignedMinTravelPx;
        var isHorizontal = absDy <= AxisAlignedCrossAxisTolerancePx && absDx >= AxisAlignedMinTravelPx;
        if (!isVertical && !isHorizontal) return false;

        var nearRight = _downX <= _initialRect.Right
            && (_initialRect.Right - _downX) < EdgeThresholdPx;
        var nearLeft = _downX >= _initialRect.Left
            && (_downX - _initialRect.Left) < EdgeThresholdPx;
        var nearBottom = _downY <= _initialRect.Bottom
            && (_initialRect.Bottom - _downY) < EdgeThresholdPx;

        if (isVertical && (nearRight || nearLeft)) return true;
        if (isHorizontal && nearBottom) return true;
        return false;
    }
}
