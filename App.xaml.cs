using System.IO;
using System.Windows;
using VideoFrameExtractor.Helpers;
using VideoFrameExtractor.Services;

namespace VideoFrameExtractor
{
    /// <summary>
    /// 应用程序入口
    /// </summary>
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            try
            {
                string proxyAddress = VideoFrameExtractor.Properties.Settings.Default.ProxyAddress;
                if (!string.IsNullOrWhiteSpace(proxyAddress))
                {
                    ProxyHelper.ApplyProxy(proxyAddress);
                }

                if (!FFmpegService.IsFFmpegInstalled())
                {
                    var result = MessageBox.Show(
                        "未检测到 FFmpeg，是否自动下载？\n（约 100MB，需要网络连接）",
                        "FFmpeg 缺失",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result != MessageBoxResult.Yes)
                    {
                        Current.Shutdown();
                        return;
                    }

                    FFmpegService.DownloadAsync().GetAwaiter().GetResult();
                    MessageBox.Show("FFmpeg 下载完成。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                FFmpegService.Configure();
                Logger.Info($"应用启动成功，工作目录: {AppDomain.CurrentDomain.BaseDirectory}");

                base.OnStartup(e);
            }
            catch (Exception ex)
            {
                Logger.Error("应用启动失败", ex);
                MessageBox.Show($"初始化失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Current.Shutdown();
            }
        }
    }

}
