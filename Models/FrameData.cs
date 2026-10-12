using OpenCvSharp;

namespace ObjectDetectTrackApp.Models
{
    /// <summary>
    /// Данные одного кадра (с преобразованием в byte[] для WPF)
    /// </summary>
    public class FrameData
    {
        public Mat Frame { get; set; }
        public byte[] FrameBytes { get; set; } // Для отображения в WPF Image
        public int FrameIndex { get; set; }
        public double Timestamp { get; set; } // ms
        public int Width { get; set; }
        public int Height { get; set; }
    }
}