using System;
using System.Collections.Generic;
using System.Linq;
using ObjectDetectTrackApp.Models;

namespace ObjectDetectTrackApp.Services
{
    /// <summary>
    /// SORT (Simple Online and Realtime Tracking)
    /// Управляет жизненным циклом треков и сопоставляет детекции с треками
    /// </summary>
    public class TrackerService
    {
        private List<SortTrack> _activeTracks = new();
        private int _nextTrackId = 1;
        private readonly LogService _logService;

        private const int MaxAge = 30; // Макс кадров без детекции
        private const int MinHits = 3; // Минимум совпадений для подтверждения трека
        private const float IouThreshold = 0.3f;

        public TrackerService(LogService logService)
        {
            _logService = logService;
        }

        /// <summary>Кадр сортировки: сопоставить детекции с треками, проставить TrackId.</summary>
        public List<Detection> Update(List<Detection> detections)
        {
            // Predict (только «осиротевшие» треки, см. комментарий в их цикле ниже)
            foreach (var track in _activeTracks)
            {
                track.Predict();
            }

            // Сбрасываем id: ниже проставим актуальные (чтобы при повторном вызове
            // с той же детекцией не оставались устаревшие TrackId)
            foreach (var d in detections)
                d.TrackId = null;

            // Match detections to tracks
            var matches = MatchDetectionsToTracks(detections);

            // Update matched tracks
            foreach (var (detIdx, trackIdx) in matches)
            {
                var detection = detections[detIdx];
                var track = _activeTracks[trackIdx];

                track.Update(detection);
                detection.TrackId = track.Id;
            }

            // Manage unmatched detections (create new tracks)
            var matchedDetIndices = new HashSet<int>(matches.Select(m => m.detIdx));
            foreach (int detIdx in Enumerable.Range(0, detections.Count))
            {
                if (!matchedDetIndices.Contains(detIdx))
                {
                    var detection = detections[detIdx];
                    CreateNewTrack(detection);
                }
            }

            // Manage unmatched tracks (age them)
            var matchedTrackIndices = new HashSet<int>(matches.Select(m => m.trackIdx));
            foreach (int trackIdx in Enumerable.Range(0, _activeTracks.Count))
            {
                if (!matchedTrackIndices.Contains(trackIdx))
                {
                    _activeTracks[trackIdx].Age++;
                }
            }

            // Remove dead tracks
            _activeTracks = _activeTracks
                .Where(t => t.Age < MaxAge)
                .ToList();

            return detections;
        }

        private List<(int detIdx, int trackIdx)> MatchDetectionsToTracks(List<Detection> detections)
        {
            int numDet = detections.Count;
            int numTrack = _activeTracks.Count;

            if (numDet == 0 || numTrack == 0)
                return new List<(int, int)>();

            // Создаём матрицу стоимости (IoU-based)
            float[,] costMatrix = new float[numDet, numTrack];

            for (int d = 0; d < numDet; d++)
            {
                for (int t = 0; t < numTrack; t++)
                {
                    var track = _activeTracks[t];
                    var detection = detections[d];

                    // не склеиваем разные классы (иначе id может «перепрыгнуть»
                    // с автобуса на человека при пересечении боксов)
                    if (!string.Equals(track.ClassName, detection.ClassName, StringComparison.OrdinalIgnoreCase))
                    {
                        costMatrix[d, t] = 1.0f;
                        continue;
                    }

                    float iou = CalculateIou(detection, track.LastDetection);
                    costMatrix[d, t] = 1.0f - iou; // Стоимость = 1 - IoU
                }
            }

            // Сопоставляем используя Hungarian Algorithm
            var matches = HungarianAlgorithm.FindMatches(costMatrix, maxCost: 1.0f - IouThreshold);

            return matches;
        }

        private float CalculateIou(Detection d1, Detection d2)
        {
            int intersectLeft = Math.Max(d1.X, d2.X);
            int intersectTop = Math.Max(d1.Y, d2.Y);
            int intersectRight = Math.Min(d1.X + d1.Width, d2.X + d2.Width);
            int intersectBottom = Math.Min(d1.Y + d1.Height, d2.Y + d2.Height);

            if (intersectRight < intersectLeft || intersectBottom < intersectTop)
                return 0;

            int intersectArea = (intersectRight - intersectLeft) * (intersectBottom - intersectTop);
            int union = d1.Width * d1.Height + d2.Width * d2.Height - intersectArea;

            return (float)intersectArea / union;
        }

        private void CreateNewTrack(Detection detection)
        {
            var track = new SortTrack
            {
                Id = _nextTrackId++,
                ClassName = detection.ClassName,
                LastDetection = detection
            };

            // id объект получает сразу (а не со второго кадра) — иначе на статичном
            // изображении номера никогда бы не появились
            detection.TrackId = track.Id;

            track.KalmanFilter.Init(
                detection.X + detection.Width / 2.0f,
                detection.Y + detection.Height / 2.0f,
                detection.Width,
                detection.Height
            );

            _activeTracks.Add(track);
        }

        public int GetActiveTrackCount() => _activeTracks.Count;

        /// <summary>Полный сброс (новый источник/новое воспроизведение).</summary>
        public void Reset()
        {
            _activeTracks.Clear();
            _nextTrackId = 1;
        }
    }

    /// <summary>
    /// Один трек (объект который отслеживается)
    /// </summary>
    public class SortTrack
    {
        public int Id { get; set; }
        public string ClassName { get; set; }
        public Detection LastDetection { get; set; }
        public int Age { get; set; } // Кадров без обновления
        public int HitStreak { get; set; } // Последовательных совпадений
        public KalmanFilter KalmanFilter { get; set; } = new();

        public void Predict()
        {
            KalmanFilter.Predict();
        }

        public void Update(Detection detection)
        {
            LastDetection = detection;
            HitStreak++;
            Age = 0;

            KalmanFilter.Update(
                detection.X + detection.Width / 2.0f,
                detection.Y + detection.Height / 2.0f,
                detection.Width,
                detection.Height
            );
        }
    }
}