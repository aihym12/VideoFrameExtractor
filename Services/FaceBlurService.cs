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
    private const string EyeCascadeFileName = "haarcascade_eye_tree_eyeglasses.xml";
    private const string FaceCascadeDownloadUrl = "https://raw.githubusercontent.com/opencv/opencv/master/data/haarcascades/haarcascade_frontalface_default.xml";
    private const string EyeCascadeDownloadUrl = "https://raw.githubusercontent.com/opencv/opencv/master/data/haarcascades/haarcascade_eye_tree_eyeglasses.xml";

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
            using var eyeClassifier = new CascadeClassifier(eyeCascadePath);
            if (faceClassifier.Empty() || eyeClassifier.Empty())
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

                    int minSide = Math.Max(60, Math.Min(image.Width, image.Height) / 8);
                    Rect[] faces = faceClassifier.DetectMultiScale(
                        gray,
                        scaleFactor: 1.1,
                        minNeighbors: 6,
                        flags: HaarDetectionTypes.ScaleImage,
                        minSize: new Size(minSide, minSide));

                    var validatedFaces = faces
                        .Where(face => IsLikelyFace(face, image.Size(), gray, eyeClassifier))
                        .ToArray();

                    if (validatedFaces.Length == 0)
                    {
                        progress?.Report($"人脸涂抹进度: {i + 1}/{files.Count}（未检测到人脸）");
                        continue;
                    }

                    foreach (Rect face in validatedFaces)
                    {
                        using var roi = new Mat(image, face);
                        int kernel = Math.Max(31, ((Math.Min(face.Width, face.Height) / 2) * 2) + 1);
                        Cv2.GaussianBlur(roi, roi, new Size(kernel, kernel), 0);
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

    private static bool IsLikelyFace(Rect face, Size imageSize, Mat gray, CascadeClassifier eyeClassifier)
    {
        if (face.Width <= 0 || face.Height <= 0)
            return false;

        double aspectRatio = face.Width / (double)face.Height;
        if (aspectRatio is < 0.7 or > 1.5)
            return false;

        double areaRatio = (face.Width * face.Height) / (double)(imageSize.Width * imageSize.Height);
        if (areaRatio is < 0.02 or > 0.65)
            return false;

        if (face.Y > imageSize.Height * 0.88)
            return false;

        int upperHeight = Math.Max(1, (int)(face.Height * 0.65));
        var upperFace = new Rect(face.X, face.Y, face.Width, upperHeight);
        using var upperFaceRoi = new Mat(gray, upperFace);

        Rect[] eyes = eyeClassifier.DetectMultiScale(
            upperFaceRoi,
            scaleFactor: 1.1,
            minNeighbors: 3,
            flags: HaarDetectionTypes.ScaleImage,
            minSize: new Size(Math.Max(10, face.Width / 10), Math.Max(10, face.Height / 12)));

        return eyes.Length >= 1;
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
