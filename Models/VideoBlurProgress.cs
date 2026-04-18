namespace VideoFrameExtractor.Models;

/// <summary>视频人脸涂抹的逐帧处理进度</summary>
public sealed class VideoBlurProgress
{
    public VideoBlurProgress(int processedFrames, int totalFrames, string? message = null)
    {
        ProcessedFrames = processedFrames;
        TotalFrames = totalFrames;
        Message = message;
    }

    public int ProcessedFrames { get; }
    public int TotalFrames { get; }

    /// <summary>可选的状态消息，用于区分特殊阶段（如"音频合并中"）</summary>
    public string? Message { get; }

    /// <summary>0–100 百分比；TotalFrames 未知时返回 -1</summary>
    public int Percentage =>
        TotalFrames > 0 ? (int)(ProcessedFrames * 100.0 / TotalFrames) : -1;

    public override string ToString() =>
        Message ?? (TotalFrames > 0
            ? $"正在处理第 {ProcessedFrames}/{TotalFrames} 帧 ({Percentage}%)"
            : $"正在处理第 {ProcessedFrames} 帧...");
}
