using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using PopClip.Core.Logging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace PopClip.App.Services;

/// <summary>本地 OCR 引擎封装。
/// 第一版仅接 Windows.Media.Ocr：零依赖、离线、用户已装的系统语言包决定可识别语言；
/// PaddleOCR / Tesseract 等更高精度引擎留给 v2 作为可选 backend。
///
/// 关键约束：
/// - 引擎用 TryCreateFromUserProfileLanguages 创建；用户语言里没有 OCR 支持时回退为英文
/// - 截图入参是 System.Drawing.Bitmap，调用方负责截屏
/// - 内部把 Bitmap → PNG 内存流 → BitmapDecoder → SoftwareBitmap，再交给 OcrEngine</summary>
internal sealed class OcrService
{
    private readonly ILog _log;
    private readonly OcrEngine? _engine;

    public OcrService(ILog log)
    {
        _log = log;
        try
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (_engine is null)
            {
                _engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
            }
        }
        catch (Exception ex)
        {
            _log.Warn("ocr engine init failed", ("err", ex.Message));
            _engine = null;
        }
    }

    /// <summary>OcrService 是否可用。未安装任何语言包时返回 false，调用方应给出明确 toast。
    /// 不在构造时抛异常，是为了让"OCR 不可用"也能让程序正常运行（其它功能不受影响）</summary>
    public bool IsAvailable => _engine is not null;

    public async Task<string> RecognizeAsync(Bitmap bitmap, CancellationToken ct)
    {
        if (_engine is null) return "";
        if (bitmap.Width < 4 || bitmap.Height < 4) return "";

        ct.ThrowIfCancellationRequested();
        try
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            using var ras = ms.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(ras).AsTask(ct).ConfigureAwait(false);
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync().AsTask(ct).ConfigureAwait(false);
            var result = await _engine.RecognizeAsync(softwareBitmap).AsTask(ct).ConfigureAwait(false);
            return (result.Text ?? string.Empty).Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("ocr recognize failed", ("err", ex.Message));
            return "";
        }
    }
}
