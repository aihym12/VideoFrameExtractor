using System.IO;
using System.Net.Http;
using OpenCvSharp;
using VideoFrameExtractor.Helpers;
using VideoFrameExtractor.Models;

namespace VideoFrameExtractor.Services;

/// <summary>
/// 图片人脸自动涂抹服务
/// </summary>
public class FaceBlurService
{
    private const string FaceCascadeFileName = "haarcascade_frontalface_default.xml";
    private const string ProfileCascadeFileName = "haarcascade_profileface.xml";
    private const string EyeCascadeFileName = "haarcascade_eye_tree_eyeglasses.xml";
    private const string FaceCascadeDownloadUrl = "https://raw.githubusercontent.com/opencv/opencv/master/data/haarcascades/haarcascade_frontalface_default.xml";
    private const string ProfileCascadeDownloadUrl = "https://raw.githubusercontent.com/opencv/opencv/master/data/haarcascades/haarcascade_profileface.xml";
    private const string EyeCascadeDownloadUrl = "https://raw.githubusercontent.com/opencv/opencv/master/data/haarcascades/haarcascade_eye_tree_eyeglasses.xml";

    /// <summary>
    /// 合并重叠人脸时的 IoU 阈值，超过此值视为同一张脸
    /// </summary>
    private const double IoUMergeThreshold = 0.3;

    /// <summary>
    /// 获取缺失的级联分类器文件名列表（用于下载前提示用户）
    /// </summary>
    public static List<string> GetMissingCascadeFileNames()
    {
        string modelDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models");
        var missing = new List<string>();

        if (!File.Exists(Path.Combine(modelDirectory, FaceCascadeFileName)))
            missing.Add(FaceCascadeFileName);
        if (!File.Exists(Path.Combine(modelDirectory, ProfileCascadeFileName)))
            missing.Add(ProfileCascadeFileName);
        if (!File.Exists(Path.Combine(modelDirectory, EyeCascadeFileName)))
            missing.Add(EyeCascadeFileName);

        return missing;
    }

    /// <summary>
    /// 对目录中的图片执行人脸检测并涂抹
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

        string faceCascadePath = await EnsureCascadeAsync(FaceCascadeFileName, FaceCascadeDownloadUrl, cancellationToken);
        string profileCascadePath = await EnsureCascadeAsync(ProfileCascadeFileName, ProfileCascadeDownloadUrl, cancellationToken);
        string eyeCascadePath = await EnsureCascadeAsync(EyeCascadeFileName, EyeCascadeDownloadUrl, cancellationToken);

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

            using var faceClassifier = new CascadeClassifier(faceCascadePath);
            using var profileClassifier = new CascadeClassifier(profileCascadePath);
            using var eyeClassifier = new CascadeClassifier(eyeCascadePath);
            if (faceClassifier.Empty() || profileClassifier.Empty() || eyeClassifier.Empty())
                throw new InvalidOperationException("OpenCV 人脸检测器初始化失败。");

            // 根据灵敏度设置检测参数
            GetDetectionParameters(blurSettings.Sensitivity, out double scaleFactor, out int minNeighbors);

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

                    using var gray = new Mat();
                    Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
                    Cv2.EqualizeHist(gray, gray);

                    var allFaces = DetectFacesMultiPass(gray, faceClassifier, profileClassifier, scaleFactor, minNeighbors);

                    var validatedFaces = allFaces
                        .Where(face => IsLikelyFace(face, image.Size(), gray, eyeClassifier, blurSettings.Sensitivity))
                        .ToArray();

                    if (validatedFaces.Length == 0)
                    {
                        progress?.Report($"人脸涂抹进度: {i + 1}/{files.Count}（未检测到人脸）");
                        continue;
                    }

                    foreach (Rect face in validatedFaces)
                    {
                        ApplyFaceBlur(image, face, blurSettings);
                    }

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

