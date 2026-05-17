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
}
