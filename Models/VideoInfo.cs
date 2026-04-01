namespace VideoFrameExtractor.Models
{
    /// <summary>
    /// 视频文件信息模型
    /// </summary>
    public class VideoInfo
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public TimeSpan Duration { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double FrameRate { get; set; }
        public long FileSize { get; set; }
        public string Format { get; set; } = string.Empty;

        /// <summary>
        /// 分辨率文本
        /// </summary>
        public string Resolution => $"{Width}x{Height}";

        /// <summary>
        /// 时长文本
        /// </summary>
        public string DurationText => $"{Duration:hh\\:mm\\:ss}";

        /// <summary>
        /// 文件大小文本
        /// </summary>
        public string FileSizeText => FormatBytes(FileSize);

        /// <summary>
        /// 帧率文本
        /// </summary>
        public string FrameRateText => $"{FrameRate:F2} FPS";

        private static string FormatBytes(long bytes)
        {
            string[] sizes = ["B", "KB", "MB", "GB"];
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:0.##} {sizes[order]}";
        }
    }
}
