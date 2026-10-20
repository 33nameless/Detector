using System;
using System.Collections.Generic;
using System.Linq;
using ObjectDetectTrackApp.Models;

namespace ObjectDetectTrackApp.Services
{
    /// <summary>Формат выходного тензора YOLO-модели.</summary>
    public enum YoloOutputFormat
    {
        Unsupported,      // например старый YOLOv2 [1,425,13,13] — тут же «месиво», если декодировать неверно
        End2End,          // [1, 300, 6]  — экспорт ultralytics с nms=True (x1,y1,x2,y2,score,cls)
        Transposed,       // [1, 4+nc, N] — YOLOv8/v11: каналы вперёд, координаты в пикселях входа
        RowMajor          // [1, N, 5+nc] — YOLOv5: строки-анкоры, obj*cls
    }

    /// <summary>
    /// Чистый (без зависимостей от ONNX/OpenCV) постпроцессор выходов YOLO:
    /// определяет формат тензора, декодирует боксы, пересчитывает координаты
    /// из letterbox-входа в пиксели оригинального кадра, применяет NMS.
    /// Выделен в отдельный класс, чтобы его можно было тестировать без модели.
    /// </summary>
    public static class YoloPostprocess
    {
        public readonly struct Letterbox
        {
            public readonly float Scale;
            public readonly float PadX;
            public readonly float PadY;

            public Letterbox(float scale, float padX, float padY)
            {
                Scale = scale; PadX = padX; PadY = padY;
            }

            /// <summary>Центр+размер в координатах входа сети -> Rect в пикселях оригинала (обрезка по границам).</summary>
            public bool MapCxCy(float cx, float cy, float w, float h, int origW, int origH,
                                out int x, out int y, out int ww, out int hh)
            {
                float x0 = (cx - w / 2f - PadX) / Scale;
                float x1 = (cx + w / 2f - PadX) / Scale;
                float y0 = (cy - h / 2f - PadY) / Scale;
                float y1 = (cy + h / 2f - PadY) / Scale;

                x = (int)MathF.Floor(Math.Clamp(x0, 0, origW));
                y = (int)MathF.Floor(Math.Clamp(y0, 0, origH));
                int rx = (int)MathF.Ceiling(Math.Clamp(x1, 0, origW));
                int ry = (int)MathF.Ceiling(Math.Clamp(y1, 0, origH));
                ww = rx - x; hh = ry - y;
                return ww > 1 && hh > 1;
            }

            /// <summary>x1,y1,x2,y2 (end2end) -> Rect оригинала.</summary>
            public bool MapXyxy(float x1n, float y1n, float x2n, float y2n, int origW, int origH,
                                out int x, out int y, out int ww, out int hh)
                => MapCxCy((x1n + x2n) / 2f, (y1n + y2n) / 2f, Math.Abs(x2n - x1n), Math.Abs(y2n - y1n),
                           origW, origH, out x, out y, out ww, out hh);
        }

        /// <summary>Определить формат выхода по размерностям тензора.</summary>
        public static YoloOutputFormat DetectFormat(int[] dims)
        {
            // Ожидаем 3D: [1, A, B]
            if (dims == null || dims.Length != 3 || dims[0] != 1) return YoloOutputFormat.Unsupported;
            int a = dims[1], b = dims[2];

            if (b == 6 && a >= 10) return YoloOutputFormat.End2End;      // [1,300,6]
            if (a >= 5 && a <= 320 && b > a) return YoloOutputFormat.Transposed; // [1,84,8400], [1,135,17640]...
            if (b >= 5 && b <= 320 && a > b) return YoloOutputFormat.RowMajor;   // [1,25200,85]...
            return YoloOutputFormat.Unsupported;
        }

        /// <summary>
        /// Декодирование + NMS. className(id) — человекочитаемое имя класса.
        /// </summary>
        public static List<Detection> Decode(float[] data, int[] dims, int origW, int origH,
                                             in Letterbox lb, float confThresh, float nmsThresh,
                                             Func<int, string> className, int classesOverride = 0)
        {
            // classesOverride > 0 — seg-модель: реальных классов N, а строк 4+N+32
            // (32 последние — коэффициенты маски, НЕ классы).
            bool withCoeffs = classesOverride > 0 && dims != null && dims.Length == 3 &&
                              dims[1] == 4 + classesOverride + 32;
            var raw = new List<Detection>();
            var fmt = DetectFormat(dims);

            switch (fmt)
            {
                case YoloOutputFormat.Transposed:
                {
                    int nc = classesOverride > 0 ? classesOverride : dims[1] - 4, n = dims[2];
                    for (int i = 0; i < n; i++)
                    {
                        float cx = data[i];
                        float cy = data[n + i];
                        // быстрый пре-фильтр пустых анкор (у ultralytics score=0 в padded-выходе)
                        float best = 0f; int bestC = 0;
                        for (int c = 0; c < nc; c++)
                        {
                            float s = data[(4 + c) * n + i];
                            if (s > best) { best = s; bestC = c; }
                        }
                        if (best < confThresh) continue;

                        float w = data[2 * n + i], h = data[3 * n + i];
                        if (w <= 0f || h <= 0f) continue;

                        if (lb.MapCxCy(cx, cy, w, h, origW, origH, out int x, out int y, out int ww, out int hh))
                        {
                            var d = New(x, y, ww, hh, best, bestC, className);
                            if (withCoeffs) d.AnchorIdx = i; // пригодится для построения маски
                            raw.Add(d);
                        }
                    }
                    break;
                }

                case YoloOutputFormat.RowMajor:
                {
                    int n = dims[1], nc = dims[2] - 5;
                    for (int i = 0; i < n; i++)
                    {
                        int o = i * (nc + 5);
                        float obj = data[o + 4];
                        if (obj < confThresh * 0.5f) continue; // грубый пре-фильтр

                        float best = 0f; int bestC = 0;
                        for (int c = 0; c < nc; c++)
                        {
                            float s = data[o + 5 + c];
                            if (s > best) { best = s; bestC = c; }
                        }
                        float conf = obj * best;
                        if (conf < confThresh) continue;

                        float cx = data[o], cy = data[o + 1], w = data[o + 2], h = data[o + 3];
                        if (w <= 0f || h <= 0f) continue;

                        if (lb.MapCxCy(cx, cy, w, h, origW, origH, out int x, out int y, out int ww, out int hh))
                            raw.Add(New(x, y, ww, hh, conf, bestC, className));
                    }
                    break;
                }

                case YoloOutputFormat.End2End:
                {
                    int n = dims[1];
                    for (int i = 0; i < n; i++)
                    {
                        int o = i * 6;
                        float conf = data[o + 4];
                        if (conf < confThresh) continue;
                        int bestC = (int)data[o + 5];

                        if (lb.MapXyxy(data[o], data[o + 1], data[o + 2], data[o + 3],
                                       origW, origH, out int x, out int y, out int ww, out int hh))
                            raw.Add(New(x, y, ww, hh, conf, bestC, className));
                    }
                    break;
                }

                default:
                    return new List<Detection>(); // Unsupported — честный пустой список вместо «мусора»
            }

            return ApplyNms(raw, nmsThresh);
        }

