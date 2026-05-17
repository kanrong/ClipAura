using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using PopClip.Core.Logging;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingRectangle = System.Drawing.Rectangle;

namespace PopClip.App.Services;

/// <summary>基于 PaddleOCR (Sdcb.PaddleOCR + ChineseV5 中英文模型 + MKL-DNN CPU runtime) 的本地 OCR 引擎封装。
///
/// 与第一版 Windows.Media.Ocr 相比：
/// - 不依赖系统 OCR 语言包，开箱即用（模型随程序分发），中英文都默认可识别；
/// - 精度显著优于系统 OCR，长文本 / 倾斜文本 / 多列文本表现更稳；
/// - 代价：模型 + native runtime 体积较大；首个识别请求需要做一次冷启动（实例化推理引擎），
///   之后的请求复用同一个 PaddleOcrAll 实例。
///
/// 关键约束：
/// - PaddleOcrAll 不是线程安全的，所有 RecognizeAsync 调用强制串行化（_gate）；
///   超时放弃时锁的归还绑定到 native runTask，避免下一次调用与 native 并行进入引擎；
/// - 引擎构造在第一次 RecognizeAsync 或 PrewarmInBackground 内 lazy 触发；
/// - 异常容忍：构造失败 → IsAvailable 转 false；识别失败返回空串，上层 UI 给"未识别到文本"提示。
///
/// 预处理流水线：
/// 1. Bitmap (任意 PixelFormat) → 强制 24bppRgb：消除 alpha 通道与色彩异常；
/// 2. 24bppRgb → OpenCV BGR Mat（通过 LockBits + Marshal.Copy 零编码拷贝，避开 PNG 编/解码循环）；
/// 3. 短边 < kMinShortSide 时整数倍上采样：弥补 detector 对小图的检测劣势。</summary>
internal sealed class OcrService : IDisposable
{
    /// <summary>短边阈值：实测高度小于这个值时 detection 容易整片漏检。
    /// 上采样到 ≥48 像素能显著提高小字号截图的命中率，代价是 1~2x 的额外算力。</summary>
    private const int kMinShortSide = 48;

    private readonly ILog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PaddleOcrAll? _engine;
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

        // Bitmap → BGR Mat：在锁外做，让多个截图请求的预处理可以并发
        Mat? mat;
        try
        {
            mat = BitmapToBgrMat(bitmap);
        }
        catch (Exception ex)
        {
            _log.Warn("ocr bitmap convert failed", ("err", ex.Message));
            return "";
        }
        if (mat is null || mat.Empty()) return "";

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        bool ownsLock = true;
        try
        {
            ct.ThrowIfCancellationRequested();
            var engine = EnsureEngine();
            if (engine is null) { mat.Dispose(); return ""; }

            // PaddleOcrAll.Run 是同步阻塞 native 调用，没有中断钩子。Task.Run 让出当前 thread；
            // 把锁的释放与 mat 的 Dispose 都挂到 runTask 完成上：调用方超时放弃后 native 仍在跑，
            // 锁与 mat 都不能提前释放，否则会与下一次调用 / native 内部访问竞争
            var matToDispose = mat;
            var runTask = Task.Run(() => RunInternal(engine, matToDispose), CancellationToken.None);
            _ = runTask.ContinueWith(_ =>
            {
                try { matToDispose.Dispose(); } catch { /* native handle 释放阶段噪音 */ }
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
                try { mat.Dispose(); } catch { }
                _gate.Release();
            }
        }
    }

