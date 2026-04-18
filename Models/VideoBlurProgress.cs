namespace VideoFrameExtractor.Models;

/// <summary>视频人脸涂抹的逐帧处理进度</summary>
public sealed class VideoBlurProgress
{
    public VideoBlurProgress(int processedFrames, int totalFrames)
    {
        ProcessedFrames = processedFrames;
        TotalFrames = totalFrames;
    }

    public int ProcessedFrames { get; }
    public int TotalFrames { get; }

    /// <summary>0–100 百分比；TotalFrames 未知时返回 -1</summary>
    public int Percentage =>
        TotalFrames > 0 ? (int)(ProcessedFrames * 100.0 / TotalFrames) : -1;

    public override string ToString() =>
        TotalFrames > 0
            ? $"正在处理第 {ProcessedFrames}/{TotalFrames} 帧 ({Percentage}%)"
            : $"正在处理第 {ProcessedFrames} 帧...";
}