        private static Detection New(int x, int y, int w, int h, float conf, int cls, Func<int, string> className)
            => new Detection
            {
                ClassName = className?.Invoke(cls) ?? $"class_{cls}",
                Confidence = conf,
                X = x,
                Y = y,
                Width = w,
                Height = h,
                Timestamp = DateTime.Now
            };

        /// <summary>Жадный NMS по IoU, class-agnostic (у YOLO на один анкор — один класс).</summary>
        public static List<Detection> ApplyNms(List<Detection> boxes, float nmsThreshold)
        {
            if (boxes == null || boxes.Count == 0) return boxes ?? new List<Detection>();

            var sorted = boxes.OrderByDescending(b => b.Confidence).ToList();
            var result = new List<Detection>();

            foreach (var box in sorted)
            {
                bool keep = true;
                foreach (var kept in result)
                {
                    if (CalcIou(box, kept) > nmsThreshold) { keep = false; break; }
                }
                if (keep) result.Add(box);
            }
            return result;
        }

        public static float CalcIou(Detection a, Detection b)
        {
            int xi1 = Math.Max(a.X, b.X);
            int yi1 = Math.Max(a.Y, b.Y);
            int xi2 = Math.Min(a.X + a.Width, b.X + b.Width);
            int yi2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

            long inter = (long)Math.Max(0, xi2 - xi1) * Math.Max(0, yi2 - yi1);
            long uni = (long)a.Width * a.Height + (long)b.Width * b.Height - inter;
            return uni > 0 ? (float)inter / uni : 0f;
        }

        // ================= Сегментация: маска из seg-ветки (чистая математика, без OpenCV) ==========

        /// <summary>
        /// По 32 mask-коэффициентам анкорса и proto-базису собрать бинарную маску объекта
        /// в пространстве маски (вход/4, для неквадратных входов по каждой оси). mask = sigmoid(coeff·proto) > threshold,
        /// всё вне croп-прямоугольника бокса обнуляется.
        /// </summary>
        public static float[] BuildInstanceMask(float[] out0, int[] dims0, float[] proto, int[] dimsP,
                                                int anchorIdx, int netSize, RectI crop, float threshold = 0.5f)
        {
            float[] empty = Array.Empty<float>();
            if (out0 == null || proto == null || dims0 == null || dimsP == null ||
                dims0.Length != 3 || dimsP.Length != 4) return empty;

            int nc = dims0[1] - 4 - 32;
            int n = dims0[2];
            int mh = dimsP[2], mw = dimsP[3];
            float[] mask = new float[mh * mw];
            if (nc < 1 || anchorIdx < 0 || anchorIdx >= n || mh <= 0 || mw <= 0) return mask;

            int coeffBase = (4 + nc) * n;
            float[] coeff = new float[32];
            for (int k = 0; k < 32; k++) coeff[k] = out0[coeffBase + k * n + anchorIdx];

            // sigmoid(s) > t  ⇔  s > ln(t/(1-t));  t>=1 — никогда
            float inv = threshold >= 1f ? float.PositiveInfinity
                                        : (float)Math.Log(threshold / (1f - threshold));
            int scale = Math.Max(1, netSize / mw);
            int cx0 = Math.Max(0, crop.X0 / scale), cy0 = Math.Max(0, crop.Y0 / scale);
            int cx1 = Math.Min(mw, (crop.X1 + scale - 1) / scale), cy1 = Math.Min(mh, (crop.Y1 + scale - 1) / scale);

            for (int y = cy0; y < cy1; y++)
            {
                int p = y * mw + cx0;
                for (int x = cx0; x < cx1; x++, p++)
                {
                    double s = 0;
                    for (int k = 0; k < 32; k++) s += (double)coeff[k] * proto[k * mh * mw + p];
                    mask[p] = s > inv ? 1f : 0f;
                }
            }
            return mask;
        }

        public readonly struct RectI
        {
            public readonly int X0, Y0, X1, Y1;
            public RectI(int x0, int y0, int x1, int y1) { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
        }

    }
}
