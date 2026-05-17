using System.Diagnostics;
using System.IO;
using PopClip.App.Ocr;
using PopClip.Core.Logging;
using RapidOcrNet;
using SkiaSharp;

namespace PopClip.App.Ocr.Providers;

/// <summary>RapidOcrNet + PP-OCRv5 中英文 mobile 模型，作为按需 plugin 分发。
///
/// 物理布局：编译为 plugins/ocr/rapid-onnx/runtime/PopClip.App.OcrProvider.RapidOnnx.dll，
/// 跟它的 RapidOcrNet.dll / Microsoft.ML.OnnxRuntime.dll / SkiaSharp.dll + native dll 一起放。
/// 主程序通过 OcrPluginLoader 用 AssemblyDependencyResolver-based PluginLoadContext 加载。
///
/// 模型文件位置：plugins/ocr/rapid-onnx/v5/，跟 plugin dll 同 plugin 根目录但不在 runtime 子目录里。
/// 这样用户删 v5/ 时模型缺失而 plugin 仍能尝试加载（给出更精确的"缺模型"诊断），
/// 删 runtime/ 时整个 provider 不会被注册（plugin loader 在加载阶段就跳过）。
///
/// Priority=100：所有 provider 中最高，"自动"模式下首选；
/// 用户想用 WeChat OCR 时直接在设置里选 WeChat，或删除整个 plugins/ocr/rapid-onnx/runtime/ 目录。</summary>
public sealed class RapidOcrProvider : IOcrProvider
{
    /// <summary>短边阈值：实测高度小于这个值时 detection 容易整片漏检。
    /// 上采样到 ≥48 像素能显著提高小字号截图的命中率，代价是 1~2x 的额外算力。</summary>
    private const int kMinShortSide = 48;

    private readonly ILog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RapidOcr? _engine;
    private bool _initFailed;
    private string? _initErrorMessage;
    private bool _disposed;

    public RapidOcrProvider(ILog log) { _log = log; }

    public string Id => OcrProviderIds.RapidOnnx;
    public string DisplayName => "RapidOCR";
    public int Priority => 100;

    /// <summary>plugin 根目录：plugins/ocr/rapid-onnx/，模型文件放在它的 v5/ 子目录。
    /// 用 AppDomain.CurrentDomain.BaseDirectory 而不是 Environment.CurrentDirectory：
    /// 服务模式 / 自动启动时 BaseDirectory 才是 ClipAura.exe 所在目录。</summary>
    private static string PluginDir =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins", "ocr", "rapid-onnx", "v5");

    /// <summary>模型 4 件套：detector、classifier、recognizer、keys 字典。
    /// 从 ModelScope (RapidAI/RapidOCR) 下载，PP-OCRv5 中英文 mobile 版本</summary>
    private static (string Det, string Cls, string Rec, string Keys) ModelFiles =>
        ("ch_PP-OCRv5_det_mobile.onnx",
         "ch_ppocr_mobile_v2.0_cls_infer.onnx",
         "ch_PP-OCRv5_rec_mobile.onnx",
         "ppocrv5_dict.txt");

    /// <summary>初始化失败也算"不可用"：避免反复触发同一个失败的加载。
    /// 文件齐全 + 上一次 EnsureEngine 没失败 = 可用。</summary>
    public bool IsAvailable
    {
        get
        {
            if (_disposed) return false;
            if (_initFailed) return false;
            return GetMissingFiles().Length == 0;
        }
    }

    public bool IsEngineReady => _engine is not null;

    public string? UnavailableReason
    {
        get
        {
            if (_disposed) return "provider 已释放";
            if (_initFailed) return _initErrorMessage ?? "上一次初始化失败";
            var missing = GetMissingFiles();
            if (missing.Length == 0) return null;
            return $"缺少模型文件 ({missing.Length} 个)：{string.Join(", ", missing.Select(Path.GetFileName))}。" +
                   $"请把它们放到 {PluginDir}";
        }
    }

    private static string[] GetMissingFiles()
    {
        var (det, cls, rec, keys) = ModelFiles;
        return new[] { det, cls, rec, keys }
            .Select(name => Path.Combine(PluginDir, name))
            .Where(p => !File.Exists(p))
            .ToArray();
    }

