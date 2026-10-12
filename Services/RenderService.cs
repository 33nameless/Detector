using System;
using System.Collections.Generic;
using OpenCvSharp;
using ObjectDetectTrackApp.Models;

namespace ObjectDetectTrackApp.Services
{
    public class RenderService
    {
        private readonly LogService _logService;

        public RenderService(LogService logService)
        {
            _logService = logService;
        }

        public Mat DrawDetections(Mat frame, List<Detection> detections)
        {
            if (detections == null || detections.Count == 0)
                return frame.Clone();

            Mat output = frame.Clone();

            foreach (var detection in detections)
            {
                try
                {
                    // Создаём Rect из detection
                    Rect boundingBox = new Rect(
                        detection.X,
                        detection.Y,
                        detection.Width,
                        detection.Height
                    );

                    // Рисуем bbox (зелёный)
                    Cv2.Rectangle(output, boundingBox, Scalar.Green, 2);
                }
                catch (Exception ex)
                {
                    _logService.Error($"Error drawing bbox: {ex.Message}");
                }
            }

            return output;
        }
    }
}