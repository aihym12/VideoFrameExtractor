namespace VideoFrameExtractor.Models;

/// <summary>
/// 帧率模式
/// </summary>
public enum FrameRateMode
{
    Original = 0,
    Fixed = 1,
    Smart = 2
}

/// <summary>
/// 文件命名模式
/// </summary>
public enum NamingPattern
{
    Sequential = 0,
    VideoName = 1,
    Timestamp = 2
}

/// <summary>
/// GPU 加速模式
/// </summary>
public enum GpuAccelerationMode
{
    Auto = 0,
    Cuda = 1,
    Qsv = 2,
    AmdAmf = 3
}

/// <summary>
/// 抽帧配置
/// </summary>
public class ExtractionSettings
{
    public double StartTime { get; set; } = 0;
    public double EndTime { get; set; } = 15;
    public FrameRateMode FrameRateMode { get; set; } = FrameRateMode.Fixed;
    public double FixedFps { get; set; } = 10;
    public string Format { get; set; } = "jpg";
    public int Quality { get; set; } = 85;
    public NamingPattern NamingPattern { get; set; } = NamingPattern.Sequential;
    public string OutputPath { get; set; } = string.Empty;
    public bool UseGpuAcceleration { get; set; }
    public GpuAccelerationMode GpuMode { get; set; } = GpuAccelerationMode.Auto;
    public bool AutoStart { get; set; }
    public bool OpenFolderWhenDone { get; set; } = true;
}
