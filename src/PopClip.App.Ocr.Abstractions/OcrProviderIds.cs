namespace PopClip.App.Ocr;

/// <summary>provider id 常量，统一定义避免散落字符串拼写错误。
/// id 与 plugins/ocr/{id}/ 目录名一致，也写到 AppSettings.OcrProviderId 里持久化。</summary>
public static class OcrProviderIds
{
    /// <summary>RapidOcrNet + PP-OCRv5 ONNX 模型，作为可选 plugin 单独分发到 plugins/ocr/rapid-onnx/runtime/。</summary>
    public const string RapidOnnx = "rapid-onnx";

    /// <summary>swigger/wechat-ocr：调用本机微信带的 WeChatOCR.exe 后端，精度高但依赖用户装了微信。</summary>
    public const string WeChat = "wechat";
}
