using OpenCvSharp;
using VideoFrameExtractor.Models;

namespace VideoFrameExtractor.Services;

/// <summary>
/// 人脸涂抹效果的共享静态辅助方法，供 FaceBlurService、VideoFaceBlurService 及预览逻辑复用。
/// </summary>
internal static class FaceBlurHelper
{
    private const int FeatherKernelSize = 21;
    private const double FeatherSigma = 8.0;

    /// <summary>
    /// 根据二值掩膜对图像应用高斯模糊或马赛克，掩膜边缘带软边羽化。
    /// </summary>
    public static void ApplyFaceBlurByMask(Mat image, Mat mask, FaceBlurSettings settings)
    {
        using var softMask = CreateSoftMask(mask);
        if (settings.BlurMode == FaceBlurMode.Mosaic)
            ApplyMosaicByMask(image, mask, softMask, settings.BlurStrength);
        else
            ApplyGaussianByMask(image, softMask, settings.BlurStrength);
    }

    /// <summary>高斯模糊：按软权重掩膜混合模糊图与原图</summary>
    public static void ApplyGaussianByMask(Mat image, Mat softMask, int strength)
    {
        int minDim = Math.Min(image.Rows, image.Cols);
        int baseKernel = Math.Max(3, (int)(minDim * (0.05 + strength / 100.0 * 0.15)));
        int kernel = baseKernel % 2 == 0 ? baseKernel + 1 : baseKernel;
        using var blurred = new Mat();
        Cv2.GaussianBlur(image, blurred, new Size(kernel, kernel), 0);
        BlendByMask(image, blurred, softMask);
    }

    /// <summary>马赛克：在掩膜包围盒内像素化，再按软权重掩膜合成</summary>
    public static void ApplyMosaicByMask(Mat image, Mat binaryMask, Mat softMask, int strength)
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

    /// <summary>将二值掩膜转换为 [0, 1] 单通道 float 软边缘掩膜</summary>
    public static Mat CreateSoftMask(Mat binaryMask)
    {
        var soft = new Mat();
        binaryMask.ConvertTo(soft, MatType.CV_32F, 1.0 / 255.0);
        Cv2.GaussianBlur(soft, soft, new Size(FeatherKernelSize, FeatherKernelSize), FeatherSigma);
        return soft;
    }

    /// <summary>按 softMask 软权重混合：dst = effect * alpha + original * (1 - alpha)，结果写回 image</summary>
    public static void BlendByMask(Mat image, Mat effect, Mat softMask)
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

    /// <summary>将矩形裁剪到图像边界内</summary>
    public static Rect ClampRect(Rect rect, Size imageSize)
    {
        int x = Math.Max(0, rect.X);
        int y = Math.Max(0, rect.Y);
        int r = Math.Min(imageSize.Width, rect.X + rect.Width);
        int b = Math.Min(imageSize.Height, rect.Y + rect.Height);
        return new Rect(x, y, Math.Max(0, r - x), Math.Max(0, b - y));
    }
}
