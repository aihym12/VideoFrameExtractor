using System.IO;
using System.Net.Http;
using OpenCvSharp;
using VideoFrameExtractor.Helpers;

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
    /// 对目录中的图片执行人脸检测并高斯涂抹
    /// </summary>
    public async Task<int> BlurFacesInFolderAsync(
        string folderPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
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

                    var allFaces = DetectFacesMultiPass(gray, faceClassifier, profileClassifier);

                    var validatedFaces = allFaces
                        .Where(face => IsLikelyFace(face, image.Size(), gray, eyeClassifier))
                        .ToArray();

                    if (validatedFaces.Length == 0)
                    {
                        progress?.Report($"人脸涂抹进度: {i + 1}/{files.Count}（未检测到人脸）");
                        continue;
                    }

                    foreach (Rect face in validatedFaces)
                    {
                        BlurFaceEllipse(image, face);
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
    /// 多策略人脸检测：正脸 + 侧脸 + 不同参数组合，提高检出率
    /// </summary>
    private static List<Rect> DetectFacesMultiPass(Mat gray, CascadeClassifier faceClassifier, CascadeClassifier profileClassifier)
    {
        int minSide = Math.Max(40, Math.Min(gray.Width, gray.Height) / 10);
        var minSize = new Size(minSide, minSide);

        // 正脸检测 - 使用较宽松的参数提高检出率
        Rect[] frontalFaces = faceClassifier.DetectMultiScale(
            gray,
            scaleFactor: 1.05,
            minNeighbors: 4,
            flags: HaarDetectionTypes.ScaleImage,
            minSize: minSize);

        // 侧脸检测 - 左侧脸
        Rect[] profileFacesLeft = profileClassifier.DetectMultiScale(
            gray,
            scaleFactor: 1.05,
            minNeighbors: 4,
            flags: HaarDetectionTypes.ScaleImage,
            minSize: minSize);

        // 侧脸检测 - 水平翻转后检测右侧脸
        using var flipped = new Mat();
        Cv2.Flip(gray, flipped, FlipMode.Y);
        Rect[] profileFacesRightFlipped = profileClassifier.DetectMultiScale(
            flipped,
            scaleFactor: 1.05,
            minNeighbors: 4,
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
    /// 使用椭圆形遮罩涂抹人脸区域，贴合脸型
    /// </summary>
    private static void BlurFaceEllipse(Mat image, Rect face)
    {
        // 创建与原图等大的遮罩
        using var mask = new Mat(image.Size(), MatType.CV_8UC1, Scalar.All(0));

        // 椭圆中心和轴长（基于人脸矩形）
        var center = new Point(face.X + face.Width / 2, face.Y + face.Height / 2);
        var axes = new Size(face.Width / 2, face.Height / 2);

        // 绘制填充椭圆作为遮罩
        Cv2.Ellipse(mask, center, axes, 0, 0, 360, Scalar.All(255), -1);

        // 对整张图片做高斯模糊
        int kernel = Math.Max(31, ((Math.Min(face.Width, face.Height) / 2) * 2) + 1);
        using var blurred = new Mat();
        Cv2.GaussianBlur(image, blurred, new Size(kernel, kernel), 0);

        // 使用遮罩将模糊区域合成到原图（仅椭圆区域被替换）
        blurred.CopyTo(image, mask);
    }

    private static bool IsLikelyFace(Rect face, Size imageSize, Mat gray, CascadeClassifier eyeClassifier)
    {
        if (face.Width <= 0 || face.Height <= 0)
            return false;

        // 初筛使用较宽松的范围兼容侧脸等非标准角度
        double aspectRatio = face.Width / (double)face.Height;
        if (aspectRatio is < 0.5 or > 1.8)
            return false;

        double areaRatio = (face.Width * face.Height) / (double)(imageSize.Width * imageSize.Height);
        if (areaRatio is < 0.01 or > 0.75)
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

        // 即使未检测到眼睛，如果面积比例合理也认为是人脸
        // 这对侧脸、眯眼、遮挡等场景更友好
        if (eyes.Length >= 1)
            return true;

        // 没检测到眼睛时，使用更严格的面积和宽高比过滤误检（排除侧脸宽高比极端的检测）
        return aspectRatio is >= 0.7 and <= 1.4
            && areaRatio >= 0.02;
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
