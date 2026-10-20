using System;
using System.Collections.Generic;
using ObjectDetectTrackApp.Models;

namespace ObjectDetectTrackApp.Services
{
    /// <summary>
    /// Чистая логика ручного выбора объектов (клик по превью) и «захвата» выбора
    /// на последующих кадрах. Без WPF/OpenCV — чтобы тестировалась на любой платформе.
    /// </summary>
    public static class SelectionLogic
    {
        /// <summary>Порог IoU для повторного сопоставления выбранного объекта (без трека).</summary>
        public const float IouThresh = 0.30f;

        public sealed class SelectionItem
        {
            public string ClassName;
            public int X, Y, W, H;
            public int LastTrackId = -1;
            // Был ли подобран детекцией в последнем кадре (для «потерян/найден» в логах)
            public bool LastMatched = true;

            public static SelectionItem From(Detection d) => new SelectionItem
            {
                ClassName = d.ClassName, X = d.X, Y = d.Y, W = d.Width, H = d.Height,
                LastTrackId = d.TrackId ?? -1
            };

            public SelectionItem Clone() => new SelectionItem
            {
                ClassName = ClassName, X = X, Y = Y, W = W, H = H, LastTrackId = LastTrackId
            };
        }

        /// <summary>Луч-кастинг: poly — плоский массив пар x,y. Полигон «дырявый» не важен — внешние границы.</summary>
        public static bool PointInPolygon(float[] poly, double px, double py)
        {
            if (poly == null || poly.Length < 6) return false;
            bool inside = false;
            int n = poly.Length / 2;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = poly[2 * i], yi = poly[2 * i + 1];
                double xj = poly[2 * j], yj = poly[2 * j + 1];
                if (((yi > py) != (yj > py)) &&
                    (px < (xj - xi) * (py - yi) / (yj - yi) + xi))
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>Хит-тест детекции: сначала маска (полигон), иначе — бокс.</summary>
        public static bool HitDetection(Detection d, double px, double py)
        {
            if (d == null) return false;
            if (d.MaskPolygon != null && PointInPolygon(d.MaskPolygon, px, py)) return true;
            return px >= d.X && px <= d.X + d.Width && py >= d.Y && py <= d.Y + d.Height;
        }

        private static bool InPoly(Detection d, double px, double py) =>
            d.MaskPolygon != null && PointInPolygon(d.MaskPolygon, px, py);

        /// <summary>
        /// Индекс объекта под точкой. Приоритет: (1) курсор внутри полигона маски,
        /// (2) меньшая площадь бокса (верхний объект), (3) выше confidence. -1 — мимо.
        /// </summary>
        public static int HitTest(IReadOnlyList<Detection> dets, double px, double py)
        {
            if (dets == null) return -1;
            int best = -1;
            int bestScore = 2;
            float bestArea = float.MaxValue;
            float bestConf = -1f;
            for (int i = 0; i < dets.Count; i++)
            {
                var d = dets[i];
                if (!HitDetection(d, px, py)) continue;
                int score = InPoly(d, px, py) ? 0 : 1;
                float area = (float)Math.Max(1, d.Width) * Math.Max(1, d.Height);
                bool better = best == -1
                    || score < bestScore
                    || (score == bestScore && area < bestArea)
                    || (score == bestScore && Math.Abs(area - bestArea) < 1e-6f && d.Confidence > bestConf);
                if (better) { best = i; bestScore = score; bestArea = area; bestConf = d.Confidence; }
            }
            return best;
        }

        public static float Iou(int ax, int ay, int aw, int ah, int bx, int by, int bw, int bh)
        {
            long ix = Math.Max(0, Math.Min(ax + aw, bx + bw) - Math.Max(ax, bx));
            long iy = Math.Max(0, Math.Min(ay + ah, by + bh) - Math.Max(ay, by));
            long inter = ix * iy;
            long uni = (long)aw * ah + (long)bw * bh - inter;
            return uni > 0 ? (float)(inter / (double)uni) : 0f;
        }

        /// <summary>Тот же ли это объект, что и сохранённый выбор: класс + (TrackId или IoU).</summary>
        public static bool SameItem(Detection d, SelectionItem it)
        {
            if (it == null || d == null) return false;
            if (!string.Equals(d.ClassName, it.ClassName, StringComparison.OrdinalIgnoreCase)) return false;
            if (it.LastTrackId > 0 && d.TrackId == it.LastTrackId) return true;
            return Iou(d.X, d.Y, d.Width, d.Height, it.X, it.Y, it.W, it.H) >= IouThresh;
        }

        /// <summary>
        /// Пометить IsSelected те детекции, что совпали с выбором; обновить в items
        /// бокс/TrackId (захват на ходу). Один item = максимум одна детекция за кадр.
        /// </summary>
        public static int Apply(IReadOnlyList<Detection> dets, IList<SelectionItem> items)
        {
            if (dets == null || items == null) return 0;
            int matched = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                it.LastMatched = false;
                for (int k = 0; k < dets.Count; k++)
                {
                    var d = dets[k];
                    if (d.IsSelected) continue;
                    if (SameItem(d, it))
                    {
                        d.IsSelected = true;
                        it.X = d.X; it.Y = d.Y; it.W = d.Width; it.H = d.Height;
                        if (d.TrackId is int tid && tid > 0) it.LastTrackId = tid;
                        it.LastMatched = true;
                        matched++;
                        break;
                    }
                }
            }
            return matched;
        }

        /// <summary>Явная установка флага «следить» для объекта (чекбокс в списке DETECTIONS).</summary>
        public static bool Set(IList<SelectionItem> items, Detection d, bool on)
        {
            if (items == null || d == null) return false;
            int idx = -1;
            for (int i = 0; i < items.Count; i++)
                if (SameItem(d, items[i])) { idx = i; break; }
            if (on)
            {
                if (idx >= 0) return false;
                items.Add(SelectionItem.From(d));
                return true;
            }
            if (idx < 0) return false;
            items.RemoveAt(idx);
            return true;
        }

        /// <summary>
        /// Логика клика: обычный клик — «только этот объект» (повторный клик по
        /// единственному выбранному — сброс), Ctrl+клик — добавить/убрать.
        /// Возвращает размер выбора после действия.
        /// </summary>
        public static int Toggle(IList<SelectionItem> items, Detection d, bool additive)
        {
            if (items == null || d == null) return items?.Count ?? 0;
            int idx = -1;
            for (int i = 0; i < items.Count; i++)
                if (SameItem(d, items[i])) { idx = i; break; }

            // обычный клик по единственному выбранному = снять выбор
            if (!additive && idx >= 0 && items.Count == 1) { items.Clear(); return 0; }
            if (!additive) items.Clear();
            if (additive && idx >= 0) items.RemoveAt(idx);   // ctrl+клик по выбранному — убрать
            else items.Add(SelectionItem.From(d));           // добавить / зафиксировать
            return items.Count;
        }
    }
}
