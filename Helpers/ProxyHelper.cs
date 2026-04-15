using System.Net;
using System.Net.Http;

namespace VideoFrameExtractor.Helpers;

/// <summary>
/// 网络代理管理工具类
/// </summary>
public static class ProxyHelper
{
    private static readonly IWebProxy OriginalDefaultProxy = HttpClient.DefaultProxy;

    /// <summary>
    /// 应用代理设置到全局 HttpClient.DefaultProxy
    /// </summary>
    public static void ApplyProxy(string? proxyAddress)
    {
        if (string.IsNullOrWhiteSpace(proxyAddress))
        {
            HttpClient.DefaultProxy = OriginalDefaultProxy;
            Logger.Info("已清除代理设置，使用系统默认网络配置");
            return;
        }

        try
        {
            var proxy = new WebProxy(proxyAddress)
            {
                BypassProxyOnLocal = true
            };
            HttpClient.DefaultProxy = proxy;
            Logger.Info($"已设置网络代理: {proxyAddress}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"代理设置失败: {ex.Message}");
            throw new InvalidOperationException($"代理地址格式无效: {proxyAddress}", ex);
        }
    }
}
