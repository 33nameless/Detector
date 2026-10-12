namespace ObjectDetectTrackApp.Models
{
    public class Detection
    {
        public int Id { get; set; }
        public string ClassName { get; set; }
        public float Confidence { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int? TrackId { get; set; } // Для SORT
        public DateTime Timestamp { get; set; }
    }
}