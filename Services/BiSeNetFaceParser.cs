using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using VideoFrameExtractor.Helpers;
using VideoFrameExtractor.Models;

namespace VideoFrameExtractor.Services;

/// <summary>
/// BiSeNet 人脸语义分割推理器
/// 使用 19 类语义分割模型精确识别脸部像素，生成二值掩膜。
/// </summary>
/// <remarks>
/// 19 类标签（face-parsing.PyTorch / zllrunning）:
///  0=background, 1=skin, 2=l_brow, 3=r_brow, 4=l_eye, 5=r_eye,
///  6=eyeglasses, 7=l_ear, 8=r_ear, 9=earrings, 10=nose,
///  11=mouth, 12=u_lip, 13=l_lip, 14=neck, 15=necklace,
///  16=cloth, 17=hair, 18=hat
/// </remarks>
public sealed class BiSeNetFaceParser : IDisposable
{
    // ── 模型配置 ────────────────────────────────────────────────────────────
    public const string ModelFileName = "face_parsing_bisenet.onnx";

    // facefusion 开源发布的 BiSeNet face-parser (同 face-parsing.PyTorch 格式, 19 类)
    public const string ModelDownloadUrl =
        "https://github.com/facefusion/facefusion-assets/releases/download/models-3.0.0/face_parser.onnx";

    private const int InputSize = 512;
    private const int NumClasses = 19;

    // ── 需要涂抹的类别 ───────────────────────────────────────────────────────
    // 皮肤(1) 左右眉(2,3) 左右眼(4,5) 眼镜(6) 左右耳(7,8)
    // 鼻(10) 嘴(11) 上下唇(12,13)
    // 不含: 耳环(9) 脖子(14) 项链(15) 衣物(16) 头发(17) 帽子(18) 背景(0)
    private static readonly bool[] IsFaceClass = BuildFaceClassTable();

    // ── ImageNet 归一化参数（RGB 通道顺序） ──────────────────────────────────
    private static readonly float[] MeanRgb = [0.485f, 0.456f, 0.406f];
    private static readonly float[] StdRgb  = [0.229f, 0.224f, 0.225f];

    // ── 实例字段 ─────────────────────────────────────────────────────────────
    private readonly InferenceSession _session;
    private bool _disposed;

