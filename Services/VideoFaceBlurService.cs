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
    private const double MinFaceMaskRatio = 0.001;
    private const int FeatherKernelSize = 21;
    private const double FeatherSigma = 8.0;

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

        // 临时无音频视频文件
        string tempVideoPath = Path.Combine(outputDir, $"_tmp_blur_{Guid.NewGuid():N}.mp4");

        try
        {
            // 1. 逐帧处理，写入临时无音频视频
            await Task.Run(() => ProcessFrames(inputPath, tempVideoPath, settings, progress, cancellationToken),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            // 2. 用 FFmpeg 把原始音频合并到输出视频
            progress?.Report(new VideoBlurProgress(0, 0)); // 音频合并阶段
            await MergeAudioAsync(inputPath, tempVideoPath, outputPath, cancellationToken);
        }
        finally
        {
            if (File.Exists(tempVideoPath))
            {
                try { File.Delete(tempVideoPath); } catch { /* 忽略清理错误 */ }
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

    // ── 涂抹逻辑（与 FaceBlurService 共享，避免依赖） ────────────────────────

    private static void ApplyFaceBlurByMask(Mat image, Mat mask, FaceBlurSettings settings)
    {
        using var softMask = CreateSoftMask(mask);
        if (settings.BlurMode == FaceBlurMode.Mosaic)
            ApplyMosaicByMask(image, mask, softMask, settings.BlurStrength);
        else
            ApplyGaussianByMask(image, softMask, settings.BlurStrength);
    }

    private static void ApplyGaussianByMask(Mat image, Mat softMask, int strength)
    {
        int minDim = Math.Min(image.Rows, image.Cols);
        int baseKernel = Math.Max(3, (int)(minDim * (0.05 + strength / 100.0 * 0.15)));
        int kernel = baseKernel % 2 == 0 ? baseKernel + 1 : baseKernel;
        using var blurred = new Mat();
        Cv2.GaussianBlur(image, blurred, new Size(kernel, kernel), 0);
        BlendByMask(image, blurred, softMask);
    }

    private static void ApplyMosaicByMask(Mat image, Mat binaryMask, Mat softMask, int strength)
    {
        using var pts = new Mat();
        Cv2.FindNonZero(binaryMask, pts);
        if (pts.Total() == 0) return;
        Rect bbox = ClampRect(Cv2.BoundingRect(pts), image.Size());
        if (bbox.Width <= 0 || bbox.Height <= 0) return;

        int minDim = Math.Min(bbox.Width, bbox.Height);
        int blockSize = Math.Max(2, (int)(minDim * (0.05 + strength / 100.0 * 0.25)));

        using var mosaicFull = image.Clone();
        using var faceRoi = new Mat(image, bbox);
        using var small = new Mat();
        Cv2.Resize(faceRoi, small,
            new Size(Math.Max(1, bbox.Width / blockSize), Math.Max(1, bbox.Height / blockSize)),
            interpolation: InterpolationFlags.Linear);
        using var mosaic = new Mat();
        Cv2.Resize(small, mosaic, new Size(bbox.Width, bbox.Height), interpolation: InterpolationFlags.Nearest);
        mosaic.CopyTo(new Mat(mosaicFull, bbox));
        BlendByMask(image, mosaicFull, softMask);
    }

    private static void BlendByMask(Mat image, Mat effect, Mat softMask)
    {
        using var softMask3 = new Mat();
        Cv2.Merge([softMask, softMask, softMask], softMask3);
        using var imageF = new Mat();
        using var effectF = new Mat();
        image.ConvertTo(imageF, MatType.CV_32FC3);
        effect.ConvertTo(effectF, MatType.CV_32FC3);
        using var diff = new Mat();
        Cv2.Subtract(effectF, imageF, diff);
        using var blended = new Mat();
        Cv2.Multiply(diff, softMask3, blended);
        Cv2.Add(imageF, blended, blended);
        blended.ConvertTo(image, MatType.CV_8UC3);
    }

    private static Mat CreateSoftMask(Mat binaryMask)
    {
        var soft = new Mat();
        binaryMask.ConvertTo(soft, MatType.CV_32F, 1.0 / 255.0);
        Cv2.GaussianBlur(soft, soft, new Size(FeatherKernelSize, FeatherKernelSize), FeatherSigma);
        return soft;
    }

    private static Rect ClampRect(Rect rect, Size sz)
    {
        int x = Math.Max(0, rect.X);
        int y = Math.Max(0, rect.Y);
        int r = Math.Min(sz.Width, rect.X + rect.Width);
        int b = Math.Min(sz.Height, rect.Y + rect.Height);
        return new Rect(x, y, Math.Max(0, r - x), Math.Max(0, b - y));
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _parser?.Dispose();
        _parser = null;
    }
}
