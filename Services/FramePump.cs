using System;
using System.Threading;
using System.Threading.Tasks;
using ObjectDetectTrackApp.Models;

namespace ObjectDetectTrackApp.Services
{
    public class FramePump
    {
        private readonly FileSourceService _fileSourceService;
        private readonly LogService _logService;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _pumpTask;
        private bool _isPlaying;
        private bool _isPaused;
        private int _frameDelayMs = 33; // ~30 fps

        public event EventHandler<FrameData> FrameReady;
        public event EventHandler PlaybackFinished;
        public event EventHandler<string> ErrorOccurred;

        public bool IsPlaying => _isPlaying;

        public FramePump(FileSourceService fileSourceService, LogService logService)
        {
            _fileSourceService = fileSourceService;
            _logService = logService;
        }

        public void SetFps(int fps)
        {
            if (fps > 0)
                _frameDelayMs = Math.Max(1, 1000 / fps);
        }

        public void Play(string filePath, CancellationToken cancellationToken = default)
        {
            if (_isPlaying)
                return;

            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pumpTask = Task.Run(() => PlayInternal(filePath, _cancellationTokenSource.Token));
        }

        public void Stop()
        {
            if (!_isPlaying)
                return;

            _cancellationTokenSource?.Cancel();
            try
            {
                _pumpTask?.Wait(5000);
            }
            catch { }
            _isPlaying = false;
            _logService.Info("Playback stopped");
        }

        public void Pause()
        {
            _isPaused = !_isPaused;
            _logService.Info(_isPaused ? "Paused" : "Resumed");
        }

        private void PlayInternal(string filePath, CancellationToken ct)
        {
            try
            {
                _isPlaying = true;

                if (_fileSourceService.IsImageFile(filePath))
                {
                    PlayImage(filePath);
                }
                else if (_fileSourceService.IsVideoFile(filePath))
                {
                    PlayVideo(filePath, ct);
                }
                else
                {
                    ErrorOccurred?.Invoke(this, "Unsupported file type");
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, ex.Message);
            }
            finally
            {
                _isPlaying = false;
                PlaybackFinished?.Invoke(this, EventArgs.Empty);
            }
        }

        private void PlayImage(string filePath)
        {
            if (!_fileSourceService.OpenImage(filePath, out int width, out int height))
            {
                ErrorOccurred?.Invoke(this, $"Failed to open image: {filePath}");
                return;
            }

            var frameData = _fileSourceService.GetCurrentImageFrame();
            FrameReady?.Invoke(this, frameData);

            _logService.Info($"Image displayed: {width}x{height}");
        }

        private void PlayVideo(string filePath, CancellationToken ct)
        {
            if (!_fileSourceService.OpenVideo(filePath,
                out int frameCount, out int fps, out int width, out int height))
            {
                ErrorOccurred?.Invoke(this, $"Failed to open video: {filePath}");
                return;
            }

            SetFps(fps);
            _logService.Info($"Video playback started: {frameCount} frames @ {fps} fps");

            int frameIdx = 0;
            while (!ct.IsCancellationRequested && frameIdx < frameCount)
            {
                // Пауза
                while (_isPaused && !ct.IsCancellationRequested)
                {
                    Thread.Sleep(100);
                }

                if (ct.IsCancellationRequested)
                    break;

                // Читаем кадр
                if (_fileSourceService.ReadNextFrame(out var frameData))
                {
                    FrameReady?.Invoke(this, frameData);
                    frameIdx++;

                    // Ждём перед следующим кадром
                    Thread.Sleep(_frameDelayMs);
                }
                else
                {
                    break;
                }
            }

            _fileSourceService.Close();
            _logService.Info($"Video playback finished: {frameIdx}/{frameCount} frames");
        }
    }
}