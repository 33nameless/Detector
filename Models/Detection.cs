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
        // Полигон instance-маски (режимы «Контур»/«Маска»): пары x,y в координатах кадра.
        public float[] MaskPolygon { get; set; }
        // Индекс анкорса в выходе seg-модели (для mask-коэффициентов), -1 если не задан
        public int AnchorIdx { get; set; } = -1;
        public int FrameIndex { get; set; } // Кадр источника (для перехода из списка результатов)
        // Ручной выбор (клик по превью): рисуем/экспортируем только выбранных, когда выбор непустой
        public bool IsSelected { get; set; }
        public DateTime Timestamp { get; set; }

        /// <summary>Готовая строка для списка справа: «person #3 | 0.87» или «bus | 0.90» без трека.</summary>
        public string DisplayLine => TrackId.HasValue
            ? $"{ClassName} #{TrackId.Value} | {Confidence:F2}"
            : $"{ClassName} | {Confidence:F2}";

        /// <summary>Строка для списка результатов всего видео: [f123] person #2 | 0.87.</summary>
        public string SessionLine => $"[f{FrameIndex}] " + DisplayLine;
    }
}