    // ── 静态 HttpClient（避免 socket 耗尽） ──────────────────────────────────
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(10) };

    // ── 静态辅助 ─────────────────────────────────────────────────────────────

    /// <summary>模型文件的完整路径</summary>
    public static string ModelFilePath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", ModelFileName);

    /// <summary>模型文件是否已存在</summary>
    public static bool IsModelPresent() => File.Exists(ModelFilePath);

    /// <summary>
    /// 下载模型文件到 models/ 目录
    /// </summary>
    public static async Task DownloadModelAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        string dir = Path.GetDirectoryName(ModelFilePath)!;
        Directory.CreateDirectory(dir);

        string tmpPath = ModelFilePath + ".tmp";
        try
        {
            // 先获取文件大小（可选）
            using var response = await _httpClient.GetAsync(ModelDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            long? totalBytes = response.Content.Headers.ContentLength;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            byte[] buffer = new byte[81920];
            long downloaded = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloaded += read;
                if (totalBytes.HasValue)
                {
                    int pct = (int)(downloaded * 100 / totalBytes.Value);
                    progress?.Report($"正在下载 BiSeNet 模型... {pct}% ({downloaded / 1_048_576}MB / {totalBytes.Value / 1_048_576}MB)");
                }
                else
                {
                    progress?.Report($"正在下载 BiSeNet 模型... {downloaded / 1_048_576}MB 已下载");
                }
            }
        }
        catch
        {
            // 下载失败时清理临时文件
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
            throw;
        }

        // 下载成功后原子性地替换目标文件
        File.Move(tmpPath, ModelFilePath, overwrite: true);
    }

    // ── 构造 ─────────────────────────────────────────────────────────────────

    public BiSeNetFaceParser(OnnxDevice device = OnnxDevice.Cpu)
    {
        if (!IsModelPresent())
            throw new FileNotFoundException(
                $"BiSeNet 模型文件缺失，请将 {ModelFileName} 放置到 models/ 目录。",
                ModelFilePath);

        var options = new SessionOptions();
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

        if (device == OnnxDevice.DirectML)
        {
            try
            {
                options.AppendExecutionProvider_DML(0);
                Logger.Info("BiSeNetFaceParser: 使用 DirectML（GPU）推理");
            }
            catch (Exception ex)
            {
                Logger.Warn($"DirectML 初始化失败，自动回退到 CPU: {ex.Message}");
                // 回退：使用默认 CPU 设置
                options.InterOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
                options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
            }
        }
        else
        {
            options.InterOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
            options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
            Logger.Info("BiSeNetFaceParser: 使用 CPU 推理");
        }

        _session = new InferenceSession(ModelFilePath, options);
    }

    // ── 公共 API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 对 BGR 图像执行 BiSeNet 推理，返回与原图同尺寸的 8UC1 二值掩膜。
    /// 掩膜中 255 = 脸部像素（需涂抹），0 = 非脸部。
    /// </summary>
    public Mat GetFaceMask(Mat bgrImage, FaceDetectionSensitivity sensitivity)
    {
        int origH = bgrImage.Rows;
        int origW = bgrImage.Cols;

        // 1. 预处理：BGR → 归一化 float32 NCHW [1, 3, 512, 512]
        using var resized = new Mat();
        Cv2.Resize(bgrImage, resized, new Size(InputSize, InputSize));
        var inputTensor = Preprocess(resized);

        // 2. 推理
        string inputName = _session.InputMetadata.Keys.First();
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
        };
        using var outputs = _session.Run(inputs);

        // 3. 取第一个输出，shape [1, 19, 512, 512]
        float[] outputData = outputs.First().AsTensor<float>().ToArray();

        // 4. Argmax → 二值掩膜 [512, 512]
        using var mask512 = BuildMask(outputData, InputSize, InputSize);

        // 5. 形态学闭操作：填补空洞、平滑边界
        using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(7, 7));
        Cv2.MorphologyEx(mask512, mask512, MorphTypes.Close, closeKernel);

        // 6. 根据灵敏度腐蚀/膨胀掩膜
        ApplySensitivity(mask512, sensitivity);

        // 7. resize 回原始尺寸（最近邻，保持边界锐利）
        var maskOrig = new Mat();
        Cv2.Resize(mask512, maskOrig, new Size(origW, origH), interpolation: InterpolationFlags.Nearest);

        return maskOrig;
    }

    // ── 私有方法 ─────────────────────────────────────────────────────────────

    private static bool[] BuildFaceClassTable()
    {
        var table = new bool[NumClasses];
        // 皮肤
        table[1] = true;
        // 眉毛
        table[2] = true; table[3] = true;
        // 眼睛
        table[4] = true; table[5] = true;
        // 眼镜
        table[6] = true;
        // 耳朵（含，不含耳环 9）
        table[7] = true; table[8] = true;
        // 鼻
        table[10] = true;
        // 嘴 + 上下唇
        table[11] = true; table[12] = true; table[13] = true;
        // 其余（0 背景, 9 耳环, 14 脖子, 15 项链, 16 衣物, 17 头发, 18 帽子）均为 false
        return table;
    }

    /// <summary>BGR Mat → float32 NCHW DenseTensor，ImageNet 归一化，RGB 通道顺序</summary>
    private static DenseTensor<float> Preprocess(Mat resizedBgr)
    {
        // 转为 float32，除以 255
        using var bgrFloat = new Mat();
        resizedBgr.ConvertTo(bgrFloat, MatType.CV_32FC3, 1.0 / 255.0);

        // 分离 B, G, R 通道
        Cv2.Split(bgrFloat, out Mat[] channels);
        // channels[0]=B, channels[1]=G, channels[2]=R

        var tensor = new DenseTensor<float>([1, 3, InputSize, InputSize]);
        int hw = InputSize * InputSize;
        Span<float> tData = tensor.Buffer.Span;

        // 通道映射：tensor[0]=R, tensor[1]=G, tensor[2]=B
        (int tChan, int cvChan, float mean, float std)[] mapping =
        [
            (0, 2, MeanRgb[0], StdRgb[0]), // R
            (1, 1, MeanRgb[1], StdRgb[1]), // G
            (2, 0, MeanRgb[2], StdRgb[2])  // B
        ];

        foreach (var (tChan, cvChan, mean, std) in mapping)
        {
            using var ch = channels[cvChan];
            // 归一化：(pixel - mean) / std
            Cv2.Subtract(ch, new Scalar(mean), ch);
            Cv2.Divide(ch, new Scalar(std), ch);

            // 将 Mat 数据复制到 tensor（连续内存，直接 Marshal.Copy）
            int offset = tChan * hw;
            float[] tmp = new float[hw];
            Marshal.Copy(ch.Data, tmp, 0, hw);
            tmp.AsSpan().CopyTo(tData.Slice(offset, hw));
        }

        return tensor;
    }

    /// <summary>对 [1, 19, H, W] 输出做 argmax，生成二值掩膜</summary>
    private static Mat BuildMask(float[] data, int h, int w)
    {
        int hw = h * w;
        var mask = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0));
        var maskIdx = mask.GetGenericIndexer<byte>();

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int pixelBase = y * w + x;
                int bestClass = 0;
                float bestScore = data[pixelBase]; // class 0
                for (int c = 1; c < NumClasses; c++)
                {
                    float score = data[c * hw + pixelBase];
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestClass = c;
                    }
                }
                if (IsFaceClass[bestClass])
                    maskIdx[y, x] = 255;
            }
        }

        return mask;
    }

    private static void ApplySensitivity(Mat mask, FaceDetectionSensitivity sensitivity)
    {
        switch (sensitivity)
        {
            case FaceDetectionSensitivity.Low:
                // 腐蚀：收紧掩膜，减少涂抹范围
                using (var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(9, 9)))
                    Cv2.Erode(mask, mask, k, iterations: 2);
                break;
            case FaceDetectionSensitivity.High:
                // 膨胀：稍微外扩掩膜
                using (var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(9, 9)))
                    Cv2.Dilate(mask, mask, k, iterations: 1);
                break;
            // Medium：不做调整
        }
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}
