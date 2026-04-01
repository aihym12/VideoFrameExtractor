using System.Diagnostics;
using System.IO;
using System.Globalization;
using VideoFrameExtractor.Helpers;
using VideoFrameExtractor.Models;
using Xabe.FFmpeg;

namespace VideoFrameExtractor.Services
{
    /// <summary>
    /// 核心帧提取服务
    /// </summary>
    public class FrameExtractor
    {
        /// <summary>
        /// 异步提取视频帧
        /// </summary>
        public async Task<ExtractionResult> ExtractFramesAsync(
            VideoInfo videoInfo,
            ExtractionSettings settings,
            IProgress<ExtractionProgress>? progress,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();

            // 确定输出目录
            string outputFolder = string.IsNullOrWhiteSpace(settings.OutputPath)
                ? PathHelper.GetDefaultOutputFolder(videoInfo.FilePath)
                : settings.OutputPath;

            PathHelper.EnsureDirectoryExists(outputFolder);
            Logger.Info($"输出目录: {outputFolder}");

            // 检查写入权限
            if (!PathHelper.HasWritePermission(outputFolder))
            {
                throw new UnauthorizedAccessException($"没有写入权限: {outputFolder}");
            }

            // 计算帧率
            double fps = settings.FrameRateMode switch
            {
                FrameRateMode.Original => videoInfo.FrameRate,
                FrameRateMode.Fixed => settings.FixedFps,
                FrameRateMode.Smart => CalculateSmartFps(settings.EndTime - settings.StartTime),
                _ => 10
            };

            int estimatedFrames = (int)Math.Ceiling((settings.EndTime - settings.StartTime) * fps);
            Logger.Info($"帧率: {fps} FPS, 预计帧数: {estimatedFrames}");

            // 构建文件名模式
            string filePattern = settings.NamingPattern switch
            {
                NamingPattern.Sequential => "frame_%04d",
                NamingPattern.VideoName => $"{SanitizeFileName(Path.GetFileNameWithoutExtension(videoInfo.FileName))}_%04d",
                NamingPattern.Timestamp => "frame_%04d",
                _ => "frame_%04d"
            };

            string ext = settings.Format.ToLowerInvariant();
            string outputPath = Path.Combine(outputFolder, $"{filePattern}.{ext}");

            // 构建 FFmpeg 转换
            var conversion = new Conversion();

            // 设置起始时间与输入文件
            conversion.SetSeek(TimeSpan.FromSeconds(settings.StartTime));
            conversion.AddParameter($"-i \"{videoInfo.FilePath}\"", ParameterPosition.PreInput);

            // GPU 加速
            if (settings.UseGpuAcceleration)
            {
                conversion.AddParameter($"-hwaccel {MapHwAccel(settings.GpuMode)}", ParameterPosition.PreInput);
                Logger.Info($"已启用 GPU 硬件加速，模式: {settings.GpuMode}");
            }

            // 设置持续时间
            double duration = settings.EndTime - settings.StartTime;
            conversion.SetOutputTime(TimeSpan.FromSeconds(duration));

            // 设置帧率滤镜
            conversion.AddParameter($"-vf \"fps={fps.ToString("F2", CultureInfo.InvariantCulture)}\"");

            // JPG 质量设置
            if (ext == "jpg" || ext == "jpeg")
            {
                int qv = CalculateQualityParameter(settings.Quality);
                conversion.AddParameter($"-q:v {qv}");
            }

            // PNG 无损
            if (ext == "png")
            {
                conversion.AddParameter("-compression_level 3");
            }

            // 覆盖输出
            conversion.SetOverwriteOutput(true);
            conversion.SetOutput(outputPath);

            // 进度回调
            conversion.OnProgress += (sender, args) =>
            {
                int currentFrame = (int)(estimatedFrames * args.Percent / 100.0);
                progress?.Report(new ExtractionProgress
                {
                    CurrentFrame = Math.Min(currentFrame, estimatedFrames),
                    TotalFrames = estimatedFrames,
                    Percentage = args.Percent,
                    TimeRemaining = args.TotalLength - args.Duration
                });
            };

            // 执行转换
            Logger.Info($"开始执行 FFmpeg 命令: {conversion.Build()}");

            try
            {
                await conversion.Start(cancellationToken);

                if (settings.NamingPattern == NamingPattern.Timestamp)
                {
                    await RenameFramesToTimestampAsync(outputFolder, ext, settings.StartTime, fps, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Info("用户取消了帧提取操作");
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error("FFmpeg 执行失败", ex);
                throw;
            }

            stopwatch.Stop();

            // 统计实际输出文件数
            int actualFrames = await CountFramesAsync(outputFolder, ext, cancellationToken);

            var result = new ExtractionResult
            {
                Success = true,
                OutputFolder = outputFolder,
                FramesExtracted = actualFrames,
                Duration = stopwatch.Elapsed
            };

            Logger.Info($"帧提取完成: {result.FramesExtracted} 帧, 耗时 {result.Duration.TotalSeconds:F1} 秒");
            return result;
        }

        /// <summary>
        /// 智能帧率推荐算法
        /// </summary>
        public static double CalculateSmartFps(double extractSeconds)
        {
            if (extractSeconds <= 10) return 30;
            if (extractSeconds <= 30) return 15;
            if (extractSeconds <= 60) return 10;
            return 5;
        }

        /// <summary>
        /// 预估输出帧数
        /// </summary>
        public static int EstimateFrameCount(double durationSeconds, double fps)
        {
            return (int)Math.Ceiling(durationSeconds * fps);
        }

        /// <summary>
        /// 预估输出大小（粗略估算）
        /// </summary>
        public static long EstimateOutputSize(int frameCount, int width, int height, string format, int quality)
        {
            // 粗略估算每帧大小
            double bytesPerPixel = format.ToLowerInvariant() switch
            {
                "png" => 1.5,   // PNG 无损，较大
                _ => 0.1 + (quality / 100.0) * 0.4  // JPG 根据质量估算
            };

            long bytesPerFrame = (long)(width * height * bytesPerPixel);
            return bytesPerFrame * frameCount;
        }

        /// <summary>
        /// 将用户质量（0-100）转换为 FFmpeg -q:v 参数（2-31）
        /// </summary>
        private static int CalculateQualityParameter(int quality)
        {
            // FFmpeg -q:v: 2=最佳, 31=最差
            // 用户输入: 0=最差, 100=最佳
            return Math.Clamp((int)(31 - (quality / 100.0 * 29)), 2, 31);
        }

        /// <summary>
        /// 清理文件名中的非法字符
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            foreach (char c in invalidChars)
            {
                name = name.Replace(c, '_');
            }
            return name;
        }

        private static string MapHwAccel(GpuAccelerationMode mode)
        {
            return mode switch
            {
                GpuAccelerationMode.Cuda => "cuda",
                GpuAccelerationMode.Qsv => "qsv",
                GpuAccelerationMode.AmdAmf => "d3d11va",
                _ => "auto"
            };
        }

        private static async Task<int> CountFramesAsync(string outputFolder, string ext, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (ext == "jpg" || ext == "jpeg")
                {
                    return Directory.EnumerateFiles(outputFolder, "*.jpg").Count()
                         + Directory.EnumerateFiles(outputFolder, "*.jpeg").Count();
                }

                return Directory.EnumerateFiles(outputFolder, $"*.{ext}").Count();
            }, cancellationToken);
        }

        private static async Task RenameFramesToTimestampAsync(
            string outputFolder,
            string ext,
            double startTime,
            double fps,
            CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                var files = Directory.EnumerateFiles(outputFolder, $"*.{ext}")
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                for (int i = 0; i < files.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    double seconds = startTime + (i / Math.Max(0.0001, fps));
                    TimeSpan ts = TimeSpan.FromSeconds(seconds);
                    string timestamp = ts.ToString("hh\\-mm\\-ss\\.fff", CultureInfo.InvariantCulture);
                    string targetFile = Path.Combine(outputFolder, $"frame_{timestamp}.{ext}");

                    if (!string.Equals(files[i], targetFile, StringComparison.OrdinalIgnoreCase) && !File.Exists(targetFile))
                    {
                        File.Move(files[i], targetFile);
                    }
                }
            }, cancellationToken);
        }
    }
}
