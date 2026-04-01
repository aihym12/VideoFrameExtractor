using System.IO;

namespace VideoFrameExtractor.Helpers
{
    /// <summary>
    /// 路径辅助工具类
    /// </summary>
    public static class PathHelper
    {
        /// <summary>
        /// 生成默认输出文件夹路径
        /// </summary>
        public static string GetDefaultOutputFolder(string videoFilePath)
        {
            string? directory = Path.GetDirectoryName(videoFilePath);
            string videoName = Path.GetFileNameWithoutExtension(videoFilePath);
            return Path.Combine(directory ?? string.Empty, videoName + "_Frames");
        }

        /// <summary>
        /// 确保目录存在，不存在则创建
        /// </summary>
        public static void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        /// <summary>
        /// 检查路径是否有写入权限
        /// </summary>
        public static bool HasWritePermission(string path)
        {
            try
            {
                string testFile = Path.Combine(path, ".write_test_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 检查文件是否被占用
        /// </summary>
        public static bool IsFileLocked(string path)
        {
            try
            {
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite)) { }
                return false;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// 支持的视频文件扩展名
        /// </summary>
        public static readonly string[] SupportedVideoExtensions =
            [".mp4", ".avi", ".mov", ".mkv", ".wmv", ".flv", ".webm", ".m4v", ".ts"];

        /// <summary>
        /// 判断是否为支持的视频格式
        /// </summary>
        public static bool IsSupportedVideoFormat(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            return SupportedVideoExtensions.Contains(ext);
        }
    }
}
