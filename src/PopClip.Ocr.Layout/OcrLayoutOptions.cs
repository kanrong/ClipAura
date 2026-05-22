namespace PopClip.Ocr.Layout;

public sealed record OcrLayoutOptions
{
    public float SameLineCenterYToleranceRatio { get; init; } = 0.55f;
    public float SameLineMinYOverlapRatio { get; init; } = 0.45f;
    public float RegionMaxVerticalGapRatio { get; init; } = 1.65f;
    public float RegionMaxHorizontalGapRatio { get; init; } = 3.5f;
    public float WrapFillRatio { get; init; } = 0.72f;
    public float LowercaseContinuationFillRatio { get; init; } = 0.55f;
    public float MetadataLineMaxHeightRatio { get; init; } = 0.82f;
    public int MinTableRows { get; init; } = 2;
    public float TableColumnXToleranceRatio { get; init; } = 1.25f;
    public float MinTableRowFillRatio { get; init; } = 0.18f;
    public float TableMaxGapToTokenWidthRatio { get; init; } = 4.5f;
    public float TableMaxGapToLineHeightRatio { get; init; } = 10f;

    /// <summary>判定列表项行首左对齐时容许的偏差，相对中位字高的比例。
    /// 0.5 表示左缘相差不超过半个字高即视为同一对齐簇。</summary>
    public float ListLeftAlignToleranceRatio { get; init; } = 0.5f;

    /// <summary>区域内被判为列表项 / 续行的行数占比阈值，达到则整块标记为 List。
    /// 0.6 留出 40% 余量，允许列表前后存在一两行标题 / 说明仍被识别为列表。</summary>
    public float ListMinClassifiedLineRatio { get; init; } = 0.6f;

    /// <summary>列表续行相对于列表项首行的最小缩进，相对中位字高的比例。
    /// 0.8 是一个字符宽的下限，避免把"另一项的轻微对齐误差"误判为续行。</summary>
    public float ListContinuationMinIndentRatio { get; init; } = 0.8f;

    /// <summary>列表续行允许的最大缩进，相对中位字高的比例。
    /// 4.0 覆盖到二级缩进，超出则视为嵌套结构而非续行。</summary>
    public float ListContinuationMaxIndentRatio { get; init; } = 4f;

    /// <summary>参考行宽计算使用的百分位（0-1）。
    /// 0.9 表示用 P90 行宽作为段尾折行判定的参考，
    /// 避开"region 内出现一两行特别长就让段尾短行被切碎"的常见误判。</summary>
    public float ReferenceLineWidthPercentile { get; init; } = 0.9f;

    /// <summary>段内允许的最大行间垂直间隙，相对中位字高的比例。
    /// 0.6 表示相邻行 vertical gap 超过 0.6 倍字高即视为段间分隔，强制换行。
    ///
    /// BuildTextComponents 已用 RegionMaxVerticalGapRatio=1.65 切大区域，
    /// 但行间间距落在 0.6 ~ 1.6 倍字高的"独立列表项 / 段落子块"会被错误聚到同一 region，
    /// 再被 fill ratio 串成一行。这里在区域内部做二次分隔，
    /// 让"看起来明显分行的内容"即使行首没有列表标记 / 行末没有句末标点也能保留换行。
    ///
    /// 默认 0.6 在常见 1.0~1.5 倍行距文档下不会误切（实际段内 gap ≤ 0.5 倍字高），
    /// 同时能切开 1.6 倍以上行距的独立条目（如带项目符号的并列短句列表）。</summary>
    public float MaxIntraRegionLineGapRatio { get; init; } = 0.6f;
}
