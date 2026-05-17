# RapidOCR (PP-OCRv5) — 可选 OCR 后端

后端：[RapidOcrNet](https://github.com/BobLd/RapidOcrNet) (Apache-2.0) + [PaddleOCR PP-OCRv5](https://github.com/PaddlePaddle/PaddleOCR) 中英文 mobile 模型。

模型源：[RapidAI/RapidOCR](https://www.modelscope.cn/models/RapidAI/RapidOCR) v3.8.0。

## 文件清单

### 运行时 (~25 MB, `runtime/` 子目录)

主程序构建时自动生成，包含 RapidOcrNet / ONNX Runtime / SkiaSharp 等 native 与 managed dll：

| 文件 | 大小 |
| --- | --- |
| `runtime/PopClip.App.OcrProvider.RapidOnnx.dll` | 12 KB |
| `runtime/RapidOcrNet.dll` | 70 KB |
| `runtime/Microsoft.ML.OnnxRuntime.dll` | 220 KB |
| `runtime/SkiaSharp.dll` | 470 KB |
| `runtime/onnxruntime.dll` | 13.5 MB |
| `runtime/libSkiaSharp.dll` | 10.9 MB |
| `runtime/onnxruntime_providers_shared.dll` | 22 KB |
| `runtime/PopClip.App.OcrProvider.RapidOnnx.deps.json` | < 1 KB |
| `runtime/Clipper2Lib.dll` / `System.Numerics.Tensors.dll` | RapidOcrNet 依赖 |

**想精简包体？直接把整个 `runtime/` 目录删了**，主程序仍正常启动，OCR 切换到 WeChat 后端。

### 模型 (~21 MB, `v5/` 子目录)

| 文件 | 用途 | 大小 |
| --- | --- | --- |
| `v5/ch_PP-OCRv5_det_mobile.onnx` | DBNet 文本框检测 | 4.6 MB |
| `v5/ch_ppocr_mobile_v2.0_cls_infer.onnx` | 180° 翻转分类 | 0.6 MB |
| `v5/ch_PP-OCRv5_rec_mobile.onnx` | CRNN 中英文识别（含 ASCII） | 15.9 MB |
| `v5/ppocrv5_dict.txt` | recognizer 字典 | 72 KB |

模型文件随 ClipAura.exe 一起分发，正常安装后无需任何手动操作。

## 工作原理

ClipAura 启动时由 `OcrPluginLoader` 扫描 `plugins/ocr/*/runtime/PopClip.App.OcrProvider.*.dll`，用隔离的 `AssemblyLoadContext` 加载（`AssemblyDependencyResolver` 解析 plugin 自带的 `.deps.json` 找所有 transitive 与 native 依赖）。

之后 plugin 通过 `IOcrProvider` 接口（来自共享的 `PopClip.App.Ocr.Abstractions.dll`）注册到主程序的 `OcrProviderRegistry`，跟其它 provider 平起平坐。

## 损坏 / 缺失自检

| 缺失内容 | 启动日志 |
| --- | --- |
| 整个 `runtime/` 目录 | `ocr plugin root absent` / 注册表里没有 rapid-onnx |
| `runtime/*.dll` 但 deps.json 损坏 | `ocr plugin load failed` 含具体错误 |
| `runtime/` 完整但 `v5/*.onnx` 缺失 | `ocr provider registered (unavailable) id=rapid-onnx reason=缺少模型文件...` |

修复模型：从 [modelscope](https://www.modelscope.cn/models/RapidAI/RapidOCR/files) 重新下载 4 个文件放回 `plugins/ocr/rapid-onnx/v5/`。
修复 runtime：重新构建 ClipAura（`dotnet build` 会自动重生）。

## 性能

- 冷启动：~500 ms-1 秒（首次 OCR 时加载 3 个 ONNX session）
- 热识别：300-600 ms / 张
- 内存占用：~30 MB

## 切换到其他 provider

设置 → OCR → 选择 `WeChat OCR`（详见 `../wechat/README.md`）。

如果完全不想要 RapidOCR，直接删 `plugins/ocr/rapid-onnx/runtime/` 目录即可，主程序会自动 fallback 到 WeChat。