    /// <summary>
    /// 根据灵敏度获取检测参数
    /// </summary>
    private static void GetDetectionParameters(FaceDetectionSensitivity sensitivity, out double scaleFactor, out int minNeighbors)
    {
        switch (sensitivity)
        {
            case FaceDetectionSensitivity.Low:
                scaleFactor = 1.1;
                minNeighbors = 6;
                break;
            case FaceDetectionSensitivity.High:
                scaleFactor = 1.03;
                minNeighbors = 2;
                break;
            default: // Medium
                scaleFactor = 1.05;
                minNeighbors = 4;
                break;
        }
    }

    /// <summary>
    /// 根据设置应用人脸涂抹效果
    /// </summary>
    private static void ApplyFaceBlur(Mat image, Rect face, FaceBlurSettings settings)
    {
        if (settings.BlurMode == FaceBlurMode.Mosaic)
            MosaicFaceEllipse(image, face, settings.BlurStrength);
        else
            BlurFaceEllipse(image, face, settings.BlurStrength);
    }

    /// <summary>
    /// 多策略人脸检测：正脸 + 侧脸 + 不同参数组合，提高检出率
    /// </summary>
    private static List<Rect> DetectFacesMultiPass(Mat gray, CascadeClassifier faceClassifier, CascadeClassifier profileClassifier, double scaleFactor, int minNeighbors)
    {
        int minSide = Math.Max(40, Math.Min(gray.Width, gray.Height) / 10);
        var minSize = new Size(minSide, minSide);

        // 正脸检测
        Rect[] frontalFaces = faceClassifier.DetectMultiScale(
            gray,
            scaleFactor: scaleFactor,
            minNeighbors: minNeighbors,
            flags: HaarDetectionTypes.ScaleImage,
            minSize: minSize);

        // 侧脸检测 - 左侧脸
        Rect[] profileFacesLeft = profileClassifier.DetectMultiScale(
            gray,
            scaleFactor: scaleFactor,
            minNeighbors: minNeighbors,
            flags: HaarDetectionTypes.ScaleImage,
            minSize: minSize);

        // 侧脸检测 - 水平翻转后检测右侧脸
        using var flipped = new Mat();
        Cv2.Flip(gray, flipped, FlipMode.Y);
        Rect[] profileFacesRightFlipped = profileClassifier.DetectMultiScale(
            flipped,
            scaleFactor: scaleFactor,
            minNeighbors: minNeighbors,
            flags: HaarDetectionTypes.ScaleImage,
            minSize: minSize);

        // 将翻转后的坐标映射回原图
        var profileFacesRight = profileFacesRightFlipped
            .Select(r => new Rect(gray.Width - r.X - r.Width, r.Y, r.Width, r.Height))
            .ToArray();

        var allDetections = new List<Rect>();
        allDetections.AddRange(frontalFaces);
        allDetections.AddRange(profileFacesLeft);
        allDetections.AddRange(profileFacesRight);

        return MergeOverlappingFaces(allDetections);
    }

    /// <summary>
    /// 合并重叠的人脸检测结果，避免重复涂抹
    /// </summary>
    private static List<Rect> MergeOverlappingFaces(List<Rect> faces)
    {
        if (faces.Count <= 1)
            return faces;

        var sorted = faces.OrderByDescending(f => f.Width * f.Height).ToList();
        var merged = new List<Rect>();

        foreach (var face in sorted)
        {
            bool isOverlapping = false;
            for (int i = 0; i < merged.Count; i++)
            {
                if (ComputeIoU(face, merged[i]) > IoUMergeThreshold)
                {
                    // 保留面积更大的检测结果
                    if (face.Width * face.Height > merged[i].Width * merged[i].Height)
                        merged[i] = face;
                    isOverlapping = true;
                    break;
                }
            }

            if (!isOverlapping)
                merged.Add(face);
        }

        return merged;
    }

