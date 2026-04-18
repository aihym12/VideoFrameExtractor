namespace VideoFrameExtractor.Models;

/// <summary>
/// ONNX 推理计算设备
/// </summary>
public enum OnnxDevice
{
    /// <summary>CPU 推理（默认，兼容所有设备）</summary>
    Cpu = 0,

    /// <summary>GPU 推理，使用 DirectML（支持 NVIDIA / AMD / Intel 显卡）</summary>
    DirectML = 1
}
