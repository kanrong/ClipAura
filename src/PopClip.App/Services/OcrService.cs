using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using PopClip.Core.Logging;
using RapidOcrNet;
using SkiaSharp;
using DrawingBitmap = System.Drawing.Bitmap;

namespace PopClip.App.Services;

/// <summary>基于 RapidOcrNet（PP-OCRv5 ONNX 模型 + ONNX Runtime + SkiaSharp）的本地 OCR 引擎封装。
///
/// 与上一版 Sdcb.PaddleOCR 相比的取舍：
/// - 后端从 paddle_inference_c（C++ 大型推理引擎 + MKL-DNN）换成 ONNX Runtime（Microsoft.ML.OnnxRuntime），
///   总安装体积从 ~380 MB 降到 ~50 MB（其中模型 ~21 MB + ORT native ~25 MB）；
/// - 不再依赖 OpenCV native dll：图像处理统一走 SkiaSharp（跨平台 + AOT 友好）；
/// - 模型仍是 PaddleOCR 训练出来的 PP-OCRv5 中文模型（detector + recognizer + cls），中英文识别率与 V5 一致；
/// - 代价：首次冷启动稍慢（ORT 加载 + JIT 三个 session）；流式推理时延和精度与 Paddle 相当。
///
/// 关键约束：
/// - RapidOcr 内部三个模型 session 不强制 thread-safe，所有 RecognizeAsync 调用串行化（_gate）；
///   超时放弃时锁的归还绑定到 native runTask，避免下一次调用与上一次并行进入 ORT；
/// - 引擎构造在第一次 RecognizeAsync 或 PrewarmInBackground 内 lazy 触发；
/// - 异常容忍：构造失败 → IsAvailable 转 false；识别失败返回空串，上层 UI 给"未识别到文本"提示。
///
/// 预处理流水线：
/// 1. Bitmap → SKBitmap：走 PNG 编/解码中转，避开 32bppArgb premultiplied alpha 与 stride 对齐的坑；
/// 2. 短边 < kMinShortSide 时整数倍上采样：弥补 detector 对小图的检测劣势；
/// 3. RapidOcrOptions.Default：legacy ImgResize=1024 + 50px padding，对截图最稳，比 PythonCompat 命中率更高。</summary>
internal sealed class OcrService : IDisposable
{
    /// <summary>短边阈值：实测高度小于这个值时 detection 容易整片漏检。
    /// 上采样到 ≥48 像素能显著提高小字号截图的命中率，代价是 1~2x 的额外算力。</summary>
    private const int kMinShortSide = 48;

    private readonly ILog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RapidOcr? _engine;
    private bool _initFailed;
    private bool _disposed;

    public OcrService(ILog log)
    {
        _log = log;
    }

    /// <summary>构造失败前一直 true。真正失败发生在引擎冷启动，
    /// 设为 false 让 UI 给出"OCR 引擎初始化失败"提示而不是空文本。</summary>
    public bool IsAvailable => !_initFailed;

    /// <summary>引擎是否已完成 lazy 初始化。预热前是 false，预热或第一次识别后变 true。
    /// 调用方可据此决定是否给用户"首次稍慢"的提示。</summary>
    public bool IsEngineReady => _engine is not null;

    /// <summary>触发后台预热：用户按下 OCR 热键时立刻调用，让模型加载和用户框选并行进行。
    /// fire-and-forget；预热失败不抛异常（首次 RecognizeAsync 会再次走 EnsureEngine 并真实失败）。</summary>
    public void PrewarmInBackground()
    {
        if (_engine is not null || _initFailed) return;
        _ = Task.Run(() =>
        {
            try
            {
                _gate.Wait();
                try { EnsureEngine(); }
                finally { _gate.Release(); }
            }
            catch (Exception ex)
            {
                _log.Debug("ocr prewarm swallowed", ("err", ex.Message));
            }
        });
    }