    /// <summary>
    /// 计算两个矩形的交并比（IoU）
    /// </summary>
    private static double ComputeIoU(Rect a, Rect b)
    {
        int x1 = Math.Max(a.X, b.X);
        int y1 = Math.Max(a.Y, b.Y);
        int x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        int y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

        if (x2 <= x1 || y2 <= y1)
            return 0.0;

        double intersection = (x2 - x1) * (y2 - y1);
        double union = (a.Width * a.Height) + (b.Width * b.Height) - intersection;
        return union > 0 ? intersection / union : 0.0;
    }

    /// <summary>
    /// 使用椭圆形遮罩高斯涂抹人脸区域
    /// </summary>
    private static void BlurFaceEllipse(Mat image, Rect face, int strength)
    {
        // 创建与原图等大的遮罩
        using var mask = new Mat(image.Size(), MatType.CV_8UC1, Scalar.All(0));

        // 椭圆中心和轴长（基于人脸矩形）
        var center = new Point(face.X + face.Width / 2, face.Y + face.Height / 2);
        var axes = new Size(face.Width / 2, face.Height / 2);

        // 绘制填充椭圆作为遮罩
        Cv2.Ellipse(mask, center, axes, 0, 0, 360, Scalar.All(255), -1);

        // 根据强度计算高斯模糊核大小（强度1≈0.515x，强度100=2.0x）
        int baseKernel = Math.Max(31, ((Math.Min(face.Width, face.Height) / 2) * 2) + 1);
        double strengthMultiplier = 0.5 + (strength / 100.0) * 1.5;
        int kernel = Math.Max(3, ((int)(baseKernel * strengthMultiplier) / 2) * 2 + 1);

        using var blurred = new Mat();
        Cv2.GaussianBlur(image, blurred, new Size(kernel, kernel), 0);

        // 使用遮罩将模糊区域合成到原图（仅椭圆区域被替换）
        blurred.CopyTo(image, mask);
    }

    /// <summary>
    /// 使用椭圆形遮罩马赛克涂抹人脸区域
    /// </summary>
    private static void MosaicFaceEllipse(Mat image, Rect face, int strength)
    {
        // 创建与原图等大的遮罩
        using var mask = new Mat(image.Size(), MatType.CV_8UC1, Scalar.All(0));

        var center = new Point(face.X + face.Width / 2, face.Y + face.Height / 2);
        var axes = new Size(face.Width / 2, face.Height / 2);
        Cv2.Ellipse(mask, center, axes, 0, 0, 360, Scalar.All(255), -1);

        // 根据强度计算马赛克块大小：强度1 → 约5%人脸尺寸，强度100 → 约30%人脸尺寸
        int minFaceDim = Math.Min(face.Width, face.Height);
        int blockSize = Math.Max(2, (int)(minFaceDim * (0.05 + (strength / 100.0) * 0.25)));

        // 确保 face 区域在图像范围内
        var clampedFace = ClampRect(face, image.Size());
        if (clampedFace.Width <= 0 || clampedFace.Height <= 0)
            return;

        // 创建马赛克效果：缩小再放大
        using var faceRoi = new Mat(image, clampedFace);
        using var small = new Mat();
        var smallSize = new Size(
            Math.Max(1, clampedFace.Width / blockSize),
            Math.Max(1, clampedFace.Height / blockSize));
        Cv2.Resize(faceRoi, small, smallSize, interpolation: InterpolationFlags.Linear);
        using var mosaic = new Mat();
        Cv2.Resize(small, mosaic, new Size(clampedFace.Width, clampedFace.Height), interpolation: InterpolationFlags.Nearest);

        // 创建一张带马赛克效果的完整图像副本
        using var mosaicFull = image.Clone();
        mosaic.CopyTo(new Mat(mosaicFull, clampedFace));

        // 使用椭圆遮罩合成
        mosaicFull.CopyTo(image, mask);
    }

