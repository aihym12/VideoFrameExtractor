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
    private readonly VideoFaceBlurService _videoFaceBlurService = new();
    private readonly ObservableCollection<BitmapImage> _previewImages = [];

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _videoBlurCts;
    private VideoInfo? _currentVideoInfo;
    private readonly List<string> _pendingFiles = [];
    private bool _isExtracting;
    private bool _isComposing;
    private bool _isVideoBlurring;

    private string? _lastOutputFolder;
    private string? _videoBlurSourcePath;
    private string? _previewImagePath;

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
        // 底部进度栏仅在"视频抽帧"标签页（0）显示
        ExtractBottomBar.Visibility = MainTabControl.SelectedIndex == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
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
            ProxyTextBox.Text = Properties.Settings.Default.ProxyAddress;

            string savedProxy = ProxyTextBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(savedProxy))
            {
                ProxyHelper.ApplyProxy(savedProxy);
            }

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

            // 加载人脸涂抹设置
            FaceBlurModeComboBox.SelectedIndex = Math.Clamp(Properties.Settings.Default.FaceBlurMode, 0, 1);
            FaceBlurStrengthSlider.Value = Math.Clamp(Properties.Settings.Default.FaceBlurStrength, 1, 100);
            FaceDetectionSensitivityComboBox.SelectedIndex = Math.Clamp(Properties.Settings.Default.FaceDetectionSensitivity, 0, 2);
            AutoBlurAfterExtractionCheckBox.IsChecked = Properties.Settings.Default.AutoBlurAfterExtraction;

            // 加载推理设备设置（视频涂抹 + 预览共用）
            int savedDevice = Math.Clamp(Properties.Settings.Default.OnnxInferenceDevice, 0, 1);
            VideoBlurDeviceComboBox.SelectedIndex = savedDevice;
            PreviewDeviceComboBox.SelectedIndex = savedDevice;
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
            Properties.Settings.Default.ProxyAddress = ProxyTextBox.Text.Trim();

            // 保存人脸涂抹设置
            Properties.Settings.Default.FaceBlurMode = FaceBlurModeComboBox.SelectedIndex;
            Properties.Settings.Default.FaceBlurStrength = (int)FaceBlurStrengthSlider.Value;
            Properties.Settings.Default.FaceDetectionSensitivity = FaceDetectionSensitivityComboBox.SelectedIndex;
            Properties.Settings.Default.AutoBlurAfterExtraction = AutoBlurAfterExtractionCheckBox.IsChecked == true;
            Properties.Settings.Default.OnnxInferenceDevice = VideoBlurDeviceComboBox.SelectedIndex;

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

                // 抽帧完成后自动执行人脸涂抹
                if (AutoBlurAfterExtractionCheckBox.IsChecked == true)
                {
                    // 确保 BiSeNet 模型存在（若缺失则提示下载）
                    if (!BiSeNetFaceParser.IsModelPresent())
                    {
                        var dlResult = MessageBox.Show(
                            $"人脸分割需要 BiSeNet 模型文件（{BiSeNetFaceParser.ModelFileName}），当前缺失。\n\n是否立即从网络下载？（约 50MB）",
                            "缺少 BiSeNet 人脸分割模型",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);
                        if (dlResult == MessageBoxResult.Yes)
                        {
                            var dlProgress = new Progress<string>(s => StatusTextBlock.Text = s);
                            await BiSeNetFaceParser.DownloadModelAsync(dlProgress, _cts.Token);
                        }
                    }

                    if (BiSeNetFaceParser.IsModelPresent())
                    {
                        var blurSettings = BuildFaceBlurSettings();
                        var blurProgress = new Progress<string>(status => StatusTextBlock.Text = status);
                        await _faceBlurService.BlurFacesInFolderAsync(result.OutputFolder, blurSettings, blurProgress, _cts.Token);
                    }
                }

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

    private void ApplyProxyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string proxyAddress = ProxyTextBox.Text.Trim();
            ProxyHelper.ApplyProxy(string.IsNullOrWhiteSpace(proxyAddress) ? null : proxyAddress);
            Properties.Settings.Default.ProxyAddress = proxyAddress;
            Properties.Settings.Default.Save();
            MessageBox.Show(
                string.IsNullOrWhiteSpace(proxyAddress) ? "已清除代理设置，使用系统默认网络配置。" : $"代理已设置为: {proxyAddress}",
                "代理设置",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.Error("代理设置失败", ex);
            MessageBox.Show($"代理设置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            var missingFiles = FaceBlurService.GetMissingModelFileNames();
            if (missingFiles.Count > 0)
            {
                string fileList = string.Join("\n", missingFiles.Select(f => $"  • {f}"));
                var downloadResult = MessageBox.Show(
                    $"人脸分割需要以下 BiSeNet 模型文件，当前缺失:\n{fileList}\n\n是否立即从网络下载？\n（约 50MB，下载完成后自动开始处理）",
                    "缺少 BiSeNet 人脸分割模型",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (downloadResult != MessageBoxResult.Yes)
                {
                    BlurFacesButton.IsEnabled = true;
                    StartButton.IsEnabled = true;
                    return;
                }

                var downloadProgress = new Progress<string>(status => StatusTextBlock.Text = status);
                StatusTextBlock.Text = "正在下载 BiSeNet 人脸分割模型...";
                await BiSeNetFaceParser.DownloadModelAsync(downloadProgress);
                StatusTextBlock.Text = "模型下载完成，开始处理...";
            }

            var blurSettings = BuildFaceBlurSettings();
            var progress = new Progress<string>(status => StatusTextBlock.Text = status);
            int modifiedCount = await _faceBlurService.BlurFacesInFolderAsync(targetFolder, blurSettings, progress);

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

    private FaceBlurSettings BuildFaceBlurSettings()
    {
        return new FaceBlurSettings
        {
            BlurMode = FaceBlurModeComboBox.SelectedIndex == 1 ? FaceBlurMode.Mosaic : FaceBlurMode.Gaussian,
            BlurStrength = (int)FaceBlurStrengthSlider.Value,
            Sensitivity = FaceDetectionSensitivityComboBox.SelectedIndex switch
            {
                0 => FaceDetectionSensitivity.Low,
                2 => FaceDetectionSensitivity.High,
                _ => FaceDetectionSensitivity.Medium
            },
            AutoBlurAfterExtraction = AutoBlurAfterExtractionCheckBox.IsChecked == true,
            // 图片批量涂抹标签页目前没有独立的设备选择器，固定使用 CPU。
            // 视频涂抹 / 预览标签页各自有 CPU/GPU 选择器（BuildVideoBlurSettings / BuildPreviewBlurSettings）。
            InferenceDevice = OnnxDevice.Cpu
        };
    }

    private FaceBlurSettings BuildVideoBlurSettings()
    {
        return new FaceBlurSettings
        {
            BlurMode = VideoBlurModeComboBox.SelectedIndex == 1 ? FaceBlurMode.Mosaic : FaceBlurMode.Gaussian,
            BlurStrength = (int)VideoBlurStrengthSlider.Value,
            Sensitivity = VideoBlurSensitivityComboBox.SelectedIndex switch
            {
                0 => FaceDetectionSensitivity.Low,
                2 => FaceDetectionSensitivity.High,
                _ => FaceDetectionSensitivity.Medium
            },
            InferenceDevice = VideoBlurDeviceComboBox.SelectedIndex == 1
                ? OnnxDevice.DirectML
                : OnnxDevice.Cpu
        };
    }

    private FaceBlurSettings BuildPreviewBlurSettings()
    {
        return new FaceBlurSettings
        {
            BlurMode = PreviewBlurModeComboBox.SelectedIndex == 1 ? FaceBlurMode.Mosaic : FaceBlurMode.Gaussian,
            BlurStrength = (int)PreviewBlurStrengthSlider.Value,
            Sensitivity = FaceDetectionSensitivity.Medium,
            InferenceDevice = PreviewDeviceComboBox.SelectedIndex == 1
                ? OnnxDevice.DirectML
                : OnnxDevice.Cpu
        };
    }

    private void FaceBlurConfig_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        SaveSettings();
    }

    private void FaceBlurConfig_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        SaveSettings();
    }

    private void FaceBlurStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
            return;

        FaceBlurStrengthValueTextBlock.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture);
        SaveSettings();
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
        _videoBlurCts?.Dispose();
        _videoFaceBlurService.Dispose();
        _faceBlurService.Dispose();
        base.OnClosed(e);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Tab 3：视频人脸涂抹
    // ══════════════════════════════════════════════════════════════════════════

    private void VideoBlurDropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            VideoBlurDropZoneBorder.BorderBrush = Brushes.DodgerBlue;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void VideoBlurDropZone_DragLeave(object sender, DragEventArgs e)
    {
        VideoBlurDropZoneBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xBD, 0xBD, 0xBD));
    }

    private void VideoBlurDropZone_Drop(object sender, DragEventArgs e)
    {
        VideoBlurDropZoneBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xBD, 0xBD, 0xBD));
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length > 0)
                LoadVideoBlurSource(files[0]);
        }
    }

    private void VideoBlurDropZone_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择源视频",
            Filter = "视频文件|*.mp4;*.avi;*.mov;*.mkv;*.wmv;*.flv;*.webm|所有文件|*.*"
        };
        if (dialog.ShowDialog() == true)
            LoadVideoBlurSource(dialog.FileName);
    }

    private void LoadVideoBlurSource(string path)
    {
        if (!File.Exists(path)) return;
        _videoBlurSourcePath = path;
        VideoBlurSourceInfoTextBlock.Text = Path.GetFileName(path);

        // 自动填充输出路径
        if (string.IsNullOrWhiteSpace(VideoBlurOutputPathTextBox.Text))
        {
            string dir = Path.GetDirectoryName(path) ?? ".";
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            VideoBlurOutputPathTextBox.Text = Path.Combine(dir, name + "_blurred" + ext);
        }
    }

    private void VideoBlurBrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "选择输出视频路径",
            Filter = "MP4 视频|*.mp4|所有文件|*.*",
            FileName = string.IsNullOrWhiteSpace(VideoBlurOutputPathTextBox.Text)
                ? "output_blurred.mp4"
                : Path.GetFileName(VideoBlurOutputPathTextBox.Text)
        };
        if (!string.IsNullOrWhiteSpace(VideoBlurOutputPathTextBox.Text))
            dialog.InitialDirectory = Path.GetDirectoryName(VideoBlurOutputPathTextBox.Text);
        if (dialog.ShowDialog() == true)
            VideoBlurOutputPathTextBox.Text = dialog.FileName;
    }

    private void VideoBlurStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        VideoBlurStrengthValueTextBlock.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture);
    }

    private async void VideoBlurStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isVideoBlurring) return;
        if (string.IsNullOrWhiteSpace(_videoBlurSourcePath) || !File.Exists(_videoBlurSourcePath))
        {
            MessageBox.Show("请先选择源视频文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string outputPath = VideoBlurOutputPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            MessageBox.Show("请指定输出视频路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 确保模型存在
        if (!BiSeNetFaceParser.IsModelPresent())
        {
            var dlResult = MessageBox.Show(
                $"人脸分割需要 BiSeNet 模型文件（{BiSeNetFaceParser.ModelFileName}），当前缺失。\n\n是否立即从网络下载？（约 50MB）",
                "缺少 BiSeNet 人脸分割模型",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (dlResult != MessageBoxResult.Yes) return;

            VideoBlurStatusTextBlock.Text = "正在下载 BiSeNet 模型...";
            try
            {
                var dlProg = new Progress<string>(s => VideoBlurStatusTextBlock.Text = s);
                await BiSeNetFaceParser.DownloadModelAsync(dlProg);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"模型下载失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        _isVideoBlurring = true;
        VideoBlurStartButton.IsEnabled = false;
        VideoBlurStopButton.IsEnabled = true;
        VideoBlurProgressBar.Value = 0;
        VideoBlurDropZoneBorder.IsHitTestVisible = false;

        _videoBlurCts = new CancellationTokenSource();

        try
        {
            var settings = BuildVideoBlurSettings();
            var progressHandler = new Progress<VideoBlurProgress>(p =>
            {
                VideoBlurStatusTextBlock.Text = p.TotalFrames > 0
                    ? p.ToString()
                    : "正在合并音频...";
                if (p.Percentage >= 0)
                    VideoBlurProgressBar.Value = p.Percentage;
            });

            VideoBlurStatusTextBlock.Text = "正在初始化...";
            await _videoFaceBlurService.BlurVideoAsync(
                _videoBlurSourcePath,
                outputPath,
                settings,
                progressHandler,
                _videoBlurCts.Token);

            VideoBlurProgressBar.Value = 100;
            VideoBlurStatusTextBlock.Text = "视频涂抹完成！";
            MessageBox.Show($"视频人脸涂抹完成！\n输出：{outputPath}",
                "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            VideoBlurStatusTextBlock.Text = "已取消";
        }
        catch (Exception ex)
        {
            Logger.Error("视频人脸涂抹失败", ex);
            VideoBlurStatusTextBlock.Text = "涂抹失败";
            MessageBox.Show($"视频人脸涂抹失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isVideoBlurring = false;
            VideoBlurStartButton.IsEnabled = true;
            VideoBlurStopButton.IsEnabled = false;
            VideoBlurDropZoneBorder.IsHitTestVisible = true;
            _videoBlurCts?.Dispose();
            _videoBlurCts = null;
        }
    }

    private void VideoBlurStopButton_Click(object sender, RoutedEventArgs e)
    {
        _videoBlurCts?.Cancel();
        VideoBlurStatusTextBlock.Text = "正在停止...";
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Tab 4：涂抹预览
    // ══════════════════════════════════════════════════════════════════════════

    private void PreviewBrowseImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择图片",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp|所有文件|*.*"
        };
        if (dialog.ShowDialog() == true)
            LoadPreviewImage(dialog.FileName);
    }

    private void PreviewOriginalImage_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length > 0)
                LoadPreviewImage(files[0]);
        }
    }

    private void LoadPreviewImage(string path)
    {
        if (!File.Exists(path)) return;
        _previewImagePath = path;
        PreviewImagePathTextBox.Text = path;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            PreviewOriginalImage.Source = bmp;
            PreviewBlurredImage.Source = null;
            PreviewStatusTextBlock.Text = "已加载图片，点击\"生成预览\"";
        }
        catch (Exception ex)
        {
            Logger.Warn($"加载预览图片失败: {ex.Message}");
        }
    }

    private void PreviewBlurStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        PreviewBlurStrengthValueTextBlock.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture);
    }

    private async void PreviewRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_previewImagePath) || !File.Exists(_previewImagePath))
        {
            MessageBox.Show("请先选择或拖入图片文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 确保模型存在
        if (!BiSeNetFaceParser.IsModelPresent())
        {
            var dlResult = MessageBox.Show(
                $"人脸分割需要 BiSeNet 模型文件（{BiSeNetFaceParser.ModelFileName}），当前缺失。\n\n是否立即从网络下载？（约 50MB）",
                "缺少 BiSeNet 人脸分割模型",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (dlResult != MessageBoxResult.Yes) return;

            PreviewStatusTextBlock.Text = "正在下载模型...";
            try
            {
                var dlProg = new Progress<string>(s => PreviewStatusTextBlock.Text = s);
                await BiSeNetFaceParser.DownloadModelAsync(dlProg);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"模型下载失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        PreviewRunButton.IsEnabled = false;
        PreviewStatusTextBlock.Text = "正在推理...";

        try
        {
            var settings = BuildPreviewBlurSettings();
            var blurredSource = await Task.Run(() =>
            {
                using var parser = new BiSeNetFaceParser(settings.InferenceDevice);
                using var image = OpenCvSharp.Cv2.ImRead(_previewImagePath, OpenCvSharp.ImreadModes.Color);
                if (image.Empty()) throw new InvalidOperationException("图片读取失败。");

                using var mask = parser.GetFaceMask(image, settings.Sensitivity);
                int total = image.Rows * image.Cols;
                int facePixels = OpenCvSharp.Cv2.CountNonZero(mask);
                if (facePixels >= total * FaceBlurConstants.MinFaceMaskRatio)
                {
                    // 直接用与 FaceBlurService 相同的软掩膜混合逻辑
                    using var soft = new OpenCvSharp.Mat();
                    mask.ConvertTo(soft, OpenCvSharp.MatType.CV_32F, 1.0 / 255.0);
                    OpenCvSharp.Cv2.GaussianBlur(soft, soft, new OpenCvSharp.Size(21, 21), 8.0);

                    if (settings.BlurMode == FaceBlurMode.Mosaic)
                    {
                        using var pts = new OpenCvSharp.Mat();
                        OpenCvSharp.Cv2.FindNonZero(mask, pts);
                        if (pts.Total() > 0)
                        {
                            var bbox = OpenCvSharp.Cv2.BoundingRect(pts);
                            int minDim = Math.Min(bbox.Width, bbox.Height);
                            int block = Math.Max(2, (int)(minDim * (0.05 + settings.BlurStrength / 100.0 * 0.25)));
                            using var mosaicFull = image.Clone();
                            using var roi = new OpenCvSharp.Mat(image, bbox);
                            using var small = new OpenCvSharp.Mat();
                            OpenCvSharp.Cv2.Resize(roi, small,
                                new OpenCvSharp.Size(Math.Max(1, bbox.Width / block), Math.Max(1, bbox.Height / block)),
                                interpolation: OpenCvSharp.InterpolationFlags.Linear);
                            using var mos = new OpenCvSharp.Mat();
                            OpenCvSharp.Cv2.Resize(small, mos,
                                new OpenCvSharp.Size(bbox.Width, bbox.Height),
                                interpolation: OpenCvSharp.InterpolationFlags.Nearest);
                            mos.CopyTo(new OpenCvSharp.Mat(mosaicFull, bbox));
                            BlendPreview(image, mosaicFull, soft);
                        }
                    }
                    else
                    {
                        int minDim2 = Math.Min(image.Rows, image.Cols);
                        int kBase = Math.Max(3, (int)(minDim2 * (0.05 + settings.BlurStrength / 100.0 * 0.15)));
                        int k = kBase % 2 == 0 ? kBase + 1 : kBase;
                        using var blurred2 = new OpenCvSharp.Mat();
                        OpenCvSharp.Cv2.GaussianBlur(image, blurred2, new OpenCvSharp.Size(k, k), 0);
                        BlendPreview(image, blurred2, soft);
                    }
                }

                OpenCvSharp.Cv2.ImEncode(".png", image, out byte[] buf);
                return buf;
            });

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new System.IO.MemoryStream(blurredSource);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            PreviewBlurredImage.Source = bmp;
            PreviewStatusTextBlock.Text = "预览完成";
        }
        catch (Exception ex)
        {
            Logger.Error("生成预览失败", ex);
            PreviewStatusTextBlock.Text = "预览失败";
            MessageBox.Show($"生成预览失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            PreviewRunButton.IsEnabled = true;
        }
    }

    private static void BlendPreview(OpenCvSharp.Mat image, OpenCvSharp.Mat effect, OpenCvSharp.Mat soft)
    {
        using var soft3 = new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.Merge([soft, soft, soft], soft3);
        using var imgF = new OpenCvSharp.Mat();
        using var effF = new OpenCvSharp.Mat();
        image.ConvertTo(imgF, OpenCvSharp.MatType.CV_32FC3);
        effect.ConvertTo(effF, OpenCvSharp.MatType.CV_32FC3);
        using var diff = new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.Subtract(effF, imgF, diff);
        using var blended = new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.Multiply(diff, soft3, blended);
        OpenCvSharp.Cv2.Add(imgF, blended, blended);
        blended.ConvertTo(image, OpenCvSharp.MatType.CV_8UC3);
    }
}
