using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using WpfClipboard = System.Windows.Clipboard;

namespace PopClip.Uia.Clipboard;

/// <summary>所有剪贴板读写都通过本类执行，由 ClipboardThread 保证在 STA 上运行。
/// 直接使用 System.Windows.Clipboard 时偶发的 ExternalException(剪贴板被占用) 在此做有界重试</summary>
public sealed class ClipboardAccess
{
    private readonly ClipboardThread _thread;
    private const int RetryCount = 3;
    private const int RetryDelayMs = 12;
    private static readonly string[] EncodedImageFormats =
    {
        "PNG",
        "image/png",
        "JFIF",
        "JPEG",
        "image/jpeg",
    };

    public ClipboardAccess(ClipboardThread thread) => _thread = thread;

    public string? GetText() => _thread.Invoke(GetTextOnSta);

    /// <summary>仅检查剪贴板是否包含文本，不实际复制内容；
    /// 用于 PasteAction.CanRun 等高频判断点，避免每次浮窗弹出都把潜在的大段剪贴板正文搬到本进程</summary>
    public bool HasText() => _thread.Invoke(HasTextOnSta);

    /// <summary>仅检查剪贴板是否包含图片，不实际搬运位图数据。</summary>
    public bool HasImage() => _thread.Invoke(HasImageOnSta);

    /// <summary>把剪贴板图片编码成 PNG 字节后跨线程返回，避免 BitmapSource 跨 Dispatcher 使用。</summary>
    public byte[]? GetImagePngBytes() => _thread.Invoke(GetImagePngBytesOnSta);

    public void SetText(string text) => _thread.Invoke(() => SetTextOnSta(text));

    public void SetImagePngBytes(byte[] pngBytes) => _thread.Invoke(() => SetImagePngBytesOnSta(pngBytes));

    public void Clear() => _thread.Invoke(() =>
    {
        for (var i = 0; i < RetryCount; i++)
        {
            try { WpfClipboard.Clear(); return; }
            catch (COMException) { Thread.Sleep(RetryDelayMs); }
            catch (ExternalException) { Thread.Sleep(RetryDelayMs); }
        }
    });

    public ClipboardSnapshot Capture() => _thread.Invoke(ClipboardSnapshot.CaptureOnSta);

    public void Restore(ClipboardSnapshot snapshot) => _thread.Invoke(snapshot.RestoreOnSta);

    /// <summary>把快照内容写入剪贴板（多格式）。实现与 Restore 相同，语义上区别在于：
    /// 这里是"主动写入采集到的选区数据"（复制动作回写富文本），而非"恢复先前备份"</summary>
    public void WriteSnapshot(ClipboardSnapshot snapshot) => _thread.Invoke(snapshot.RestoreOnSta);

    private static string? GetTextOnSta()
    {
        for (var i = 0; i < RetryCount; i++)
        {
            try
            {
                return WpfClipboard.ContainsText() ? WpfClipboard.GetText() : null;
            }
            catch (COMException) { Thread.Sleep(RetryDelayMs); }
            catch (ExternalException) { Thread.Sleep(RetryDelayMs); }
            catch (Exception) { return null; }
        }
        return null;
    }

    private static bool HasTextOnSta()
    {
        for (var i = 0; i < RetryCount; i++)
        {
            try { return WpfClipboard.ContainsText(); }
            catch (COMException) { Thread.Sleep(RetryDelayMs); }
            catch (ExternalException) { Thread.Sleep(RetryDelayMs); }
            catch (Exception) { return false; }
        }
        return false;
    }

    private static bool HasImageOnSta()
    {
        for (var i = 0; i < RetryCount; i++)
        {
            try
            {
                if (WpfClipboard.ContainsImage()) return true;
                var data = WpfClipboard.GetDataObject();
                return HasRawImageFormat(data);
            }
            catch (COMException) { Thread.Sleep(RetryDelayMs); }
            catch (ExternalException) { Thread.Sleep(RetryDelayMs); }
            catch (Exception) { return false; }
        }
        return false;
    }

    private static byte[]? GetImagePngBytesOnSta()
    {
        for (var i = 0; i < RetryCount; i++)
        {
            try
            {
                var data = WpfClipboard.GetDataObject();
                if (data is null) return null;

                var encoded = TryGetEncodedImageAsPng(data);
                if (encoded is not null) return encoded;

                var dib = TryGetDibAsPng(data);
                if (dib is not null) return dib;

                if (!WpfClipboard.ContainsImage()) return null;
                var image = WpfClipboard.GetImage();
                return image is null ? null : EncodeBitmapSourceAsPng(image);
            }
            catch (COMException) { Thread.Sleep(RetryDelayMs); }
            catch (ExternalException) { Thread.Sleep(RetryDelayMs); }
            catch (Exception) { return null; }
        }
        return null;
    }

