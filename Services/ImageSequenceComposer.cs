using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xabe.FFmpeg;

namespace VideoFrameExtractor.Services;

public class ImageSequenceComposer
{
    private static readonly string[] SupportedExtensions = [".jpg", ".jpeg", ".png", ".bmp"];

    public async Task<string> ComposeAsync(
        string imageFolder,
        string outputPath,
        double fps,
        string codec,
        int crf,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(imageFolder))
            throw new DirectoryNotFoundException($"图片文件夹不存在: {imageFolder}");

        var imageFiles = Directory.EnumerateFiles(imageFolder)
            .Where(file => SupportedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (imageFiles.Count == 0)
            throw new InvalidOperationException("所选文件夹中未找到可用图片（支持 jpg/jpeg/png/bmp）。");

        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new InvalidOperationException("输出路径无效。请重新选择输出文件。");

        Directory.CreateDirectory(outputDirectory);

        string concatFile = Path.Combine(Path.GetTempPath(), $"vfe_concat_{Guid.NewGuid():N}.txt");
        double frameDuration = 1.0 / Math.Max(0.1, fps);

        try
        {
            var lines = new List<string>(imageFiles.Count * 2 + 1);
            foreach (string image in imageFiles)
            {
                string escapedPath = image.Replace("'", "'\\''", StringComparison.Ordinal);
                lines.Add($"file '{escapedPath}'");
                lines.Add($"duration {frameDuration.ToString("0.######", CultureInfo.InvariantCulture)}");
            }

            string lastFileEscaped = imageFiles[^1].Replace("'", "'\\''", StringComparison.Ordinal);
            lines.Add($"file '{lastFileEscaped}'");

            await File.WriteAllLinesAsync(concatFile, lines, new UTF8Encoding(false), cancellationToken);

            var conversion = new Conversion();
            conversion.AddParameter($"-f concat -safe 0 -i \"{concatFile}\"", ParameterPosition.PreInput);
            conversion.AddParameter($"-r {fps.ToString("0.###", CultureInfo.InvariantCulture)}");
            conversion.AddParameter("-vf \"pad=ceil(iw/2)*2:ceil(ih/2)*2\"");
            conversion.AddParameter($"-c:v {codec}");
            if (string.Equals(codec, "libx264", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(codec, "libx265", StringComparison.OrdinalIgnoreCase))
            {
                conversion.AddParameter($"-crf {Math.Clamp(crf, 0, 51)}");
            }

            conversion.AddParameter("-pix_fmt yuv420p");
            conversion.SetOverwriteOutput(true);
            conversion.SetOutput(outputPath);
            conversion.OnProgress += (_, args) => progress?.Report(Math.Clamp(args.Percent, 0, 100));

            await conversion.Start(cancellationToken);
            progress?.Report(100);
            return outputPath;
        }
        finally
        {
            try
            {
                if (File.Exists(concatFile))
                {
                    File.Delete(concatFile);
                }
            }
            catch
            {
            }
        }
    }
}
