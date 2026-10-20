using OpenCvSharp;

namespace ObjectDetectTrackApp.Models
{
    /// <summary>
    /// Данные одного кадра. Frame — Mat в BGR (нативный OpenCV),
    /// FrameBytes — PNG в корректном порядке каналов для WPF Image.
    /// </summary>
    public class FrameData
    {
        public Mat Frame { get; set; }
        public byte[] FrameBytes { get; set; } // Для отображения в WPF Image
        public int FrameIndex { get; set; }
        public double Timestamp { get; set; } // ms
        public int Width { get; set; }
        public int Height { get; set; }
        // true — кадр из непрерывного прогона (Play): отрисовка не ждёт инференс
        public bool IsStream { get; set; }
    }
}