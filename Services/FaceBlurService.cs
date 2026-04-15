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
    /// 眼睛检测最小像素尺寸下限
    /// </summary>
    private const int MinEyeSizePixels = 8;

    /// <summary>
    /// 眼睛宽度相对于脸部宽度的最大比例（过大的检测为误检）
    /// </summary>
    private const double MaxEyeWidthRatio = 0.5;

    /// <summary>
    /// 眼睛高度相对于脸部高度的最大比例
    /// </summary>
    private const double MaxEyeHeightRatio = 0.4;

    /// <summary>
    /// 眼睛宽度相对于脸部宽度的最小比例（过小的检测为误检）
    /// </summary>
    private const double MinEyeWidthRatio = 0.05;

    /// <summary>
    /// 从眼睛位置到脸部中心的垂直偏移比例（基于人脸比例，眼睛在脸部上方约35%处）
    /// </summary>
    private const double EyeToFaceCenterOffsetRatio = 0.15;

    /// <summary>
    /// 皮肤像素面积相对于脸部面积的最小比例，低于此值认为皮肤检测不可信
    /// </summary>
    private const double MinSkinAreaRatio = 0.05;

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

                    var imageSize = image.Size();
                    foreach (Rect face in validatedFaces)
                    {
                        var refinedFace = RefineFaceRect(face, gray, image, eyeClassifier, imageSize);
                        BlurFaceEllipse(image, refinedFace);
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
    /// 精细化人脸矩形位置：优先使用眼睛定位，其次使用皮肤颜色分析。
    /// 解决侧脸检测时遮挡区域偏移到耳朵等非脸部区域的问题。
    /// </summary>
    private static Rect RefineFaceRect(Rect face, Mat gray, Mat colorImage, CascadeClassifier eyeClassifier, Size imageSize)
    {
        // 优先使用眼睛检测来精确定位脸部中心
        Rect? eyeRefined = TryRefineFaceByEyes(face, gray, eyeClassifier, imageSize);
        if (eyeRefined.HasValue)
            return eyeRefined.Value;

        // 没有检测到眼睛时，使用皮肤颜色质心来调整遮挡位置
        return RefineFaceBySkinColor(face, colorImage, imageSize);
    }

    /// <summary>
    /// 通过眼睛检测精细化人脸位置。
    /// 在检测框的扩展区域内搜索眼睛，根据眼睛位置重新估算脸部中心。
    /// </summary>
    private static Rect? TryRefineFaceByEyes(Rect face, Mat gray, CascadeClassifier eyeClassifier, Size imageSize)
    {
        // 扩大搜索区域以覆盖检测框外可能遗漏的眼睛（侧脸时常见）
        int expandX = face.Width / 2;
        int expandY = face.Height / 4;
        var searchArea = new Rect(
            face.X - expandX,
            face.Y - expandY,
            face.Width + expandX * 2,
            (int)(face.Height * 0.75) + expandY);
        searchArea = ClampRect(searchArea, imageSize);

        if (searchArea.Width <= 0 || searchArea.Height <= 0)
            return null;

        using var searchRoi = new Mat(gray, searchArea);
        int minEyeSize = Math.Max(MinEyeSizePixels, face.Width / 10);
        Rect[] eyes = eyeClassifier.DetectMultiScale(
            searchRoi,
            scaleFactor: 1.05,
            minNeighbors: 2,
            flags: HaarDetectionTypes.ScaleImage,
            minSize: new Size(minEyeSize, minEyeSize));

        if (eyes.Length == 0)
            return null;

        // 过滤掉不合理的眼睛检测（太大或太小的）
        var validEyes = eyes.Where(e =>
            e.Width < face.Width * MaxEyeWidthRatio &&
            e.Height < face.Height * MaxEyeHeightRatio &&
            e.Width > face.Width * MinEyeWidthRatio).ToArray();

        if (validEyes.Length == 0)
            return null;

        // 计算眼睛中心的绝对坐标
        double eyeCenterX = validEyes.Average(e => searchArea.X + e.X + e.Width / 2.0);
        double eyeCenterY = validEyes.Average(e => searchArea.Y + e.Y + e.Height / 2.0);

        // 眼睛大约在脸部上方35%处，据此估算脸部中心
        int faceCenterX = (int)eyeCenterX;
        int faceCenterY = (int)(eyeCenterY + face.Height * EyeToFaceCenterOffsetRatio);

        var refined = new Rect(
            faceCenterX - face.Width / 2,
            faceCenterY - face.Height / 2,
            face.Width,
            face.Height);
        return ClampRect(refined, imageSize);
    }

    /// <summary>
    /// 通过皮肤颜色分析精细化人脸位置。
    /// 在检测框周围扩展搜索，找到皮肤区域的质心作为脸部中心参考。
    /// </summary>
    private static Rect RefineFaceBySkinColor(Rect face, Mat colorImage, Size imageSize)
    {
        // 扩展搜索区域
        int expandX = face.Width / 2;
        int expandY = face.Height / 4;
        var searchArea = new Rect(
            face.X - expandX,
            face.Y - expandY,
            face.Width + expandX * 2,
            face.Height + expandY * 2);
        searchArea = ClampRect(searchArea, imageSize);

        if (searchArea.Width <= 0 || searchArea.Height <= 0)
            return face;

        using var roi = new Mat(colorImage, searchArea);
        using var hsv = new Mat();
        Cv2.CvtColor(roi, hsv, ColorConversionCodes.BGR2HSV);

        // 使用两个HSV范围覆盖不同肤色
        using var skinMask1 = new Mat();
        using var skinMask2 = new Mat();
        using var skinMask = new Mat();
        Cv2.InRange(hsv, new Scalar(0, 30, 60), new Scalar(25, 180, 255), skinMask1);
        Cv2.InRange(hsv, new Scalar(160, 30, 60), new Scalar(180, 180, 255), skinMask2);
        Cv2.BitwiseOr(skinMask1, skinMask2, skinMask);

        // 形态学操作去除噪点
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5));
        Cv2.MorphologyEx(skinMask, skinMask, MorphTypes.Close, kernel);
        Cv2.MorphologyEx(skinMask, skinMask, MorphTypes.Open, kernel);

        var moments = Cv2.Moments(skinMask, true);
        // 需要有足够的皮肤像素才认为结果可信
        double minSkinArea = face.Width * face.Height * MinSkinAreaRatio;
        if (moments.M00 > minSkinArea)
        {
            int skinCenterX = (int)(moments.M10 / moments.M00) + searchArea.X;
            int skinCenterY = (int)(moments.M01 / moments.M00) + searchArea.Y;

            // 使用加权平均：偏向皮肤质心但不完全脱离原始检测
            int origCenterX = face.X + face.Width / 2;
            int origCenterY = face.Y + face.Height / 2;
            int newCenterX = (origCenterX + skinCenterX * 2) / 3;
            int newCenterY = (origCenterY + skinCenterY * 2) / 3;

            var refined = new Rect(
                newCenterX - face.Width / 2,
                newCenterY - face.Height / 2,
                face.Width,
                face.Height);
            return ClampRect(refined, imageSize);
        }

        return face;
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
