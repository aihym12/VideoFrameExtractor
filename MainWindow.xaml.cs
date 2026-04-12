using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Media.Animation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using VideoFrameExtractor.Helpers;
using VideoFrameExtractor.Models;
using VideoFrameExtractor.Services;

namespace VideoFrameExtractor;

/// <summary>
/// 主窗口交互逻辑
/// </summary>
public partial class MainWindow : Window
{
    private readonly VideoAnalyzer _videoAnalyzer = new();
    private readonly FrameExtractor _frameExtractor = new();
    private readonly ImageSequenceComposer _imageSequenceComposer = new();
    private readonly FaceBlurService _faceBlurService = new();
    private readonly ObservableCollection<BitmapImage> _previewImages = [];

    private CancellationTokenSource? _cts;
    private VideoInfo? _currentVideoInfo;
    private readonly List<string> _pendingFiles = [];
    private bool _isExtracting;
    private bool _isComposing;
    private string? _lastOutputFolder;

    public MainWindow()
    {
        InitializeComponent();
        PreviewItemsControl.ItemsSource = _previewImages;
        ComposeOutputPathTextBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "output.mp4");
        UpdateTabSpecificUi();
        LoadSettings();
        UpdateEstimatedOutput();
    }

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        if (e.Source is TabControl)
        {
            UpdateTabSpecificUi();
        }
    }

    private void UpdateTabSpecificUi()
    {
        bool isComposeTab = MainTabControl.SelectedIndex == 1;
        ExtractBottomBar.Visibility = isComposeTab ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LoadSettings()
    {
        try
        {
            OutputPathTextBox.Text = Properties.Settings.Default.LastOutputPath;
            GpuAccelerationCheckBox.IsChecked = Properties.Settings.Default.UseGpuAcceleration;
            AutoStartCheckBox.IsChecked = Properties.Settings.Default.AutoStart;
            OpenFolderCheckBox.IsChecked = Properties.Settings.Default.OpenFolderWhenDone;
            QualitySlider.Value = Properties.Settings.Default.DefaultQuality;

            if (Properties.Settings.Default.DefaultFrameRateMode == (int)FrameRateMode.Original)
                OriginalFpsRadioButton.IsChecked = true;
            else if (Properties.Settings.Default.DefaultFrameRateMode == (int)FrameRateMode.Smart)
                SmartFpsRadioButton.IsChecked = true;
            else
                FixedFpsRadioButton.IsChecked = true;

            string defaultFps = Properties.Settings.Default.DefaultFps.ToString(CultureInfo.InvariantCulture);
            foreach (var item in FixedFpsComboBox.Items.OfType<ComboBoxItem>())
            {
                if (item.Content?.ToString() == defaultFps)
                {
                    FixedFpsComboBox.SelectedItem = item;
                    break;
                }
            }

            if (Properties.Settings.Default.DefaultFormat.Equals("png", StringComparison.OrdinalIgnoreCase))
                PngFormatRadioButton.IsChecked = true;
            else
                JpgFormatRadioButton.IsChecked = true;

            NamingPatternComboBox.SelectedIndex = Math.Clamp(Properties.Settings.Default.DefaultNamingPattern, 0, 2);
            GpuModeComboBox.SelectedIndex = Math.Clamp(Properties.Settings.Default.DefaultGpuMode, 0, 3);
            GpuModeComboBox.IsEnabled = GpuAccelerationCheckBox.IsChecked == true;
            FixedFpsComboBox.IsEnabled = FixedFpsRadioButton.IsChecked == true;
            UpdateQualityState();
        }
        catch (Exception ex)
        {
            Logger.Warn($"加载设置失败: {ex.Message}");
        }
    }

    private void SaveSettings()
    {
        try
        {
            Properties.Settings.Default.LastOutputPath = OutputPathTextBox.Text.Trim();
            Properties.Settings.Default.UseGpuAcceleration = GpuAccelerationCheckBox.IsChecked == true;
            Properties.Settings.Default.AutoStart = AutoStartCheckBox.IsChecked == true;
            Properties.Settings.Default.OpenFolderWhenDone = OpenFolderCheckBox.IsChecked == true;
            Properties.Settings.Default.DefaultQuality = (int)QualitySlider.Value;
            Properties.Settings.Default.DefaultFormat = JpgFormatRadioButton.IsChecked == true ? "jpg" : "png";
            Properties.Settings.Default.DefaultNamingPattern = NamingPatternComboBox.SelectedIndex;
            Properties.Settings.Default.DefaultFrameRateMode = OriginalFpsRadioButton.IsChecked == true
                ? (int)FrameRateMode.Original
                : SmartFpsRadioButton.IsChecked == true
                    ? (int)FrameRateMode.Smart
                    : (int)FrameRateMode.Fixed;
            Properties.Settings.Default.DefaultFps = GetFixedFps();
            Properties.Settings.Default.DefaultGpuMode = GpuModeComboBox.SelectedIndex;
            Properties.Settings.Default.Save();
        }
        catch (Exception ex)
        {
            Logger.Warn($"保存设置失败: {ex.Message}");
        }
    }

    private async void DropZone_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
            return;

        var dialog = new OpenFileDialog
        {
            Title = "选择视频文件",
            Filter = "视频文件|*.mp4;*.avi;*.mov;*.mkv;*.wmv;*.flv;*.webm;*.m4v;*.ts|所有文件|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog() != true)
            return;

        var validFiles = dialog.FileNames.Where(PathHelper.IsSupportedVideoFormat).Distinct().ToList();

        if (validFiles.Count == 0)
        {
            MessageBox.Show("未检测到支持的视频格式。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _pendingFiles.Clear();
        _pendingFiles.AddRange(validFiles);

        try
        {
            await LoadVideoInfoAsync(_pendingFiles[0]);

            if (AutoStartCheckBox.IsChecked == true)
            {
                await StartExtractionAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("文件选择处理失败", ex);
            MessageBox.Show($"加载视频失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        bool isFileDrop = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = isFileDrop ? DragDropEffects.Copy : DragDropEffects.None;
        DropZoneBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isFileDrop ? "#1976D2" : "#BDBDBD"));
        DropZoneBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isFileDrop ? "#E3F2FD" : "#F5F5F5"));
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        DropZoneBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BDBDBD"));
        DropZoneBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5F5F5"));
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone_DragLeave(sender, e);

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
        var validFiles = files.Where(PathHelper.IsSupportedVideoFormat).Distinct().ToList();

        if (validFiles.Count == 0)
        {
            MessageBox.Show("未检测到支持的视频格式。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _pendingFiles.Clear();
        _pendingFiles.AddRange(validFiles);

        try
        {
            await LoadVideoInfoAsync(_pendingFiles[0]);

            if (AutoStartCheckBox.IsChecked == true)
            {
                await StartExtractionAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("拖拽处理失败", ex);
            MessageBox.Show($"加载视频失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadVideoInfoAsync(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("文件不存在", filePath);

        if (PathHelper.IsFileLocked(filePath))
            throw new IOException("文件被其他程序占用，请关闭后重试。");

        StatusTextBlock.Text = "正在解析视频信息...";
        _currentVideoInfo = await _videoAnalyzer.AnalyzeAsync(filePath);

        FileNameTextBlock.Text = _currentVideoInfo.FileName;
        ResolutionTextBlock.Text = _currentVideoInfo.Resolution;
        FpsTextBlock.Text = _currentVideoInfo.FrameRateText;
        DurationTextBlock.Text = _currentVideoInfo.DurationText;
        SizeTextBlock.Text = _currentVideoInfo.FileSizeText;

        DurationSlider.Maximum = Math.Max(1, Math.Ceiling(_currentVideoInfo.Duration.TotalSeconds));
        if (!double.TryParse(EndTimeTextBox.Text, out var end) || end > DurationSlider.Maximum)
        {
            EndTimeTextBox.Text = Math.Min(15, DurationSlider.Maximum).ToString("F0", CultureInfo.InvariantCulture);
        }

        OutputPathTextBox.Text = PathHelper.GetDefaultOutputFolder(filePath);

        StatusTextBlock.Text = "准备就绪";
        UpdateEstimatedOutput();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        await StartExtractionAsync();
    }

    private async Task StartExtractionAsync()
    {
        if (_isExtracting || _isComposing)
            return;

        if (_pendingFiles.Count == 0 && _currentVideoInfo == null)
        {
            MessageBox.Show("请先拖拽视频文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _isExtracting = true;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            List<string> filesToProcess = _pendingFiles.Count > 0
                ? [.. _pendingFiles]
                : [_currentVideoInfo!.FilePath];

            int totalFiles = filesToProcess.Count;

            for (int i = 0; i < totalFiles; i++)
            {
                string file = filesToProcess[i];
                _cts.Token.ThrowIfCancellationRequested();

                if (_currentVideoInfo == null || !string.Equals(_currentVideoInfo.FilePath, file, StringComparison.OrdinalIgnoreCase))
                {
                    await LoadVideoInfoAsync(file);
                }

                var settings = BuildCurrentSettings();
                var progress = new Progress<ExtractionProgress>(p =>
                {
                    AnimateProgress(p.Percentage);
                    StatusTextBlock.Text = $"[{i + 1}/{totalFiles}] 正在提取第 {p.CurrentFrame}/{p.TotalFrames} 帧...";
                });

                ExtractionResult result = await _frameExtractor.ExtractFramesAsync(
                    _currentVideoInfo!,
                    settings,
                    progress,
                    _cts.Token);

                _lastOutputFolder = result.OutputFolder;
                await LoadPreviewAsync(result.OutputFolder, settings.Format);

                if (OpenFolderCheckBox.IsChecked == true)
                {
                    OpenFolder(result.OutputFolder);
                }
            }

            StatusTextBlock.Text = "全部处理完成";
            MessageBox.Show("抽帧完成。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "操作已取消";
        }
        catch (Exception ex)
        {
            Logger.Error("抽帧失败", ex);
            StatusTextBlock.Text = "处理失败";
            MessageBox.Show($"提取失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isExtracting = false;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            AnimateProgress(0);
            _pendingFiles.Clear();
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private async void BlurFacesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExtracting || _isComposing)
        {
            MessageBox.Show("请先等待当前任务完成。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string targetFolder = ResolveFaceBlurTargetFolder();
        if (!Directory.Exists(targetFolder))
        {
            MessageBox.Show("未找到可处理的输出目录，请先执行一次抽帧。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        BlurFacesButton.IsEnabled = false;
        StartButton.IsEnabled = false;

        try
        {
            var progress = new Progress<string>(status => StatusTextBlock.Text = status);
            int modifiedCount = await _faceBlurService.BlurFacesInFolderAsync(targetFolder, progress);

            await LoadPreviewAsync(targetFolder, null);
            StatusTextBlock.Text = modifiedCount > 0
                ? $"人脸涂抹完成，已处理 {modifiedCount} 张图片"
                : "已完成扫描，未检测到人脸";

            MessageBox.Show(
                modifiedCount > 0
                    ? $"人脸自动涂抹完成，共处理 {modifiedCount} 张图片。"
                    : "未检测到可涂抹的人脸。",
                "人脸自动涂抹",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.Error("人脸涂抹失败", ex);
            StatusTextBlock.Text = "人脸涂抹失败";
            MessageBox.Show($"人脸涂抹失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BlurFacesButton.IsEnabled = true;
            StartButton.IsEnabled = true;
        }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (!string.IsNullOrWhiteSpace(OutputPathTextBox.Text) && Directory.Exists(OutputPathTextBox.Text))
        {
            dialog.InitialDirectory = OutputPathTextBox.Text;
        }

        if (dialog.ShowDialog() == true)
        {
            OutputPathTextBox.Text = dialog.FolderName;
        }
    }

    private void BrowseImageFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (!string.IsNullOrWhiteSpace(ImageFolderTextBox.Text) && Directory.Exists(ImageFolderTextBox.Text))
        {
            dialog.InitialDirectory = ImageFolderTextBox.Text;
        }

        if (dialog.ShowDialog() == true)
        {
            ImageFolderTextBox.Text = dialog.FolderName;

            if (string.IsNullOrWhiteSpace(ComposeOutputPathTextBox.Text))
            {
                string ext = GetComposeContainerExtension();
                ComposeOutputPathTextBox.Text = Path.Combine(dialog.FolderName, $"output.{ext}");
            }
        }
    }

    private void BrowseComposeOutputButton_Click(object sender, RoutedEventArgs e)
    {
        string ext = GetComposeContainerExtension();
        var dialog = new SaveFileDialog
        {
            Title = "选择输出视频文件",
            Filter = $"{ext.ToUpperInvariant()} 文件|*.{ext}|所有文件|*.*",
            FileName = $"output.{ext}",
            AddExtension = true,
            DefaultExt = ext
        };

        if (dialog.ShowDialog() == true)
        {
            ComposeOutputPathTextBox.Text = dialog.FileName;
        }
    }

    private async void ComposeVideoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExtracting || _isComposing)
            return;

        string folder = ImageFolderTextBox.Text.Trim();
        if (!Directory.Exists(folder))
        {
            MessageBox.Show("请选择有效的图片文件夹。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        double fps = Math.Clamp(ParseDouble(ComposeFpsComboBox.Text, 10), 1, 120);
        string outputPath = ComposeOutputPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = Path.Combine(folder, $"output.{GetComposeContainerExtension()}");
            ComposeOutputPathTextBox.Text = outputPath;
        }

        string codec = (ComposeCodecComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "libx264";
        int crf = (int)ComposeQualitySlider.Value;

        _isComposing = true;
        ComposeVideoButton.IsEnabled = false;
        ComposeStopButton.IsEnabled = true;
        ComposeProgressBar.Value = 0;
        ComposeStatusTextBlock.Text = "准备开始合成...";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<double>(p =>
            {
                ComposeProgressBar.Value = Math.Clamp(p, 0, 100);
                ComposeStatusTextBlock.Text = $"正在合成视频... {p:F0}%";
            });

            string result = await _imageSequenceComposer.ComposeAsync(
                folder,
                outputPath,
                fps,
                codec,
                crf,
                progress,
                _cts.Token);

            ComposeProgressBar.Value = 100;
            ComposeStatusTextBlock.Text = "图片合成视频完成";
            string? outputFolder = Path.GetDirectoryName(result);
            if (!string.IsNullOrWhiteSpace(outputFolder))
            {
                OpenFolder(outputFolder);
            }

            MessageBox.Show($"视频合成完成:\n{result}", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            ComposeStatusTextBlock.Text = "视频合成已取消";
        }
        catch (Exception ex)
        {
            Logger.Error("图片合成视频失败", ex);
            ComposeStatusTextBlock.Text = "视频合成失败";
            MessageBox.Show($"视频合成失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isComposing = false;
            ComposeVideoButton.IsEnabled = true;
            ComposeStopButton.IsEnabled = false;
        }
    }

    private void ComposeStopButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private void ComposeQualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
            return;

        ComposeQualityValueTextBlock.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture);
    }

    private void ComposeContainerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        string path = ComposeOutputPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return;

        string ext = GetComposeContainerExtension();
        ComposeOutputPathTextBox.Text = Path.ChangeExtension(path, ext);
    }

    private void DurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
            return;

        EndTimeTextBox.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture);
    }

    private void QualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
            return;

        QualityValueTextBlock.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture);
        UpdateEstimatedOutput();
    }

    private void FormatRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        UpdateQualityState();
        UpdateEstimatedOutput();
    }

    private void Config_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        if (GpuModeComboBox is not null && GpuAccelerationCheckBox is not null)
        {
            GpuModeComboBox.IsEnabled = GpuAccelerationCheckBox.IsChecked == true;
        }

        if (FixedFpsComboBox is not null && FixedFpsRadioButton is not null)
        {
            FixedFpsComboBox.IsEnabled = FixedFpsRadioButton.IsChecked == true;
        }

        if (IsLoaded)
        {
            UpdateEstimatedOutput();
        }
    }

    private void Config_Changed(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            UpdateEstimatedOutput();
        }
    }

    private void Config_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            UpdateEstimatedOutput();
        }
    }

    private ExtractionSettings BuildCurrentSettings()
    {
        double startTime = ParseDouble(StartTimeTextBox.Text, 0);
        double endTime = ParseDouble(EndTimeTextBox.Text, 15);
        if (endTime <= startTime)
            endTime = startTime + 1;

        if (_currentVideoInfo != null)
        {
            endTime = Math.Min(endTime, _currentVideoInfo.Duration.TotalSeconds);
        }

        return new ExtractionSettings
        {
            StartTime = Math.Max(0, startTime),
            EndTime = Math.Max(1, endTime),
            FrameRateMode = OriginalFpsRadioButton.IsChecked == true
                ? FrameRateMode.Original
                : SmartFpsRadioButton.IsChecked == true
                    ? FrameRateMode.Smart
                    : FrameRateMode.Fixed,
            FixedFps = GetFixedFps(),
            Format = JpgFormatRadioButton.IsChecked == true ? "jpg" : "png",
            Quality = (int)QualitySlider.Value,
            NamingPattern = NamingPatternComboBox.SelectedIndex switch
            {
                1 => NamingPattern.VideoName,
                2 => NamingPattern.Timestamp,
                _ => NamingPattern.Sequential
            },
            OutputPath = OutputPathTextBox.Text.Trim(),
            UseGpuAcceleration = GpuAccelerationCheckBox.IsChecked == true,
            GpuMode = GpuModeComboBox.SelectedIndex switch
            {
                1 => GpuAccelerationMode.Cuda,
                2 => GpuAccelerationMode.Qsv,
                3 => GpuAccelerationMode.AmdAmf,
                _ => GpuAccelerationMode.Auto
            },
            AutoStart = AutoStartCheckBox.IsChecked == true,
            OpenFolderWhenDone = OpenFolderCheckBox.IsChecked == true
        };
    }

    private void AnimateProgress(double target)
    {
        var animation = new DoubleAnimation
        {
            To = Math.Clamp(target, 0, 100),
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        ExtractionProgressBar.BeginAnimation(ProgressBar.ValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void UpdateEstimatedOutput()
    {
        try
        {
            var settings = BuildCurrentSettings();
            double seconds = Math.Max(1, settings.EndTime - settings.StartTime);
            double fps = settings.FrameRateMode switch
            {
                FrameRateMode.Original => _currentVideoInfo?.FrameRate ?? 30,
                FrameRateMode.Fixed => settings.FixedFps,
                FrameRateMode.Smart => FrameExtractor.CalculateSmartFps(seconds),
                _ => 10
            };

            int frameCount = FrameExtractor.EstimateFrameCount(seconds, fps);
            EstimatedFramesTextBlock.Text = $"{frameCount} 张图片（约 {fps:F1} FPS）";

            if (_currentVideoInfo is not null)
            {
                long estimatedSize = FrameExtractor.EstimateOutputSize(
                    frameCount,
                    _currentVideoInfo.Width,
                    _currentVideoInfo.Height,
                    settings.Format,
                    settings.Quality);
                EstimatedSizeTextBlock.Text = FormatBytes(estimatedSize);
            }
            else
            {
                EstimatedSizeTextBlock.Text = "-";
            }
        }
        catch
        {
            EstimatedFramesTextBlock.Text = "-";
            EstimatedSizeTextBlock.Text = "-";
        }
    }

    private async Task LoadPreviewAsync(string outputFolder, string? format)
    {
        await Task.Run(() =>
        {
            if (!Directory.Exists(outputFolder))
                return;

            IEnumerable<string> files = format?.Equals("jpg", StringComparison.OrdinalIgnoreCase) == true
                ? Directory.EnumerateFiles(outputFolder, "*.jpg").Concat(Directory.EnumerateFiles(outputFolder, "*.jpeg"))
                : format?.Equals("png", StringComparison.OrdinalIgnoreCase) == true
                    ? Directory.EnumerateFiles(outputFolder, "*.png")
                    : Directory.EnumerateFiles(outputFolder, "*.jpg")
                        .Concat(Directory.EnumerateFiles(outputFolder, "*.jpeg"))
                        .Concat(Directory.EnumerateFiles(outputFolder, "*.png"));

            var previewFiles = files
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .Take(5)
                .ToList();

            Dispatcher.Invoke(() =>
            {
                _previewImages.Clear();
                foreach (string file in previewFiles)
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(file);
                    image.EndInit();
                    image.Freeze();
                    _previewImages.Add(image);
                }
            });
        });
    }

    private void UpdateQualityState()
    {
        if (JpgFormatRadioButton is null || QualitySlider is null || QualityValueTextBlock is null)
            return;

        bool isJpg = JpgFormatRadioButton.IsChecked == true;
        QualitySlider.IsEnabled = isJpg;
        QualityValueTextBlock.Opacity = isJpg ? 1 : 0.5;
    }

    private int GetFixedFps()
    {
        string? value = FixedFpsComboBox.Text;
        if (!int.TryParse(value, out int fps))
        {
            fps = 10;
        }

        return Math.Clamp(fps, 1, 240);
    }

    private string ResolveFaceBlurTargetFolder()
    {
        if (!string.IsNullOrWhiteSpace(_lastOutputFolder))
            return _lastOutputFolder;

        if (!string.IsNullOrWhiteSpace(OutputPathTextBox.Text))
            return OutputPathTextBox.Text.Trim();

        if (_currentVideoInfo is not null)
            return PathHelper.GetDefaultOutputFolder(_currentVideoInfo.FilePath);

        return string.Empty;
    }

    private static double ParseDouble(string text, double fallback)
    {
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private string GetComposeContainerExtension()
    {
        return (ComposeContainerComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()?.ToLowerInvariant() ?? "mp4";
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        int order = 0;
        double value = bytes;
        while (value >= 1024 && order < sizes.Length - 1)
        {
            order++;
            value /= 1024;
        }

        return $"{value:0.##} {sizes[order]}";
    }

    private static void OpenFolder(string folderPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{folderPath}\"",
            UseShellExecute = true
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveSettings();
        _cts?.Dispose();
        base.OnClosed(e);
    }
}
