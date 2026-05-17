namespace PopClip.App.Ocr;

/// <summary>OCR 识别返回的结构化结果。
///
/// 为什么不是简单的 string：iOS 风格的交互需要每段文字的屏幕位置，
/// 这样才能在原图上画高亮框、做点选 / 框选 / 字符级选择。
///
/// FullText 是按 reading order 拼好的全文，让"Quick"模式（不弹结果窗，直接复制）零成本沿用。
/// SourceWidth / SourceHeight 是识别时实际处理的图片像素尺寸（去畸变后），
/// 结果窗用它把 polygon 坐标缩放到 window DIP 尺寸。</summary>
/// <param name="Blocks">每段文字的文本 + 多边形位置 + 置信度。
/// 顺序为 reading order（从上到下、从左到右），与 FullText 中行的顺序一致。</param>
/// <param name="FullText">所有 block 文本拼接（多行用 \n 分隔），已 Trim。
/// 沿用旧 API 的调用方可以只看这个，等价于改造前 RecognizeAsync 返回的 string。</param>
/// <param name="SourceWidth">识别输入图的像素宽。Polygon 顶点坐标在 [0, SourceWidth) × [0, SourceHeight) 空间中。</param>
/// <param name="SourceHeight">识别输入图的像素高。</param>
public sealed record OcrResult(
    IReadOnlyList<OcrTextBlock> Blocks,
    string FullText,
    int SourceWidth,
    int SourceHeight)
{
    /// <summary>空结果常量（识别未命中时返回，避免调用方判 null）。
    /// SourceWidth/Height = 0 调用方应该当作 "没有识别空间" 处理。</summary>
    public static readonly OcrResult Empty = new(Array.Empty<OcrTextBlock>(), "", 0, 0);
}

/// <summary>单段 OCR 文本块。
/// 文本以行 / 短语 / 段落为粒度（取决于 provider 的内部检测器），不到字符级；
/// 字符级选择由结果窗在 UI 层用 TextBox 子层模拟。</summary>
/// <param name="Text">本块识别出的文本，已 Trim。</param>
/// <param name="Box">本块在原图中的四边形位置，用于在结果窗里画 overlay。</param>
/// <param name="Confidence">[0, 1] 区间的置信度；不同 provider 标定不同。
/// 没有给出置信度时返回 1.0（不当作不可信处理）。</param>
public sealed record OcrTextBlock(
    string Text,
    OcrPolygon Box,
    float Confidence);

/// <summary>四边形包围盒，4 个顶点按 左上 → 右上 → 右下 → 左下 顺序。
///
/// 为什么不是 axis-aligned Rect：倾斜 / 旋转文字的检测框是斜的，
/// RapidOcr 的 DBNet 返回的就是 4 个点；WeChat OCR 大多数情况下是水平矩形
/// （pos 缺失时退化为 left/top/right/bottom 矩形）。统一用四边形最通用。
///
/// 坐标空间与 OcrResult.SourceWidth/Height 一致（图片像素坐标，原点左上、Y 向下）。
/// 顶点顺序与 PaddleOCR / WeChat OCR 上游约定一致，调用方画 Polygon 时直接传 (X1,Y1,X2,Y2,X3,Y3,X4,Y4) 序列即可。</summary>
public readonly record struct OcrPolygon(
    float X1, float Y1,
    float X2, float Y2,
    float X3, float Y3,
    float X4, float Y4)
{
    /// <summary>由 axis-aligned 矩形构造四边形（WeChat OCR 走的快路径）。</summary>
    public static OcrPolygon FromRect(float left, float top, float right, float bottom)
        => new(left, top, right, top, right, bottom, left, bottom);

    /// <summary>四点的最小外接矩形，主要用于结果窗的命中检测 / marquee 求交。</summary>
    public (float Left, float Top, float Right, float Bottom) AABB()
    {
        float l = Math.Min(Math.Min(X1, X2), Math.Min(X3, X4));
        float r = Math.Max(Math.Max(X1, X2), Math.Max(X3, X4));
        float t = Math.Min(Math.Min(Y1, Y2), Math.Min(Y3, Y4));
        float b = Math.Max(Math.Max(Y1, Y2), Math.Max(Y3, Y4));
        return (l, t, r, b);
    }
}