    private static bool IsLikelyFace(Rect face, Size imageSize, Mat gray, CascadeClassifier eyeClassifier, FaceDetectionSensitivity sensitivity)
    {
        if (face.Width <= 0 || face.Height <= 0)
            return false;

        double aspectRatio = face.Width / (double)face.Height;
        double areaRatio = (face.Width * face.Height) / (double)(imageSize.Width * imageSize.Height);

        // 根据灵敏度调整验证阈值
        double minAspectRatio, maxAspectRatio, minAreaRatio, maxAreaRatio, strictMinAspectRatio, strictMaxAspectRatio, strictMinAreaRatio;
        switch (sensitivity)
        {
            case FaceDetectionSensitivity.High:
                minAspectRatio = 0.4;
                maxAspectRatio = 2.0;
                minAreaRatio = 0.005;
                maxAreaRatio = 0.85;
                strictMinAspectRatio = 0.6;
                strictMaxAspectRatio = 1.6;
                strictMinAreaRatio = 0.01;
                break;
            case FaceDetectionSensitivity.Low:
                minAspectRatio = 0.6;
                maxAspectRatio = 1.5;
                minAreaRatio = 0.02;
                maxAreaRatio = 0.65;
                strictMinAspectRatio = 0.8;
                strictMaxAspectRatio = 1.3;
                strictMinAreaRatio = 0.03;
                break;
            default: // Medium
                minAspectRatio = 0.5;
                maxAspectRatio = 1.8;
                minAreaRatio = 0.01;
                maxAreaRatio = 0.75;
                strictMinAspectRatio = 0.7;
                strictMaxAspectRatio = 1.4;
                strictMinAreaRatio = 0.02;
                break;
        }

        if (aspectRatio < minAspectRatio || aspectRatio > maxAspectRatio)
            return false;

        if (areaRatio < minAreaRatio || areaRatio > maxAreaRatio)
            return false;

        if (face.Y > imageSize.Height * 0.92)
            return false;

        // 尝试检测眼睛进行验证，但不作为硬性要求
        int upperHeight = Math.Max(1, (int)(face.Height * 0.65));
        var upperFace = new Rect(face.X, face.Y, face.Width, upperHeight);

        // 确保 ROI 在图像范围内
        upperFace = ClampRect(upperFace, imageSize);
        if (upperFace.Width <= 0 || upperFace.Height <= 0)
            return false;

        using var upperFaceRoi = new Mat(gray, upperFace);

        Rect[] eyes = eyeClassifier.DetectMultiScale(
            upperFaceRoi,
            scaleFactor: 1.1,
            minNeighbors: 2,
            flags: HaarDetectionTypes.ScaleImage,
            minSize: new Size(Math.Max(8, face.Width / 12), Math.Max(8, face.Height / 14)));

        if (eyes.Length >= 1)
            return true;

        // 没检测到眼睛时，使用更严格的面积和宽高比过滤误检
        return aspectRatio >= strictMinAspectRatio && aspectRatio <= strictMaxAspectRatio
            && areaRatio >= strictMinAreaRatio;
    }

    /// <summary>
    /// 将矩形裁剪到图像边界内
    /// </summary>
    private static Rect ClampRect(Rect rect, Size imageSize)
    {
        int x = Math.Max(0, rect.X);
        int y = Math.Max(0, rect.Y);
        int right = Math.Min(imageSize.Width, rect.X + rect.Width);
        int bottom = Math.Min(imageSize.Height, rect.Y + rect.Height);
        return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    private static async Task<string> EnsureCascadeAsync(string fileName, string downloadUrl, CancellationToken cancellationToken)
    {
        string modelDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models");
        string modelPath = Path.Combine(modelDirectory, fileName);

        if (File.Exists(modelPath))
            return modelPath;

        Directory.CreateDirectory(modelDirectory);

        using var httpClient = new HttpClient();
        byte[] xml = await httpClient.GetByteArrayAsync(downloadUrl, cancellationToken);
        await File.WriteAllBytesAsync(modelPath, xml, cancellationToken);
        return modelPath;
    }
}
