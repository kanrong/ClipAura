# WeChat OCR — 本机微信后端

后端：[swigger/wechat-ocr](https://github.com/swigger/wechat-ocr) 编译出的 `wcocr.dll`，调用本机微信带的 OCR 服务（`WeChatOCR.exe` / `wxocr.dll`）。

## 重要：先弄清两个名字相近的 DLL

这两个 DLL 名字只差一个字母，**特别容易混淆**：

| DLL | 角色 | 来源 | 你需要拷贝吗？ |
|---|---|---|---|
| `wcocr.dll` | C# 调用层 (P/Invoke 包装) | swigger/wechat-ocr 编译产物 | **要**！必须放到 `plugins\ocr\wechat\wcocr.dll` |
| `wxocr.dll` | 微信自带的 OCR 后端 DLL | 微信 4.0 安装时下载到 `%APPDATA%` | **不用**，ClipAura 自动探测 |

> 简单说：你只需要管 `wcocr.dll` 一个文件，`wxocr.dll` 装了微信就有，ClipAura 启动时会自动从
> `%APPDATA%\Tencent\xwechat\XPlugin\plugins\WeChatOcr\{ver}\extracted\wxocr.dll`
> 找到它（支持多用户名、多版本号；探测结果会写到日志）。
>
> 为什么不直接用 `wxocr.dll`？因为它的接口是腾讯私有的 protobuf 协议，C# 不能直接 P/Invoke 调用 —
> `wcocr.dll` 就是 swigger 写的一层薄包装让它能被 C# 调用。

依赖条件：

1. 本机装了微信 3.x 或 4.0，且至少打开过一次（首启动微信才会下载 `wxocr.dll` 到 `%APPDATA%`）；
2. `wcocr.dll` 放到 `plugins\ocr\wechat\`。

## 安装步骤

### 1. 准备 `wcocr.dll`（你需要做的唯一事情）

到 [swigger/wechat-ocr Releases](https://github.com/swigger/wechat-ocr/releases) 下载最新版的 64-bit `wcocr.dll`。

**两个放置位置（按角色选）**：

| 你是谁 | 放在哪里 | 效果 |
| --- | --- | --- |
| **开发者** (本地编译运行 ClipAura) | `src/PopClip.App/plugins/ocr/wechat/wcocr.dll` | 每次 `dotnet build` 自动复制到 bin 输出 `plugins\ocr\wechat\wcocr.dll`；该文件被 `.gitignore` 排除不会进仓库 |
| **最终用户** (拿到打包好的 ClipAura.exe) | `<安装目录>\plugins\ocr\wechat\wcocr.dll` | 直接生效，重启 ClipAura 即可 |

> 路径完全一致 — README 里所有路径都指 `plugins\ocr\wechat\wcocr.dll`，开发态和运行态唯一区别只是前缀（`src/PopClip.App/` vs 安装目录）。
>
> 没有现成 release？按上游 README 用 CMake + VS 2022 自己编一份即可（项目结构小，~1 分钟）。

### 1b. publish / 打包时是否带上 wcocr.dll

跟 `plugins/ocr/rapid-onnx/runtime/` 是同一套语义：

- 想让安装包**默认就能用 WeChat OCR** → publish 前把 wcocr.dll 放进 `src/PopClip.App/plugins/ocr/wechat/`，发布脚本会自动带走；
- 想做"最小安装包"留给用户自己装 → 不要放，安装包默认只带 README，用户自己按上面的步骤补 dll；
- 想做"无 RapidOCR 版"（节省 ~25 MB） → publish 后手动删 `plugins/ocr/rapid-onnx/runtime/` 整个目录即可。

### 2. 微信路径自动探测（不用你做）

ClipAura 会按以下顺序自动找微信路径：

- **WeChat 4.0**：
  - `ocrExe` = `%APPDATA%\Tencent\xwechat\XPlugin\plugins\WeChatOcr\{ver}\extracted\wxocr.dll`
  - `wechatDir` = `C:\Program Files\Tencent\Weixin\{ver}\` （选最高版本子目录）
- **WeChat 3.x**：
  - `ocrExe` = `%APPDATA%\Tencent\WeChat\XPlugin\Plugins\WeChatOCR\{ver}\extracted\WeChatOCR.exe`
  - `wechatDir` = 注册表 `HKCU\Software\Tencent\WeChat\InstallPath` 或默认 `C:\Program Files (x86)\Tencent\WeChat`

只要本机微信至少打开过一次，自动探测就够用。

### 3. 显式覆盖（少数自动探测失败的场景）

如果你的微信装在非默认路径（绿色版 / 多开 / 自定义安装目录），在本目录创建 `paths.json`：

```json
{
  "ocrExe": "C:\\绿色版微信\\WeChat\\XPlugin\\Plugins\\WeChatOCR\\7061\\extracted\\WeChatOCR.exe",
  "wechatDir": "C:\\绿色版微信\\WeChat"
}
```

`paths.json` 优先级高于自动探测，路径不存在时会回退到自动探测。

## 在 ClipAura 中启用

`wcocr.dll` 就绪后：

- 设置 → OCR → provider 选择 `WeChat OCR`，或
- 保持"自动"模式 — 默认优先级 `RapidOCR (100) > WeChat (80)`，所以装了 RapidOCR plugin 时会用 RapidOCR。
  想让 WeChat 自动接管，把设置里的偏好显式选成 WeChat，或直接删除 `plugins/ocr/rapid-onnx/runtime/` 目录。

## 取舍对比

| 维度       | WeChat OCR             | RapidOCR        |
| -------- | ---------------------- | --------------- |
| 中文精度     | 极高（业界领先）               | 高 (~99%)        |
| 冷启动      | 1-2 秒（spawn 子进程）       | 0.5-1 秒         |
| 热识别      | 100-300 ms             | 300-600 ms      |
| 额外依赖     | 微信 + wcocr.dll         | RapidOCR plugin (~25 MB) |
| 内存占用     | ~80 MB (含子进程)          | ~30 MB          |
| 离线 / 隐私  | 完全离线，但子进程是腾讯私有二进制      | 完全离线，全开源        |

## 已知限制

- WeChat 4.0 第一次启动后插件下载有延迟，要等微信弹一次"OCR 插件已就绪"再用。
- 不要在 OCR 进行中关闭微信主进程，会让 `wcocr.dll` 的 IPC 通道断掉。
- 应用退出时 ClipAura 会调用 `stop_ocr()` 终止子进程，正常情况无需手动清理。
