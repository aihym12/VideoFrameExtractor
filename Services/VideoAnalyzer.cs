using System.IO;
using VideoFrameExtractor.Helpers;
using VideoFrameExtractor.Models;
using Xabe.FFmpeg;

namespace VideoFrameExtractor.Services
{
    /// <summary>
    /// 视频信息解析服务
    /// </summary>
    public class VideoAnalyzer
    {
        /// <summary>
        /// 异步解析视频文件信息
        /// </summary>
        public async Task<VideoInfo> AnalyzeAsync(string filePath, CancellationToken cancellationToken = default)
        {
            Logger.Info($"开始解析视频信息: {filePath}");

            if (!File.Exists(filePath))
                throw new FileNotFoundException("视频文件不存在", filePath);

            if (!PathHelper.IsSupportedVideoFormat(filePath))
                throw new NotSupportedException($"不支持的视频格式: {Path.GetExtension(filePath)}");

            var mediaInfo = await FFmpeg.GetMediaInfo(filePath, cancellationToken);
            var videoStream = mediaInfo.VideoStreams.FirstOrDefault()
                ?? throw new InvalidOperationException("无法找到视频流");

            var info = new VideoInfo
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                Duration = mediaInfo.Duration,
                Width = videoStream.Width,
                Height = videoStream.Height,
                FrameRate = Math.Round(videoStream.Framerate, 2),
                FileSize = new FileInfo(filePath).Length,
                Format = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant()
            };

            Logger.Info($"视频解析完成: {info.Resolution}, {info.FrameRateText}, {info.DurationText}");
            return info;
        }
    }
}
