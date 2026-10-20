using OpenCvSharp;
using ObjectDetectTrackApp.Models;
using System;
using System.Collections.Generic;

namespace ObjectDetectTrackApp.Services
{
    /// <summary>Как подчёркивать объекты поверх кадра.</summary>
    public enum HighlightMode
    {
        Boxes = 0,      // только рамки (как раньше)
        Contours = 1,   // только контур маски
        Both = 2,       // рамки + контур
        MaskFill = 3,   // полупрозрачная заливка маски + контур
    }

    public class RenderService
    {
        private readonly LogService _logService;

        // детерминированная палитра по id класса (как в ultralytics)
        private static readonly Scalar[] Palette =
        {
            new Scalar(  0,  0, 255), new Scalar(  0,128, 255), new Scalar(  0,255, 255),
            new Scalar(128, 255,  0), new Scalar(255, 255,   0), new Scalar(255, 128,   0),
            new Scalar(255,   0,   0), new Scalar(180,   0, 180), new Scalar( 80,  80, 255),
            new Scalar(  0, 255, 128)
        };

        public RenderService(LogService logService = null)
        {
            _logService = logService;
        }

        /// <summary>Рисует выделение+подписи на КОПИИ кадра (BGR). Вход не мутируется.
        /// Цвета Scalar задаются в порядке (B,G,R), как принято в OpenCV.</summary>
        public Mat DrawDetections(Mat frameBgr, IEnumerable<Detection> detections,
                                  HighlightMode mode = HighlightMode.Boxes)
        {
            if (frameBgr == null || frameBgr.IsDisposed || frameBgr.Empty())
                return new Mat();

            Mat annotated = frameBgr.Clone();

            try
            {
                foreach (var d in detections ?? Array.Empty<Detection>())
                {
                    Scalar color = Palette[(uint)Math.Abs(d.ClassName?.GetHashCode() ?? 0) % Palette.Length];
                    int x = d.X, y = d.Y, w = d.Width, h = d.Height;
                    LineTypes lineType = d.TrackId.HasValue ? LineTypes.Link8 : LineTypes.AntiAlias;

                    Point[] poly = null;
                    if (mode != HighlightMode.Boxes && d.MaskPolygon != null && d.MaskPolygon.Length >= 6)
                    {
                        int cnt = d.MaskPolygon.Length / 2;
                        poly = new Point[cnt];
                        for (int pi = 0; pi < cnt; pi++)
                            poly[pi] = new Point((int)Math.Round(d.MaskPolygon[2 * pi]),
                                                 (int)Math.Round(d.MaskPolygon[2 * pi + 1]));
                    }

                    if (mode == HighlightMode.MaskFill && poly != null)
                    {
                        using Mat m = Mat.Zeros(annotated.Size(), MatType.CV_8UC1);
                        Cv2.FillPoly(m, new[] { poly }, new Scalar(255));
                        using Mat solid = new Mat(annotated.Size(), MatType.CV_8UC3, color);
                        using Mat tinted = new Mat();
                        Cv2.AddWeighted(annotated, 0.62, solid, 0.38, 0, tinted);
                        tinted.CopyTo(annotated, m);
                    }

                    if (poly != null)
                        Cv2.Polylines(annotated, new[] { poly }, true, color, 2, LineTypes.AntiAlias);
                    if (mode == HighlightMode.Boxes || mode == HighlightMode.Both || poly == null)
                        Cv2.Rectangle(annotated, new Rect(x, y, w, h), color, 2, lineType);

                    string label = d.ClassName;
                    if (d.TrackId.HasValue) label += $" #{d.TrackId.Value}";
                    label += $" {d.Confidence:P0}";

                    var tagSize = Cv2.GetTextSize(label, HersheyFonts.HersheySimplex, 0.5, 1, out int baseline);
                    int tagY = Math.Max(tagSize.Height + baseline + 2, y);
                    Cv2.Rectangle(annotated,
                        new Rect(x, tagY - tagSize.Height - baseline - 2, tagSize.Width + 6, tagSize.Height + baseline + 2),
                        color, -1);
                    Cv2.PutText(annotated, label, new Point(x + 3, tagY - baseline - 1),
                        HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 0, 0), 1, LineTypes.AntiAlias);
                }
            }
            catch (Exception ex)
            {
                _logService?.Error($"Ошибка отрисовки: {ex.Message}");
            }

            return annotated;
        }
    }
}
