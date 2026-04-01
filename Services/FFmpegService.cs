using System.IO;
using VideoFrameExtractor.Helpers;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

namespace VideoFrameExtractor.Services
{
    /// <summary>
    /// FFmpeg 管理服务（检测、下载、配置）
    /// </summary>
    public static class FFmpegService
    {
        private static readonly string FFmpegDirectory = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "ffmpeg");

        /// <summary>
        /// 检查 FFmpeg 是否已安装
        /// </summary>
        public static bool IsFFmpegInstalled()
        {
            return TryGetExecutableDirectory() is not null;
        }

        /// <summary>
        /// 配置 FFmpeg 可执行文件路径
        /// </summary>
        public static void Configure()
        {
            if (!Directory.Exists(FFmpegDirectory))
            {
                Directory.CreateDirectory(FFmpegDirectory);
            }

            string? executableDirectory = TryGetExecutableDirectory();
            if (string.IsNullOrWhiteSpace(executableDirectory))
            {
                throw new FileNotFoundException("未找到 ffmpeg.exe / ffprobe.exe，请先下载 FFmpeg。");
            }

            FFmpeg.SetExecutablesPath(executableDirectory, "ffmpeg.exe", "ffprobe.exe");
            Logger.Info($"FFmpeg 路径已配置: {executableDirectory}");
        }

        /// <summary>
        /// 下载最新版本的 FFmpeg
        /// </summary>
        public static async Task DownloadAsync(IProgress<string>? progress = null)
        {
            Logger.Info("开始下载 FFmpeg...");
            progress?.Report("正在下载 FFmpeg，请稍候...");

            if (!Directory.Exists(FFmpegDirectory))
            {
                Directory.CreateDirectory(FFmpegDirectory);
            }

            await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, FFmpegDirectory);

            string? executableDirectory = TryGetExecutableDirectory();
            if (string.IsNullOrWhiteSpace(executableDirectory))
            {
                throw new FileNotFoundException("FFmpeg 下载完成，但未找到 ffmpeg.exe / ffprobe.exe。");
            }

            Logger.Info("FFmpeg 下载完成");
            progress?.Report("FFmpeg 下载完成！");
        }

        private static string? TryGetExecutableDirectory()
        {
            if (!Directory.Exists(FFmpegDirectory))
            {
                return null;
            }

            var ffmpegFile = Directory.EnumerateFiles(FFmpegDirectory, "ffmpeg.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            var ffprobeFile = Directory.EnumerateFiles(FFmpegDirectory, "ffprobe.exe", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (ffmpegFile is null || ffprobeFile is null)
            {
                return null;
            }

            string? ffmpegDir = Path.GetDirectoryName(ffmpegFile);
            string? ffprobeDir = Path.GetDirectoryName(ffprobeFile);

            if (string.IsNullOrWhiteSpace(ffmpegDir) || string.IsNullOrWhiteSpace(ffprobeDir))
            {
                return null;
            }

            if (string.Equals(ffmpegDir, ffprobeDir, StringComparison.OrdinalIgnoreCase))
            {
                return ffmpegDir;
            }

            return ffmpegDir;
        }
    }
}
