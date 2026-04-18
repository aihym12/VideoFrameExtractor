using System.IO;
using OpenCvSharp;
using VideoFrameExtractor.Helpers;
using VideoFrameExtractor.Models;

namespace VideoFrameExtractor.Services;

/// <summary>
/// 图片人脸自动涂抹服务（BiSeNet 像素级脸型检测版）
/// </summary>
public class FaceBlurService : IDisposable
{
    /// <summary>掩膜有效像素占总像素的最低比例，低于此值视为无脸</summary>
    private const double MinFaceMaskRatio = FaceBlurConstants.MinFaceMaskRatio;

    private BiSeNetFaceParser? _biSeNetParser;
    private OnnxDevice _currentDevice = OnnxDevice.Cpu;
    private bool _disposed;

    // ── 静态辅助（供 MainWindow 查询缺失文件） ───────────────────────────────

    /// <summary>
    /// 返回缺失的模型文件名列表（供 UI 提示用户）
    /// </summary>
    public static List<string> GetMissingModelFileNames()
    {
        var missing = new List<string>();
        if (!BiSeNetFaceParser.IsModelPresent())
            missing.Add(BiSeNetFaceParser.ModelFileName);
        return missing;
    }

    // ── 主流程 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 对目录中的所有图片执行 BiSeNet 脸型检测并涂抹。
    /// 若模型不存在则抛出 <see cref="FileNotFoundException"/>。
    /// </summary>
    public async Task<int> BlurFacesInFolderAsync(
        string folderPath,
        FaceBlurSettings? blurSettings = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        blurSettings ??= new FaceBlurSettings();

        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            throw new DirectoryNotFoundException("目标图片目录不存在。");

        // 初始化解析器（设备变更时重建 ONNX Session）
        if (_biSeNetParser == null || _currentDevice != blurSettings.InferenceDevice)
        {
            _biSeNetParser?.Dispose();
            _biSeNetParser = new BiSeNetFaceParser(blurSettings.InferenceDevice);
            _currentDevice = blurSettings.InferenceDevice;
        }

        return await Task.Run(() =>
        {
            string[] patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp"];
            var files = patterns
                .SelectMany(p => Directory.EnumerateFiles(folderPath, p, SearchOption.TopDirectoryOnly))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToList();

            if (files.Count == 0)
                return 0;

            int changedCount = 0;
            for (int i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string file = files[i];

                try
                {
                    using var image = Cv2.ImRead(file, ImreadModes.Color);
                    if (image.Empty())
                        continue;

                    // 获取脸型二值掩膜（255=脸部，0=非脸部）
                    using var mask = _biSeNetParser.GetFaceMask(image, blurSettings.Sensitivity);

                    // 检查掩膜中脸部像素是否足够多
                    int totalPixels = image.Rows * image.Cols;
                    int facePixels = Cv2.CountNonZero(mask);
                    if (facePixels < totalPixels * MinFaceMaskRatio)
                    {
                        progress?.Report($"人脸涂抹进度: {i + 1}/{files.Count}（未检测到人脸）");
                        continue;
                    }

                    // 按掩膜精确涂抹
                    ApplyFaceBlurByMask(image, mask, blurSettings);

                    Cv2.ImWrite(file, image);
                    changedCount++;
                    progress?.Report($"人脸涂抹进度: {i + 1}/{files.Count}（已处理 {changedCount} 张）");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"人脸涂抹跳过文件: {file}，原因: {ex.Message}");
                }
            }

            return changedCount;
        }, cancellationToken);
    }

    // ── 涂抹效果 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 根据二值掩膜对图像应用高斯模糊或马赛克，掩膜边缘带羽化过渡。
    /// </summary>
    private static void ApplyFaceBlurByMask(Mat image, Mat mask, FaceBlurSettings settings) =>
        FaceBlurHelper.ApplyFaceBlurByMask(image, mask, settings);

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _biSeNetParser?.Dispose();
        _biSeNetParser = null;
    }
}
