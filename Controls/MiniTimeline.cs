using System;
using System.Windows;
using System.Windows.Media;

namespace ObjectDetectTrackApp.Controls
{
    /// <summary>
    /// Мини-таймлайн видео в терминальном стиле: внизу — «шипы» количества детекций
    /// на кадр, сверху — янтарный курсор текущего кадра. Клик по элементу родителя
    /// маппится в индекс кадра (см. MainWindow.Timeline_MouseUp).
    /// </summary>
    public class MiniTimeline : FrameworkElement
    {
        public static readonly DependencyProperty FrameCountProperty = DependencyProperty.Register(
            nameof(FrameCount), typeof(int), typeof(MiniTimeline),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PlayheadProperty = DependencyProperty.Register(
            nameof(Playhead), typeof(int), typeof(MiniTimeline),
            new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty HitsProperty = DependencyProperty.Register(
            nameof(Hits), typeof(int[]), typeof(MiniTimeline),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public int FrameCount { get => (int)GetValue(FrameCountProperty); set => SetValue(FrameCountProperty, value); }
        public int Playhead { get => (int)GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }
        public int[] Hits { get => (int[])GetValue(HitsProperty); set => SetValue(HitsProperty, value); }

        private static readonly Brush Bg = Make(Color.FromRgb(0x0A, 0x0A, 0x0A));
        private static readonly Brush Hit = Make(Color.FromArgb(0xE0, 0x00, 0xC8, 0x3C));
        private static readonly Pen Frame = MakePen(Color.FromRgb(0x33, 0x33, 0x33), 1);
        private static readonly Pen Base = MakePen(Color.FromRgb(0x55, 0x55, 0x55), 1);
        private static readonly Pen Head = MakePen(Color.FromRgb(0xFF, 0xC1, 0x07), 1.6);

        protected override Size MeasureOverride(Size availableSize)
        {
            double w = double.IsInfinity(availableSize.Width) ? 60 : availableSize.Width;
            double h = double.IsInfinity(availableSize.Height) ? 26 : availableSize.Height;
            return new Size(Math.Max(20, w), Math.Max(18, Math.Min(48, h)));
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 4 || h < 4) return;

            dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));

            int fc = Math.Max(1, FrameCount);
            double colW = w / fc;

            var hits = Hits;
            if (hits != null)
            {
                int n = Math.Min(fc, hits.Length);
                for (int i = 0; i < n; i++)
                {
                    int c = hits[i];
                    if (c <= 0) continue;
                    double bh = Math.Min(h - 4, 2 + Math.Min(c, 8) * 1.15);
                    double bw = Math.Max(colW, 0.8);
                    dc.DrawRectangle(Hit, null, new Rect(i * colW, h - 2 - bh, bw, bh));
                }
            }

            dc.DrawLine(Base, new Point(0, h - 1.5), new Point(w, h - 1.5));

            if (FrameCount > 1 && Playhead >= 0)
            {
                double px = (Playhead + 0.5) * colW;
                dc.DrawLine(Head, new Point(px, 1), new Point(px, h - 1));
            }

            dc.DrawRectangle(null, Frame, new Rect(0.5, 0.5, w - 1, h - 1));
        }

        private static Brush Make(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        private static Pen MakePen(Color c, double t) { var p = new Pen(Make(c), t); p.Freeze(); return p; }
    }
}
