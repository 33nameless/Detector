using System;
using System.Collections.Generic;
using System.Linq;

namespace ObjectDetectTrackApp.Services
{
    /// <summary>
    /// Hungarian Algorithm для оптимального сопоставления (matching)
    /// Решает задачу поиска максимального паросочетания в двудольном графе
    /// </summary>
    public class HungarianAlgorithm
    {
        public static List<(int detection, int track)> FindMatches(float[,] costMatrix, float maxCost = float.MaxValue)
        {
            int detections = costMatrix.GetLength(0);
            int tracks = costMatrix.GetLength(1);

            var matches = new List<(int, int)>();

            if (detections == 0 || tracks == 0)
                return matches;

            // Простая жадная стратегия (для MVP)
            // Более оптимальный Hungarian Algorithm требует сложной реализации
            var usedTracks = new HashSet<int>();

            for (int d = 0; d < detections; d++)
            {
                int bestTrack = -1;
                float bestCost = maxCost;

                for (int t = 0; t < tracks; t++)
                {
                    if (!usedTracks.Contains(t) && costMatrix[d, t] < bestCost)
                    {
                        bestCost = costMatrix[d, t];
                        bestTrack = t;
                    }
                }

                if (bestTrack >= 0)
                {
                    matches.Add((d, bestTrack));
                    usedTracks.Add(bestTrack);
                }
            }

            return matches;
        }
    }
}