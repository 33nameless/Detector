using System;
using System.IO;
using OpenCvSharp;
using ObjectDetectTrackApp.Models;

namespace ObjectDetectTrackApp.Services
{
    public class FileSourceService
    {
        private readonly LogService _logService;
        private VideoCapture _videoCapture;
        private Mat _currentFrame;

        public FileSourceService(LogService logService)
        {
            _logService = logService;
        }

        public bool IsVideoFile(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLower();
            return ext is ".mp4" or ".avi" or ".mov" or ".mkv" or ".flv" or ".wmv";
        }

        public bool IsImageFile(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLower();
            return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".tiff";
        }

        public bool OpenVideo(string filePath, out int frameCount, out int fps, out int width, out int height)
        {
            frameCount = 0;
            fps = 0;
            width = 0;
            height = 0;

            try
            {
                _videoCapture?.Dispose();
                _videoCapture = new VideoCapture(filePath);

                if (!_videoCapture.IsOpened())
                {
                    _logService.Error($"Failed to open video: {filePath}");
                    return false;
                }

                frameCount = (int)_videoCapture.Get(VideoCaptureProperties.FrameCount);
                fps = (int)_videoCapture.Get(VideoCaptureProperties.Fps);
                width = (int)_videoCapture.Get(VideoCaptureProperties.FrameWidth);
                height = (int)_videoCapture.Get(VideoCaptureProperties.FrameHeight);

                _logService.Info($"Video opened: {Path.GetFileName(filePath)} ({width}x{height}, {frameCount} frames, {fps} fps)");
                return true;
            }
            catch (Exception ex)
            {
                _logService.Error($"Exception opening video: {ex.Message}");
                return false;
            }
        }

        public bool OpenImage(string filePath, out int width, out int height)
        {
            width = 0;
            height = 0;

            try
            {
                _currentFrame = Cv2.ImRead(filePath);
                if (_currentFrame.Empty())
                {
                    _logService.Error($"Failed to open image: {filePath}");
                    return false;
                }

                width = _currentFrame.Width;
                height = _currentFrame.Height;
                _logService.Info($"Image opened: {Path.GetFileName(filePath)} ({width}x{height})");
                return true;
            }
            catch (Exception ex)
            {
                _logService.Error($"Exception opening image: {ex.Message}");
                return false;
            }
        }

        public bool ReadNextFrame(out FrameData frameData, bool rawOnly = false)
        {
            frameData = null;

            if (_videoCapture == null || !_videoCapture.IsOpened())
                return false;

            Mat frame = new Mat();
            if (!_videoCapture.Read(frame) || frame.Empty())
            {
                frame.Dispose();
                return false;
            }

            int frameIndex = (int)_videoCapture.Get(VideoCaptureProperties.PosFrames) - 1;
            double timestamp = _videoCapture.Get(VideoCaptureProperties.PosMsec);

            frameData = CreateFrameData(frame, frameIndex, timestamp, encodeBytes: !rawOnly);
            frame.Dispose();

            return true;
        }

        public FrameData GetCurrentImageFrame()
        {
            if (_currentFrame == null || _currentFrame.Empty())
                return null;

            return CreateFrameData(_currentFrame, 0, 0);
        }

        public bool SeekFrame(int frameIndex)
        {
            if (_videoCapture == null || !_videoCapture.IsOpened())
                return false;

            try
            {
                _videoCapture.Set(VideoCaptureProperties.PosFrames, frameIndex);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Close()
        {
            _videoCapture?.Dispose();
            _currentFrame?.Dispose();
            _videoCapture = null;
            _currentFrame = null;
        }

        private FrameData CreateFrameData(Mat frame, int frameIndex, double timestamp, bool encodeBytes = true)
        {
            // frame — BGR (нативный порядок каналов OpenCV), и оставляем BGR.
            // Раньше кадр сначала переводили в RGB, а потом кодировали ImEncode,
            // который ждёт именно BGR — из-за этого на экране менялись местами
            // красный и синий («инверсия»). Конверсия в RGB для сети делается
            // внутри DetectorService,Так всё и задумано: BGR.
            // PNG на каждый кадр прогона стоил 10-30мс — для стрима не кодируем вовсе
            byte[] imageBytes = encodeBytes ? frame.ToBytes(".png") : null;

            var frameData = new FrameData
            {
                Frame = frame.Clone(), // BGR
                FrameBytes = imageBytes, // PNG из BGR -> корректные цвета
                FrameIndex = frameIndex,
                Timestamp = timestamp,
                Width = frame.Width,
                Height = frame.Height
            };
            return frameData;
        }
    }
}