    private static bool HasRawImageFormat(IDataObject? data)
    {
        if (data is null) return false;
        foreach (var format in EncodedImageFormats)
        {
            if (data.GetDataPresent(format, autoConvert: false)) return true;
        }
        return data.GetDataPresent(DataFormats.Dib, autoConvert: false)
               || data.GetDataPresent(DataFormats.Bitmap, autoConvert: false);
    }

    private static byte[]? TryGetEncodedImageAsPng(IDataObject data)
    {
        foreach (var format in EncodedImageFormats)
        {
            if (!data.GetDataPresent(format, autoConvert: false)) continue;
            var bytes = ExtractBytes(data.GetData(format, autoConvert: false));
            if (bytes is null || bytes.Length == 0) continue;
            if (format.Equals("PNG", StringComparison.OrdinalIgnoreCase)
                || format.Equals("image/png", StringComparison.OrdinalIgnoreCase))
            {
                return bytes;
            }
            return DecodeAndEncodePng(bytes);
        }
        return null;
    }

    private static byte[]? TryGetDibAsPng(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.Dib, autoConvert: false)) return null;
        var dib = ExtractBytes(data.GetData(DataFormats.Dib, autoConvert: false));
        if (dib is null || dib.Length < 40) return null;
        var bmp = WrapDibAsBmp(dib);
        return DecodeAndEncodePng(bmp);
    }

    private static byte[]? ExtractBytes(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case byte[] bytes:
                return bytes;
            case MemoryStream ms:
                return ms.ToArray();
            case Stream stream:
                using (stream)
                using (var copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    return copy.ToArray();
                }
            default:
                return null;
        }
    }

    private static byte[]? DecodeAndEncodePng(byte[] encoded)
    {
        try
        {
            using var ms = new MemoryStream(encoded);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            return EncodeBitmapSourceAsPng(decoder.Frames[0]);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] WrapDibAsBmp(byte[] dib)
    {
        var headerSize = BitConverter.ToInt32(dib, 0);
        var colorsUsed = dib.Length >= 36 ? BitConverter.ToInt32(dib, 32) : 0;
        var bitCount = dib.Length >= 16 ? BitConverter.ToUInt16(dib, 14) : (ushort)32;
        var colorTableBytes = colorsUsed > 0
            ? colorsUsed * 4
            : bitCount <= 8 ? (1 << bitCount) * 4 : 0;
        var pixelOffset = 14 + headerSize + colorTableBytes;
        var fileSize = 14 + dib.Length;
        var bmp = new byte[fileSize];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(fileSize).CopyTo(bmp, 2);
        BitConverter.GetBytes(pixelOffset).CopyTo(bmp, 10);
        Buffer.BlockCopy(dib, 0, bmp, 14, dib.Length);
        return bmp;
    }

    private static byte[] EncodeBitmapSourceAsPng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static void SetTextOnSta(string text)
    {
        for (var i = 0; i < RetryCount; i++)
        {
            try
            {
                WpfClipboard.SetDataObject(text, copy: true);
                return;
            }
            catch (COMException) { Thread.Sleep(RetryDelayMs); }
            catch (ExternalException) { Thread.Sleep(RetryDelayMs); }
        }
    }

    private static void SetImagePngBytesOnSta(byte[] pngBytes)
    {
        if (pngBytes is null || pngBytes.Length == 0) return;

        for (var i = 0; i < RetryCount; i++)
        {
            try
            {
                using var decode = new MemoryStream(pngBytes);
                var decoder = BitmapDecoder.Create(decode, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0) return;
                var frame = decoder.Frames[0];
                frame.Freeze();

                var data = new DataObject();
                data.SetData("PNG", new MemoryStream(pngBytes), autoConvert: false);
                data.SetData("image/png", new MemoryStream(pngBytes), autoConvert: false);
                data.SetData(DataFormats.Bitmap, frame, autoConvert: true);
                WpfClipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (COMException) { Thread.Sleep(RetryDelayMs); }
            catch (ExternalException) { Thread.Sleep(RetryDelayMs); }
            catch (Exception) { return; }
        }
    }
}