    public void PrewarmInBackground()
    {
        if (_engine is not null || _initFailed || _disposed) return;
        if (!IsAvailable) return;
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
                _log.Debug("ocr prewarm swallowed", ("id", Id), ("err", ex.Message));
            }
        });
    }

    public async Task<string> RecognizeAsync(byte[] pngBytes, CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
        if (pngBytes is null || pngBytes.Length == 0) return "";
        ct.ThrowIfCancellationRequested();

        SKBitmap? sk;
        try { sk = SKBitmap.Decode(pngBytes); }
        catch (Exception ex)
        {
            _log.Warn("ocr png decode failed", ("id", Id), ("err", ex.Message));
            return "";
        }
        if (sk is null || sk.IsEmpty || sk.Width < 4 || sk.Height < 4)
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
            if (engine is null)
            {
                sk.Dispose();
                throw new InvalidOperationException($"OCR provider '{Id}' 引擎初始化失败：{_initErrorMessage}");
            }

            // 把锁与 sk 的释放绑定到 native runTask 完成，避免调用方超时取消后 sk/锁被提前释放，
            // 与下一次请求或仍在跑的 native 任务竞争
            var skToDispose = sk;
            var runTask = Task.Run(() => RunInternal(engine, skToDispose), CancellationToken.None);
            _ = runTask.ContinueWith(_ =>
            {
                try { skToDispose.Dispose(); } catch { }
                try { _gate.Release(); } catch (ObjectDisposedException) { /* 进程退出 */ }
            }, TaskScheduler.Default);
            ownsLock = false;

            var winner = await Task.WhenAny(runTask, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
            if (winner != runTask)
            {
                _log.Warn("ocr recognize timed out, native task still running", ("id", Id));
                throw new OperationCanceledException(ct);
            }
            return await runTask.ConfigureAwait(false);
        }
        finally
        {
            if (ownsLock)
            {
                try { sk.Dispose(); } catch { }
                try { _gate.Release(); } catch (ObjectDisposedException) { }
            }
        }
    }

    private string RunInternal(RapidOcr engine, SKBitmap src)
    {
        try
        {
            var sw = Stopwatch.StartNew();

            // 小图上采样：detector 对短边 < 48 px 的截图常常整片漏检；放大后命中率显著提升。
            // SkiaSharp 3.x：Mitchell cubic 比 Linear 边缘更锐，更适合文字
            SKBitmap working = src;
            int shortSide = Math.Min(src.Width, src.Height);
            int scale = 1;
            if (shortSide < kMinShortSide)
            {
                scale = Math.Min(4, (int)Math.Ceiling((double)kMinShortSide / shortSide));
                var info = new SKImageInfo(src.Width * scale, src.Height * scale, src.ColorType, src.AlphaType);
                working = new SKBitmap(info);
                src.ScalePixels(working, new SKSamplingOptions(SKCubicResampler.Mitchell));
            }

            try
            {
                // DoAngle=false / MostAngle=false：UI 截图场景几乎没有倒立文字，关掉省 ~30% 推理时间
                var options = RapidOcrOptions.Default with
                {
                    DoAngle = false,
                    MostAngle = false,
                };
                var result = engine.Detect(working, options);
                var text = (result.StrRes ?? string.Empty).Trim();
                sw.Stop();
                _log.Debug("ocr run",
                    ("id", Id),
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
            _log.Warn("ocr recognize failed", ("id", Id), ("err", ex.Message));
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
            var (detName, clsName, recName, keysName) = ModelFiles;
            var detPath = Path.Combine(PluginDir, detName);
            var clsPath = Path.Combine(PluginDir, clsName);
            var recPath = Path.Combine(PluginDir, recName);
            var keysPath = Path.Combine(PluginDir, keysName);

            foreach (var p in new[] { detPath, clsPath, recPath, keysPath })
            {
                if (!File.Exists(p))
                    throw new FileNotFoundException($"OCR 模型文件缺失: {Path.GetFileName(p)}", p);
            }

            var engine = new RapidOcr();
            engine.InitModels(detPath, clsPath, recPath, keysPath);
            _engine = engine;
            sw.Stop();
            _log.Info("ocr engine ready",
                ("id", Id), ("ms", sw.ElapsedMilliseconds), ("backend", "RapidOcrNet"));
            return _engine;
        }
        catch (Exception ex)
        {
            _initFailed = true;
            _initErrorMessage = ex.Message;
            _log.Error($"ocr engine init failed: {Id}", ex);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _engine?.Dispose(); } catch { }
        _engine = null;
        _gate.Dispose();
    }
}
