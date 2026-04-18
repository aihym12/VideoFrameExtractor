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
    private const double MinFaceMaskRatio = 0.001;

    /// <summary>软边缘羽化的高斯核尺寸</summary>
    private const int FeatherKernelSize = 21;

    /// <summary>软边缘高斯 sigma</summary>
    private const double FeatherSigma = 8.0;

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
    private static void ApplyFaceBlurByMask(Mat image, Mat mask, FaceBlurSettings settings)
    {
        // 生成软边缘 float 权重掩膜 [0, 1]，避免硬边
        using var softMask = CreateSoftMask(mask);

        if (settings.BlurMode == FaceBlurMode.Mosaic)
            ApplyMosaicByMask(image, mask, softMask, settings.BlurStrength);
        else
            ApplyGaussianByMask(image, softMask, settings.BlurStrength);
    }

    /// <summary>高斯模糊：blended = blurred * alpha + original * (1 - alpha)</summary>
    private static void ApplyGaussianByMask(Mat image, Mat softMask, int strength)
    {
        int minDim = Math.Min(image.Rows, image.Cols);
        // 核大小随强度线性插值：strength=1 → ~5% 最短边，strength=100 → ~20% 最短边
        int baseKernel = Math.Max(3, (int)(minDim * (0.05 + strength / 100.0 * 0.15)));
        int kernel = baseKernel % 2 == 0 ? baseKernel + 1 : baseKernel; // 保证奇数

        using var blurred = new Mat();
        Cv2.GaussianBlur(image, blurred, new Size(kernel, kernel), 0);

        BlendByMask(image, blurred, softMask);
    }

    /// <summary>马赛克：找掩膜包围盒内做像素化，再按掩膜合成</summary>
    private static void ApplyMosaicByMask(Mat image, Mat binaryMask, Mat softMask, int strength)
    {
        // 找掩膜的包围盒
        using var nonZeroPoints = new Mat();
        Cv2.FindNonZero(binaryMask, nonZeroPoints);
        if (nonZeroPoints.Total() == 0) return;
        Rect bbox = Cv2.BoundingRect(nonZeroPoints);
        bbox = ClampRect(bbox, image.Size());
        if (bbox.Width <= 0 || bbox.Height <= 0) return;

        int minFaceDim = Math.Min(bbox.Width, bbox.Height);
        int blockSize = Math.Max(2, (int)(minFaceDim * (0.05 + strength / 100.0 * 0.25)));

        // 在包围盒内创建马赛克效果
        using var mosaicFull = image.Clone();
        using var faceRoi = new Mat(image, bbox);
        using var small = new Mat();
        var smallSize = new Size(Math.Max(1, bbox.Width / blockSize), Math.Max(1, bbox.Height / blockSize));
        Cv2.Resize(faceRoi, small, smallSize, interpolation: InterpolationFlags.Linear);
        using var mosaic = new Mat();
        Cv2.Resize(small, mosaic, new Size(bbox.Width, bbox.Height), interpolation: InterpolationFlags.Nearest);
        mosaic.CopyTo(new Mat(mosaicFull, bbox));

        BlendByMask(image, mosaicFull, softMask);
    }

    /// <summary>
    /// 按 softMask 软权重混合：dst = effect * alpha + original * (1 - alpha)
    /// 结果写回 image。
    /// </summary>
    private static void BlendByMask(Mat image, Mat effect, Mat softMask)
    {
        // 扩展 softMask 到 3 通道 float
        using var softMask3 = new Mat();
        Cv2.Merge([softMask, softMask, softMask], softMask3);

        using var imageF = new Mat();
        using var effectF = new Mat();
        image.ConvertTo(imageF, MatType.CV_32FC3);
        effect.ConvertTo(effectF, MatType.CV_32FC3);

        // diff = effect - original
        using var diff = new Mat();
        Cv2.Subtract(effectF, imageF, diff);

        // blended = original + diff * alpha
        using var blended = new Mat();
        Cv2.Multiply(diff, softMask3, blended);
        Cv2.Add(imageF, blended, blended);
        blended.ConvertTo(image, MatType.CV_8UC3);
    }

    /// <summary>
    /// 将二值掩膜转换为 [0, 1] 范围的单通道 float 软边缘掩膜。
    /// 通过高斯模糊使边缘过渡自然。
    /// </summary>
    private static Mat CreateSoftMask(Mat binaryMask)
    {
        var soft = new Mat();
        binaryMask.ConvertTo(soft, MatType.CV_32F, 1.0 / 255.0);
        Cv2.GaussianBlur(soft, soft, new Size(FeatherKernelSize, FeatherKernelSize), FeatherSigma);
        return soft;
    }

    /// <summary>将矩形裁剪到图像边界内</summary>
    private static Rect ClampRect(Rect rect, Size imageSize)
    {
        int x = Math.Max(0, rect.X);
        int y = Math.Max(0, rect.Y);
        int right = Math.Min(imageSize.Width, rect.X + rect.Width);
        int bottom = Math.Min(imageSize.Height, rect.Y + rect.Height);
        return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _biSeNetParser?.Dispose();
        _biSeNetParser = null;
    }
}
