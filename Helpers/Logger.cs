using System.IO;

namespace VideoFrameExtractor.Helpers
{
    /// <summary>
    /// 日志记录工具类
    /// </summary>
    public static class Logger
    {
        private static readonly string LogDirectory = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "logs");

        private static readonly object _lock = new();

        /// <summary>
        /// 记录信息日志
        /// </summary>
        public static void Info(string message)
        {
            WriteLog("INFO", message);
        }

        /// <summary>
        /// 记录警告日志
        /// </summary>
        public static void Warn(string message)
        {
            WriteLog("WARN", message);
        }

        /// <summary>
        /// 记录错误日志
        /// </summary>
        public static void Error(string message, Exception? ex = null)
        {
            string logMessage = ex != null ? $"{message}\n{ex}" : message;
            WriteLog("ERROR", logMessage);
        }

        private static void WriteLog(string level, string message)
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                {
                    Directory.CreateDirectory(LogDirectory);
                }

                string logFile = Path.Combine(LogDirectory, $"{DateTime.Now:yyyy-MM-dd}.txt");
                string logEntry = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}{Environment.NewLine}";

                lock (_lock)
                {
                    File.AppendAllText(logFile, logEntry);
                }
            }
            catch
            {
                // 日志写入失败不应影响程序运行
            }
        }
    }
}
