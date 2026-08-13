# ClipAura

Windows 选区动作工具。划选文字后弹出浮窗，复制、搜索、翻译、OCR、截图和 AI 都从这里走。

类似 macOS 上的 [PopClip](https://www.popclip.app/)，面向 Windows 10 / 11。

## 功能

- **选区浮窗**：用 UIA 读取选中文本，失败时回退到剪贴板。可配置弹出时机、修饰键和进程黑白名单。
- **内置动作**：复制、粘贴、打开链接、搜索、翻译、计算、字数、剪贴板历史。
- **智能动作**：只在文本匹配时出现，例如 JSON 格式化、颜色码、时间戳、文件路径、Markdown / CSV / TSV 互转、离线查词。
- **区域 OCR**：框选屏幕文字。默认 RapidOCR（PP-OCRv5），可选本机微信 OCR。
- **区域截图**：预览、标注、复制、自动保存、钉到桌面；也可对截图再跑 OCR。
- **AI**：对接 DeepSeek / OpenAI 兼容接口。翻译、解释、对话、自定义 Prompt。Key 用 DPAPI 加密存放。
- **剪贴板历史**：文本和图片都记入本地 SQLite，可回看、粘贴、对图片 OCR。

## 系统要求

- Windows 10 1809（build 17763）或更高
- x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（从源码编译时）

## 从源码运行

```powershell
dotnet build PopClip.Win.slnx -c Release
dotnet run --project src/PopClip.App/PopClip.App.csproj -c Release
```

输出在 `src/PopClip.App/bin/Release/net10.0-windows/win-x64/`。日常使用可把 `ClipAura.exe` 放到开始菜单或打开「开机启动」。

第一次启动会打开设置窗口。之后从托盘图标进入设置、暂停、OCR 或截图。

## 默认快捷键

| 功能 | 快捷键 |
| --- | --- |
| 暂停 / 恢复 | `Ctrl+Alt+P` |
| 在光标处唤起浮窗 | `Ctrl+Alt+Space` |
| 区域 OCR | `Ctrl+Alt+O` |
| 区域截图 | `Ctrl+Alt+S` |
| 延时截图 | `Ctrl+Alt+Shift+T` |

可在 **设置 → 快捷键** 里改。冲突时设置页会标出注册失败原因。

划选弹出默认需要按住 **Alt**。可在 **设置 → 常规** 改成立即弹出、延迟弹出或换修饰键。

## OCR

安装包默认带 RapidOCR（PP-OCRv5 中英文模型，约 21 MB + 25 MB 运行时）。一般不用再装东西。

想换微信 OCR（中文更准，需要本机微信）：

1. 从 [swigger/wechat-ocr](https://github.com/swigger/wechat-ocr/releases) 下载 64 位 `wcocr.dll`
2. 放到 `<安装目录>\plugins\ocr\wechat\wcocr.dll`
3. 打开过一次微信，再在 **设置 → OCR** 里选 WeChat

精简体积：删掉 `plugins\ocr\rapid-onnx\runtime\`，程序会回退到微信后端。

详细步骤见：

- [`src/PopClip.App/plugins/ocr/rapid-onnx/README.md`](src/PopClip.App/plugins/ocr/rapid-onnx/README.md)
- [`src/PopClip.App/plugins/ocr/wechat/README.md`](src/PopClip.App/plugins/ocr/wechat/README.md)

## AI

1. 打开 **设置 → AI**，打开「启用 AI」
2. 选 DeepSeek、OpenAI 或自定义兼容接口
3. 填入 API Key（只存在本机，用 Windows DPAPI 保护）
4. 点「测试连接」

启用后，浮窗「翻译」默认走内联气泡；也可加「AI 对话」「AI 解释」和自己的 Prompt 模板。

## 离线词典

「查词」「词汇解析」需要 ECDICT SQLite。把 `ecdict.sqlite` 放到：

```text
<安装目录>\plugins\dict\ecdict\ecdict.sqlite
```

开发时可用 `python tools/import_ecdict.py` 生成，说明见 [`src/PopClip.App/plugins/dict/ecdict/README.md`](src/PopClip.App/plugins/dict/ecdict/README.md)。

## 配置与数据

都在当前用户目录，不写安装目录：

```text
%LOCALAPPDATA%\ClipAura\settings.json
%LOCALAPPDATA%\ClipAura\actions.json
%LOCALAPPDATA%\ClipAura\history.db
```

设置窗口的「关于」页可以打开日志目录和配置目录。

## 项目结构

| 项目 | 作用 |
| --- | --- |
| `PopClip.App` | WPF 宿主、设置、浮窗、OCR 编排 |
| `PopClip.Core` | 选区状态机、动作契约 |
| `PopClip.Hooks` | 低级键鼠钩子、前台窗口 |
| `PopClip.Uia` | UI Automation 读改选区、剪贴板 |
| `PopClip.Actions.BuiltIn` | 内置 / 智能动作 |
| `PopClip.App.Ocr.Abstractions` | OCR 插件接口 |
| `PopClip.App.OcrProvider.RapidOnnx` | RapidOCR 插件（按需加载） |
| `PopClip.Ocr.Layout` | OCR 版面整理 |

贡献代码时请看 [AGENTS.md](AGENTS.md) 里的换行约定。
