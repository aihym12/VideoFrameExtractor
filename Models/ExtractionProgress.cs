namespace VideoFrameExtractor.Models
{
    /// <summary>
    /// 抽帧进度模型
    /// </summary>
    public class ExtractionProgress
    {
        /// <summary>当前帧序号</summary>
        public int CurrentFrame { get; set; }

        /// <summary>总帧数</summary>
        public int TotalFrames { get; set; }

        /// <summary>完成百分比（0-100）</summary>
        public double Percentage { get; set; }

        /// <summary>剩余时间</summary>
        public TimeSpan TimeRemaining { get; set; }
    }
}
