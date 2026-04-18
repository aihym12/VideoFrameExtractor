namespace VideoFrameExtractor.Models;

/// <summary>
/// 人脸涂抹相关的共享常量
/// </summary>
public static class FaceBlurConstants
{
    /// <summary>
    /// 掩膜有效像素占总像素的最低比例。
    /// 低于此值视为"无人脸"，跳过该帧/图片。
    /// </summary>
    public const double MinFaceMaskRatio = 0.001;
}
