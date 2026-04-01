namespace VideoFrameExtractor.Models
{
    /// <summary>
    /// 抽帧结果模型
    /// </summary>
    public class ExtractionResult
    {
        /// <summary>是否成功</summary>
        public bool Success { get; set; }

        /// <summary>输出文件夹路径</summary>
        public string OutputFolder { get; set; } = string.Empty;

        /// <summary>提取的帧数</summary>
        public int FramesExtracted { get; set; }

        /// <summary>耗时</summary>
        public TimeSpan Duration { get; set; }

        /// <summary>错误消息</summary>
        public string? ErrorMessage { get; set; }
    }
}
