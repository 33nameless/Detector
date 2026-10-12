using System;
using System.Windows;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ObjectDetectTrackApp.Models;
using ObjectDetectTrackApp.Services;

namespace ObjectDetectTrackApp.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly FileSourceService _fileSourceService;
        private readonly LogService _logService;
        private readonly FramePump _framePump;
        private DetectorService _detectorService;
        private CancellationTokenSource _cancellationTokenSource;

        public LogViewModel LogViewModel { get; }
        public SourcesViewModel SourcesViewModel { get; }

        [ObservableProperty]
        public BitmapImage previewImage;

        [ObservableProperty]
        public string statusText = "Ready";

        [ObservableProperty]
        public double fps = 0.0;

        [ObservableProperty]
        public string resolution = "-";

        [ObservableProperty]
        public bool isPlaying;

        [ObservableProperty]
        public ObservableCollection<Detection> detections = new();

        [ObservableProperty]
        public float confidenceThreshold = 0.45f;

        [ObservableProperty]
        public float nmsThreshold = 0.45f;

        [ObservableProperty]
        public bool filterPersonOnly = false;

        [ObservableProperty]
        public int activeTracksCount = 0;

        public ICommand PlayCommand { get; }
        public ICommand PauseCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand ExportJsonCommand { get; }
        public ICommand ExportCsvCommand { get; }

        public MainViewModel()
        {
            _logService = new LogService();
            _fileSourceService = new FileSourceService(_logService);
            _framePump = new FramePump(_fileSourceService, _logService);

            LogViewModel = new LogViewModel(_logService);
            SourcesViewModel = new SourcesViewModel(_fileSourceService, _logService);

            PlayCommand = new AsyncRelayCommand(PlayAsync);
            PauseCommand = new RelayCommand(() => _framePump.Pause());
            StopCommand = new RelayCommand(Stop);
            ExportJsonCommand = new RelayCommand(() => _logService.Info("Export JSON not implemented yet"));
            ExportCsvCommand = new RelayCommand(() => _logService.Info("Export CSV not implemented yet"));

            InitializeDetector();

            _framePump.FrameReady += FramePump_FrameReady;
            _framePump.PlaybackFinished += FramePump_PlaybackFinished;
            _framePump.ErrorOccurred += FramePump_ErrorOccurred;

            _logService.Info("✓ Application initialized");
        }

        private void InitializeDetector()
        {
            try
            {
                string modelPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Assets",
                    "TinyYolo2_model.onnx");

                _logService.Info($"Model path: {modelPath}");
                _logService.Info($"Model exists: {File.Exists(modelPath)}");

                if (!File.Exists(modelPath))
                {
                    _logService.Warn("Model file not found! Detection will not work.");
                    _logService.Warn("Please download TinyYolo2_model.onnx and place in Assets/");
                    return;
                }

                _detectorService = new DetectorService(modelPath, _logService);
                _logService.Info("✓ Detector initialized successfully");
            }
            catch (Exception ex)
            {
                _logService.Error($"Detector init failed: {ex.Message}");
            }
        }

        private async Task PlayAsync()
        {
            var source = SourcesViewModel.SelectedSource;
            if (source == null)
            {
                _logService.Warn("No source selected");
                return;
            }

            _cancellationTokenSource = new CancellationTokenSource();
            IsPlaying = true;
            StatusText = $"Playing: {source.FileName}";

            _framePump.Play(source.FilePath, _cancellationTokenSource.Token);
        }

        private void Stop()
        {
            _framePump.Stop();
            _cancellationTokenSource?.Cancel();
            IsPlaying = false;
            StatusText = "Stopped";
        }

        private void FramePump_FrameReady(object sender, FrameData frameData)
        {
            try
            {
                // Преобразуем byte[] в BitmapImage для WPF
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = new System.IO.MemoryStream(frameData.FrameBytes);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();

                PreviewImage = bitmap;
                Resolution = $"{frameData.Width}x{frameData.Height}";
                StatusText = $"Frame {frameData.FrameIndex}";

                // Детекция (будет реализована в итерации 3)
                if (_detectorService != null)
                {
                    var dets = _detectorService.Detect(frameData.Frame, ConfidenceThreshold);

                    Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        Detections.Clear();
                        foreach (var det in dets)
                        {
                            Detections.Add(det);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logService.Error($"Frame processing error: {ex.Message}");
            }
        }

        private void FramePump_PlaybackFinished(object sender, EventArgs e)
        {
            IsPlaying = false;
            StatusText = "Playback finished";
        }

        private void FramePump_ErrorOccurred(object sender, string errorMessage)
        {
            _logService.Error(errorMessage);
            IsPlaying = false;
        }
    }
}