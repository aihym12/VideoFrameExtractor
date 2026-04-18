using System.Diagnostics;
using System.IO;
using OpenCvSharp;
using VideoFrameExtractor.Helpers;
using VideoFrameExtractor.Models;

namespace VideoFrameExtractor.Services;

/// <summary>
/// 视频人脸涂抹服务：逐帧 BiSeNet 检测 + 涂抹，输出带原始音频的新视频。
/// </summary>
public sealed class VideoFaceBlurService : IDisposable
{
    private const double MinFaceMaskRatio = FaceBlurConstants.MinFaceMaskRatio;

    private BiSeNetFaceParser? _parser;
    private OnnxDevice _currentDevice = OnnxDevice.Cpu;
    private bool _disposed;

    // ── 公共 API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 对输入视频做人脸涂抹后输出新视频。
    /// 音频轨道使用 FFmpeg 从原始视频复制（如果原始视频含音频）。
    /// </summary>
    public async Task BlurVideoAsync(
        string inputPath,
        string outputPath,
        FaceBlurSettings settings,
        IProgress<VideoBlurProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("源视频文件不存在。", inputPath);

        // 确保解析器与设备匹配
        if (_parser == null || _currentDevice != settings.InferenceDevice)
        {
            _parser?.Dispose();
            _parser = new BiSeNetFaceParser(settings.InferenceDevice);
            _currentDevice = settings.InferenceDevice;
        }

        string outputDir = Path.GetDirectoryName(outputPath) ?? ".";
        Directory.CreateDirectory(outputDir);

        // 临时无音频视频文件（放在系统 temp 目录，防止磁盘跨分区移动问题）
        string tempVideoPath = Path.Combine(Path.GetTempPath(), $"vfe_blur_{Guid.NewGuid():N}.mp4");

        try
        {
            // 1. 逐帧处理，写入临时无音频视频
            await Task.Run(() => ProcessFrames(inputPath, tempVideoPath, settings, progress, cancellationToken),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            // 2. 用 FFmpeg 把原始音频合并到输出视频
            progress?.Report(new VideoBlurProgress(0, 0, "正在合并音频...")); // 音频合并阶段
            await MergeAudioAsync(inputPath, tempVideoPath, outputPath, cancellationToken);
        }
        finally
        {
            if (File.Exists(tempVideoPath))
            {
                try { File.Delete(tempVideoPath); }
                catch (Exception ex) { Logger.Warn($"清理临时文件失败: {ex.Message}"); }
            }
        }
    }

    // ── 逐帧处理 ─────────────────────────────────────────────────────────────

    private void ProcessFrames(
        string inputPath,
        string outputPath,
        FaceBlurSettings settings,
        IProgress<VideoBlurProgress>? progress,
        CancellationToken ct)
    {
        using var capture = new VideoCapture(inputPath);
        if (!capture.IsOpened())
            throw new InvalidOperationException($"无法打开视频文件: {inputPath}");

        double fps = capture.Get(VideoCaptureProperties.Fps);
        int width  = (int)capture.Get(VideoCaptureProperties.FrameWidth);
        int height = (int)capture.Get(VideoCaptureProperties.FrameHeight);
        int totalFrames = (int)capture.Get(VideoCaptureProperties.FrameCount);
        if (fps <= 0) fps = 25;

        // mp4v 在 mp4 容器中兼容性最好
        int fourcc = VideoWriter.FourCC('m', 'p', '4', 'v');
        using var writer = new VideoWriter(outputPath, fourcc, fps, new Size(width, height));
        if (!writer.IsOpened())
            throw new InvalidOperationException($"无法创建视频写入器: {outputPath}");

        using var frame = new Mat();
        int processed = 0;

        while (capture.Read(frame) && !frame.Empty())
        {
            ct.ThrowIfCancellationRequested();

            // BiSeNet 推理 + 涂抹
            using var mask = _parser!.GetFaceMask(frame, settings.Sensitivity);
            int facePixels = Cv2.CountNonZero(mask);
            if (facePixels >= frame.Rows * frame.Cols * MinFaceMaskRatio)
                ApplyFaceBlurByMask(frame, mask, settings);

            writer.Write(frame);
            processed++;

            if (processed % 5 == 0 || processed == totalFrames)
                progress?.Report(new VideoBlurProgress(processed, totalFrames));
        }
    }

    // ── FFmpeg 音频合并 ───────────────────────────────────────────────────────

    private static async Task MergeAudioAsync(
        string sourceVideoPath,
        string blurredVideoPath,
        string outputPath,
        CancellationToken ct)
    {
        // 获取 FFmpeg 可执行文件路径
        string ffmpegExe = FindFfmpegExe();
        if (string.IsNullOrWhiteSpace(ffmpegExe) || !File.Exists(ffmpegExe))
        {
            // FFmpeg 不可用：直接把无音频视频复制为最终输出
            Logger.Warn("FFmpeg 未找到，输出视频将不含音频。");
            File.Copy(blurredVideoPath, outputPath, overwrite: true);
            return;
        }

        // 尝试合并音频；若源视频无音频轨，直接复制
        // ffmpeg -i blurred.mp4 -i source.mp4 -c:v copy -c:a aac -map 0:v:0 -map 1:a:0 -shortest -y output.mp4
        string args = $"-i \"{blurredVideoPath}\" -i \"{sourceVideoPath}\" " +
                      $"-c:v copy -c:a aac -map 0:v:0 -map 1:a:0 -shortest -y \"{outputPath}\"";

        var psi = new ProcessStartInfo(ffmpegExe, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 FFmpeg 进程。");
        string stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            // 合并失败（可能无音频轨）→ 直接复制无音频版本
            Logger.Warn($"FFmpeg 音频合并失败（可能源视频无音频轨），输出无音频视频。FFmpeg stderr: {stderr}");
            File.Copy(blurredVideoPath, outputPath, overwrite: true);
        }
    }

    private static string FindFfmpegExe()
    {
        string ffmpegDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg");
        if (!Directory.Exists(ffmpegDir)) return string.Empty;
        return Directory.EnumerateFiles(ffmpegDir, "ffmpeg.exe", SearchOption.AllDirectories)
            .FirstOrDefault() ?? string.Empty;
    }

    // ── 涂抹逻辑（委托 FaceBlurHelper）────────────────────────────────────────

    private static void ApplyFaceBlurByMask(Mat image, Mat mask, FaceBlurSettings settings) =>
        FaceBlurHelper.ApplyFaceBlurByMask(image, mask, settings);

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _parser?.Dispose();
        _parser = null;
    }
}
