using PopClip.App.Ocr;

namespace PopClip.Ocr.Layout;

public enum OcrLayoutContentKind
{
    Unknown,
    Paragraph,
    Table,
    Mixed,
}

public sealed record OcrLayoutResult(
    IReadOnlyList<OcrLayoutRegion> Regions,
    string PlainText)
{
    public static readonly OcrLayoutResult Empty = new(Array.Empty<OcrLayoutRegion>(), "");
}

public sealed record OcrLayoutRegion(
    OcrLayoutContentKind ContentKind,
    OcrLayoutRect Bounds,
    IReadOnlyList<OcrLayoutLine> Lines,
    string Text);

public sealed record OcrLayoutLine(
    OcrLayoutRect Bounds,
    string Text,
    IReadOnlyList<int> SourceBlockIndexes);

public readonly record struct OcrLayoutRect(float Left, float Top, float Right, float Bottom)
{
    public float Width => Math.Max(0, Right - Left);
    public float Height => Math.Max(0, Bottom - Top);
    public float CenterX => (Left + Right) / 2f;
    public float CenterY => (Top + Bottom) / 2f;

    public static OcrLayoutRect FromPolygon(OcrPolygon polygon)
    {
        var (left, top, right, bottom) = polygon.AABB();
        return new OcrLayoutRect(left, top, right, bottom);
    }

    public static OcrLayoutRect Union(IEnumerable<OcrLayoutRect> rects)
    {
        var hasAny = false;
        float left = 0, top = 0, right = 0, bottom = 0;
        foreach (var r in rects)
        {
            if (!hasAny)
            {
                left = r.Left;
                top = r.Top;
                right = r.Right;
                bottom = r.Bottom;
                hasAny = true;
                continue;
            }

            left = Math.Min(left, r.Left);
            top = Math.Min(top, r.Top);
            right = Math.Max(right, r.Right);
            bottom = Math.Max(bottom, r.Bottom);
        }

        return hasAny ? new OcrLayoutRect(left, top, right, bottom) : default;
    }

    public float VerticalOverlapRatio(OcrLayoutRect other)
    {
        var overlap = Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top);
        if (overlap <= 0) return 0;
        var basis = Math.Min(Height, other.Height);
        return basis <= 0 ? 0 : overlap / basis;
    }

    public float HorizontalOverlapRatio(OcrLayoutRect other)
    {
        var overlap = Math.Min(Right, other.Right) - Math.Max(Left, other.Left);
        if (overlap <= 0) return 0;
        var basis = Math.Min(Width, other.Width);
        return basis <= 0 ? 0 : overlap / basis;
    }

    public float HorizontalGap(OcrLayoutRect other)
    {
        if (Right < other.Left) return other.Left - Right;
        if (other.Right < Left) return Left - other.Right;
        return 0;
    }

    public float VerticalGap(OcrLayoutRect other)
    {
        if (Bottom < other.Top) return other.Top - Bottom;
        if (other.Bottom < Top) return Top - other.Bottom;
        return 0;
    }
}