    private string RunInternal(PaddleOcrAll engine, Mat src)
    {
        try
        {
            var sw = Stopwatch.StartNew();

            // 短边过小时按整数倍上采样：detector 对小字号 / 低分辨率截图常常整片漏检，
            // 放大后再识别能把命中率从 0% 拉到接近 100%，代价是 < 100 ms 额外算力
            Mat working;
            int shortSide = Math.Min(src.Width, src.Height);
            int scale = 1;
            if (shortSide < kMinShortSide)
            {
                scale = (int)Math.Ceiling((double)kMinShortSide / shortSide);
                scale = Math.Min(scale, 4);
                working = new Mat();
                Cv2.Resize(src, working, new OpenCvSharp.Size(src.Width * scale, src.Height * scale),
                    interpolation: InterpolationFlags.Cubic);
            }
            else
            {
                working = src;
            }

            try
            {
                var result = engine.Run(working);
                var text = (result.Text ?? string.Empty).Trim();
                sw.Stop();
                _log.Debug("ocr paddle run",
                    ("ms", sw.ElapsedMilliseconds),
                    ("len", text.Length),
                    ("regions", result.Regions?.Length ?? 0),
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
            _log.Warn("ocr paddle recognize failed", ("err", ex.Message));
            return "";
        }
    }

    private PaddleOcrAll? EnsureEngine()
    {
        if (_engine is not null) return _engine;
        if (_initFailed) return null;
        try
        {
            var sw = Stopwatch.StartNew();
            // ChineseV5：PP-OCRv5 中英文模型，detector + recognizer 直接从 Sdcb.PaddleOCR.Models.LocalV5
            // 子包嵌入资源加载，绕开 Sdcb.PaddleOCR.Models.Local 主包对 V3 + V4 的 ~250 MB 模型依赖。
            // V5 detector/recognizer 在 UI 截图上实测识别率 0.99+，与 V4 持平甚至更高
            FullOcrModel model = EmbeddedV5Models.ChineseFullModel;

            // PaddleDevice.Mkldnn()：CPU + MKL-DNN，适合普通用户机器；如果需要 GPU 后续可再加分支
            _engine = new PaddleOcrAll(model, PaddleDevice.Mkldnn())
            {
                // 关键：必须关闭旋转检测。
                // detector 在小图 (< 200px 短边) + 文字偏扁的截图上 minAreaRect 会误判为 -90°
                // 竖排文字框（rect 尺寸长宽倒置），recognizer 跟着旋转 90° 去识别 → 整段乱码。
                // OCR 截选实测场景几乎全是水平文字（代码 / 网页 / PDF / UI），关掉它后识别率从 ~0 升至 99%+。
                // 如果未来有竖排文字需求（PPT 设计 / 漫画），加 setting toggle 暴露给用户即可。
                AllowRotateDetection = false,
                // 关闭 180 度翻转分类：截图几乎不会有倒立文字，节省 ~10% 时延
                Enable180Classification = false,
            };

            // Detector 参数调宽，提高小图与紧贴边界文字的命中率：
            // - MaxSize=1536：原图较大时不会被过度缩小，保留细节；小图保持原尺寸
            // - UnclipRatio=1.8：默认 1.6 在紧贴边界时会切掉字符上下半部分，
            //   1.8 让 detection box 向外扩大 ~12.5%，给 recognizer 拿到完整字符
            if (_engine.Detector is not null)
            {
                _engine.Detector.MaxSize = 1536;
                _engine.Detector.UnclipRatio = 1.8f;
            }

            sw.Stop();
            _log.Info("ocr paddle engine ready",
                ("ms", sw.ElapsedMilliseconds), ("device", "mkldnn"), ("model", "ChineseV5"));
            return _engine;
        }
        catch (Exception ex)
        {
            _initFailed = true;
            _log.Error("ocr paddle engine init failed", ex);
            return null;
        }
    }

    /// <summary>Bitmap → OpenCV BGR Mat 零编码转换。
    /// 中间统一过一道 24bppRgb 是为了：
    /// 1. 屏蔽源 PixelFormat 千变万化（CopyFromScreen 出 32bppArgb / 剪贴板出 32bppRgb / 屏幕缓存 16bppRgb…）；
    /// 2. 消除 alpha 通道带来的色彩异常（OpenCV 把 ARGB 直接当 BGR 解会全屏发黑或反色）。
    /// 转换走 OpenCvSharp.Extensions.BitmapConverter.ToMat，内部 LockBits + Marshal.Copy 零编码拷贝。</summary>
    private static Mat BitmapToBgrMat(DrawingBitmap source)
    {
        using var normalized = new DrawingBitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(normalized))
        {
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(source, new DrawingRectangle(0, 0, source.Width, source.Height));
        }
        return BitmapConverter.ToMat(normalized);
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