    public async Task<string> RecognizeAsync(DrawingBitmap bitmap, CancellationToken ct)
    {
        if (_disposed) return "";
        if (bitmap.Width < 4 || bitmap.Height < 4) return "";
        ct.ThrowIfCancellationRequested();

        // Bitmap → SKBitmap：在锁外做，让多个截图请求的预处理可以并发
        SKBitmap? sk;
        try
        {
            sk = BitmapToSkBitmap(bitmap);
        }
        catch (Exception ex)
        {
            _log.Warn("ocr bitmap convert failed", ("err", ex.Message));
            return "";
        }
        if (sk is null || sk.IsEmpty)
        {
            sk?.Dispose();
            return "";
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        bool ownsLock = true;
        try
        {
            ct.ThrowIfCancellationRequested();
            var engine = EnsureEngine();
            if (engine is null) { sk.Dispose(); return ""; }

            // RapidOcr.Detect 是同步阻塞 native 调用，没有中断钩子。Task.Run 让出当前 thread；
            // 把锁的释放与 sk 的 Dispose 都挂到 runTask 完成上：调用方超时放弃后 native 仍在跑，
            // 锁与 SKBitmap 都不能提前释放，否则会与下一次调用 / native 内部访问竞争
            var skToDispose = sk;
            var runTask = Task.Run(() => RunInternal(engine, skToDispose), CancellationToken.None);
            _ = runTask.ContinueWith(_ =>
            {
                try { skToDispose.Dispose(); } catch { /* native handle 释放阶段噪音 */ }
                try { _gate.Release(); } catch (ObjectDisposedException) { /* 进程退出 */ }
            }, TaskScheduler.Default);
            ownsLock = false;

            var winner = await Task.WhenAny(runTask, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
            if (winner != runTask)
            {
                _log.Warn("ocr recognize timed out, native task still running");
                throw new OperationCanceledException(ct);
            }
            return await runTask.ConfigureAwait(false);
        }
        finally
        {
            if (ownsLock)
            {
                try { sk.Dispose(); } catch { }
                _gate.Release();
            }
        }
    }

    private string RunInternal(RapidOcr engine, SKBitmap src)
    {
        try
        {
            var sw = Stopwatch.StartNew();

            // 短边过小时按整数倍上采样：detector 对小字号 / 低分辨率截图常常整片漏检，
            // 放大后再识别能把命中率从 0% 拉到接近 100%，代价是 < 100 ms 额外算力。
            // SkiaSharp 3.x：用 Mitchell cubic resampler，比 Linear 边缘更锐
            SKBitmap working = src;
            int shortSide = Math.Min(src.Width, src.Height);
            int scale = 1;
            if (shortSide < kMinShortSide)
            {
                scale = Math.Min(4, (int)Math.Ceiling((double)kMinShortSide / shortSide));
                var info = new SKImageInfo(src.Width * scale, src.Height * scale,
                    src.ColorType, src.AlphaType);
                working = new SKBitmap(info);
                src.ScalePixels(working, new SKSamplingOptions(SKCubicResampler.Mitchell));
            }

            try
            {
                // 截图 OCR 的最佳预设：
                // - DoAngle=false / MostAngle=false：截图几乎不会出现倒立文字，关掉省 ~30 ms / 不会乱翻转
                // - 其余沿用 Default：50px padding + ImgResize=1024 长边封顶（小图保留原尺寸，不会被强行拉到 736）
                var opts = RapidOcrOptions.Default with
                {
                    DoAngle = false,
                    MostAngle = false,
                };
                var result = engine.Detect(working, opts);
                var text = (result.StrRes ?? string.Empty).Trim();
                sw.Stop();
                _log.Debug("ocr rapid run",
                    ("ms", sw.ElapsedMilliseconds),
                    ("len", text.Length),
                    ("blocks", result.TextBlocks?.Length ?? 0),
                    ("inputSize", $"{src.Width}x{src.Height}"),
                    ("scale", scale));
                return text;
            }
            finally
            {
                if (!ReferenceEquals(working, src)) working.Dispose();
            }
        }
        catch (Exception ex)
        {
            _log.Warn("ocr rapid recognize failed", ("err", ex.Message));
            return "";
        }
    }

    private RapidOcr? EnsureEngine()
    {
        if (_engine is not null) return _engine;
        if (_initFailed) return null;
        try
        {
            var sw = Stopwatch.StartNew();

            // 模型从 bin\models\v5\ 下加载（csproj 把 Assets\OcrModels\v5\ 同步过去）。
            // RapidOcr 接收完整文件路径，每个 session 一份；不再依赖 NuGet 包自带 latin 模型。
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var modelDir = Path.Combine(baseDir, "models", "v5");
            var det = Path.Combine(modelDir, "ch_PP-OCRv5_det_mobile.onnx");
            var cls = Path.Combine(modelDir, "ch_ppocr_mobile_v2.0_cls_infer.onnx");
            var rec = Path.Combine(modelDir, "ch_PP-OCRv5_rec_mobile.onnx");
            var dict = Path.Combine(modelDir, "ppocrv5_dict.txt");

            // 缺一不可，提前 throw 让用户在 log 里看到具体丢哪个，比 ORT 内部那一长串 native 异常友好
            foreach (var p in new[] { det, cls, rec, dict })
            {
                if (!File.Exists(p))
                    throw new FileNotFoundException($"OCR 模型文件缺失: {Path.GetFileName(p)}", p);
            }

            // ORT 默认会取所有可用 CPU 核心做 intra-op 并行。对 OCR 这种短任务（< 1s），
            // numThread=0 表示交给 ORT 默认策略，通常拿到的吞吐已足够
            var engine = new RapidOcr();
            engine.InitModels(det, cls, rec, dict);
            _engine = engine;

            sw.Stop();
            _log.Info("ocr rapid engine ready",
                ("ms", sw.ElapsedMilliseconds),
                ("backend", "onnxruntime"),
                ("model", "PP-OCRv5-ch-mobile"));
            return _engine;
        }
        catch (Exception ex)
        {
            _initFailed = true;
            _log.Error("ocr rapid engine init failed", ex);
            return null;
        }
    }

    /// <summary>Bitmap → SKBitmap，走 PNG 编/解码中转。
    ///
    /// 这条路径看起来"绕"，但实测最稳：
    /// 1. CopyFromScreen 出来的 Bitmap 几乎都是 32bppArgb / 32bppPArgb / 24bppRgb 之间各种组合，
    ///    pre-multiplied alpha 与 raw alpha 直接 LockBits + memcpy 到 SKBitmap 时颜色会偏暗或反色；
    /// 2. PNG 编码会把 GDI+ 的色彩规范化（lossless，无 alpha 偏移），SKBitmap.Decode 完整保持原色；
    /// 3. 中间消耗 5~30 ms，对总 OCR 时延（~300 ms）不显著。
    ///
    /// 若日后要把首次截图时延再压一截，可以改写成 LockBits + SKBitmap.InstallPixels（unsafe），
    /// 但需要按 stride 行 copy 并解 premul，未来优化项。</summary>
    private static SKBitmap BitmapToSkBitmap(DrawingBitmap source)
    {
        using var ms = new MemoryStream();
        source.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        var sk = SKBitmap.Decode(ms);
        if (sk is null)
            throw new InvalidOperationException("SKBitmap.Decode 返回 null，PNG 编码或解码失败");
        return sk;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _engine?.Dispose(); } catch { /* swallow: 进程退出阶段释放，不应阻塞 */ }
        _engine = null;
        _gate.Dispose();
    }
}
