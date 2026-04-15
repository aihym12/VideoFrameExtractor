namespace VideoFrameExtractor.Models;

/// <summary>
/// 人脸涂抹方式
/// </summary>
public enum FaceBlurMode
{
    /// <summary>
    /// 高斯模糊
    /// </summary>
    Gaussian = 0,

    /// <summary>
    /// 马赛克（像素化）
    /// </summary>
    Mosaic = 1
}

/// <summary>
/// 人脸检测灵敏度
/// </summary>
public enum FaceDetectionSensitivity
{
    /// <summary>
    /// 低灵敏度（减少误检）
    /// </summary>
    Low = 0,

    /// <summary>
    /// 中等灵敏度（默认）
    /// </summary>
    Medium = 1,

    /// <summary>
    /// 高灵敏度（检出更多人脸，可能有误检）
    /// </summary>
    High = 2
}

/// <summary>
/// 人脸涂抹配置
/// </summary>
public class FaceBlurSettings
{
    /// <summary>
    /// 涂抹方式（高斯模糊 / 马赛克）
    /// </summary>
    public FaceBlurMode BlurMode { get; set; } = FaceBlurMode.Gaussian;

    /// <summary>
    /// 涂抹强度（1~100），值越大涂抹效果越强
    /// </summary>
    public int BlurStrength { get; set; } = 50;

    /// <summary>
    /// 人脸检测灵敏度
    /// </summary>
    public FaceDetectionSensitivity Sensitivity { get; set; } = FaceDetectionSensitivity.Medium;

    /// <summary>
    /// 抽帧完成后自动执行人脸涂抹
    /// </summary>
    public bool AutoBlurAfterExtraction { get; set; }
}
