namespace ObjectDetectTrackApp.Models
{
    public class Track
    {
        public int Id { get; set; }
        public string ClassName { get; set; }
        public List<Detection> History { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public DateTime LastSeenAt { get; set; }
        public int HitStreak { get; set; } // Для SORT
        public int Age { get; set; } // Для SORT
    }
}