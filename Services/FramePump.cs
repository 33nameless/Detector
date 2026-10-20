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

        /// <summary>Текущий источник — видео (для мини-таймлайна). Значения остаются
        /// и после остановки: таймлайн прогона виден, пока не выбран другой источник.</summary>
        public bool IsVideoSource { get; private set; }
        public int CurrentVideoFrameCount { get; private set; }

        public FramePump(FileSourceService fileSourceService, LogService logService)
        {
            _fileSourceService = fileSourceService;
            _logService = logService;
        }

        /// <summary>
        /// Показ конкретного кадра без запуска воспроизведения (для перехода из
        /// списка результатов). Для видео переоткрывает файл и делает seek на индекс.
        /// </summary>
        public void ShowFrameAt(string filePath, int frameIndex)
        {
            if (_isPlaying) return; // пусть сначала остановят прогон

            var prev = _pumpTask;
            _pumpTask = Task.Run(() =>
            {
                // Stop() больше не блокирует UI — позволить предыдущему прогону
                // корректно дотухнуть и отпустить capture до того, как мы его переоткроем
                try { prev?.Wait(3000); } catch { }
                if (_isPlaying) return; // пока ждали, стартовал полноценный прогон — он и покажет

                try
                {
                    if (_fileSourceService.IsImageFile(filePath))
                    {
                        IsVideoSource = false;
                        CurrentVideoFrameCount = 0;
                        if (_fileSourceService.OpenImage(filePath, out _, out _))
                        {
                            var f = _fileSourceService.GetCurrentImageFrame();
                            if (f != null) FrameReady?.Invoke(this, f);
                        }
                        return;
                    }

                    if (!_fileSourceService.IsVideoFile(filePath))
                    {
                        ErrorOccurred?.Invoke(this, "Unsupported file type");
                        return;
                    }

                    if (!_fileSourceService.OpenVideo(filePath, out int frameCount, out int fps, out _, out _))
                    {
                        ErrorOccurred?.Invoke(this, "Не удалось переоткрыть видео для перехода");
                        return;
                    }

                    IsVideoSource = true;
                    CurrentVideoFrameCount = frameCount;

                    int idx = Math.Clamp(frameIndex, 0, Math.Max(0, frameCount - 1));
                    if (!_fileSourceService.SeekFrame(idx))
                    {
                        ErrorOccurred?.Invoke(this, $"Seek к кадру {idx} не удался");
                        return;
                    }

                    if (_fileSourceService.ReadNextFrame(out var frameData))
                        FrameReady?.Invoke(this, frameData);
                    else
                        ErrorOccurred?.Invoke(this, $"Не удалось прочитать кадр {idx}");
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, "ShowFrameAt: " + ex.Message);
                }
            });
        }

        public void SetFps(int fps)
        {
            if (fps > 0)
                _frameDelayMs = Math.Max(1, 1000 / fps);
        }

        public void Play(string filePath, CancellationToken cancellationToken = default)
        {
            if (_isPlaying)
            {
                // Уже играем (возможно, на паузе) — повторный Play = снять паузу.
                // Раньше здесь был пустой return, из-за чего «Play» после паузы
                // не возобновлял прогон (помп так и висел в _isPaused).
                SetPaused(false);
                return;
            }

            _isPaused = false; // не тащим старую паузу в новый прогон
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _cancellationTokenSource = cts;
            var prev = _pumpTask;
            _pumpTask = Task.Run(() =>
            {
                // Прошлый прогон (если ещё дотухает после Stop) дожидается ЗДЕСЬ, на фоновом
                // потоке. Раньше это делалось синхронным Wait() в UI-потоке — и Stop()/экспорт
                // ловили дедлок: UI ждал помп, а помп в этот момент писал лог / слал Finished
                // в UI через Dispatcher. Теперь ни там, ни здесь взаимного ожидания нет.
                try { prev?.Wait(3000); } catch { }
                PlayInternal(filePath, cts.Token);
            });
        }

        public void Stop()
        {
            if (!_isPlaying)
                return;

            _cancellationTokenSource?.Cancel();
            _isPaused = false; // разбудить паузный цикл и не оставлять флаг «навешенным»
            // Никаких Wait() на вызывающем (UI) потоке: помп завершится за кадр сам,
            // а передача capture следующему прогону гарантирована prev?.Wait в Play().
            _isPlaying = false;
            _logService.Info("Playback stopped");
        }

        public void Pause()
        {
            if (!_isPlaying) return; // пауза «вхолостую» не должна оставлять взведённый флаг
            SetPaused(!_isPaused);
        }

        /// <summary>Признак паузы для VM (Play по той же записи должен возобновлять, а не «вешать»).</summary>
        public bool IsPaused => _isPaused && _isPlaying;

        public void SetPaused(bool paused)
        {
            if (_isPaused == paused) return;
            _isPaused = paused;
            _logService?.Info(_isPaused ? "Paused" : "Resumed");
        }

        public void Resume() => SetPaused(false);

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
            IsVideoSource = false;
            CurrentVideoFrameCount = 0;
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
            IsVideoSource = true;
            CurrentVideoFrameCount = frameCount;
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