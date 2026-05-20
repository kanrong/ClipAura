namespace PopClip.App.Ocr;

public enum OcrImagePixelFormat
{
    Bgra32,
}

/// <summary>OCR 输入图像；优先携带原始像素，只有文件型 provider 需要时才生成 PNG。</summary>
public sealed class OcrImage
{
    private readonly Lazy<byte[]> _pngBytes;

    public OcrImage(
        int width,
        int height,
        OcrImagePixelFormat pixelFormat,
        byte[] pixels,
        int stride,
        Func<byte[]> pngFactory)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (pixels is null || pixels.Length == 0) throw new ArgumentException("像素 buffer 不能为空", nameof(pixels));
        if (stride < width * 4) throw new ArgumentOutOfRangeException(nameof(stride));
        if (pngFactory is null) throw new ArgumentNullException(nameof(pngFactory));
        var requiredBytes = checked(stride * height);
        if (pixels.Length < requiredBytes)
            throw new ArgumentException("像素 buffer 长度小于 width/height/stride 描述的图像范围", nameof(pixels));

        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        Pixels = pixels;
        Stride = stride;
        _pngBytes = new Lazy<byte[]>(pngFactory, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int Width { get; }
    public int Height { get; }
    public OcrImagePixelFormat PixelFormat { get; }
    public byte[] Pixels { get; }
    public int Stride { get; }

    public byte[] GetPngBytes() => _pngBytes.Value;
}
