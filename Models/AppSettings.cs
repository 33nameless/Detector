namespace ObjectDetectTrackApp.Models
{
    /// <summary>
    /// Настройки приложения
    /// </summary>
    public class AppSettings
    {
        public float ConfidenceThreshold { get; set; } = 0.45f;
        public float NmsThreshold { get; set; } = 0.45f;
        public string FilterClass { get; set; } = "All";
        public int MaxInferenceFreq { get; set; } = 10; // Детекция раз в N кадров
    }
}