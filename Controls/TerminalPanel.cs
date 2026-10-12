using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ObjectDetectTrackApp.Controls
{
    /// <summary>
    /// Панель с терминальной рамкой и выпирающим заголовком
    /// </summary>
    public class TerminalPanel : Border
    {
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register("Title", typeof(string), typeof(TerminalPanel),
                new PropertyMetadata("PANEL", OnTitleChanged));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((TerminalPanel)d).InvalidateVisual();
        }

        public TerminalPanel()
        {
            this.Padding = new Thickness(8);
            this.Margin = new Thickness(4);
            this.Background = (Brush)Application.Current.Resources["TerminalBgBrush"];
            this.BorderBrush = (Brush)Application.Current.Resources["TerminalFgBrush"];
            this.BorderThickness = new Thickness(1);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            // Рисуем выпирающий заголовок (текст над линией)
            if (!string.IsNullOrEmpty(Title))
            {
                var formattedText = new FormattedText(
                    $"[ {Title} ",
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Consolas"), 11,
                    (Brush)Application.Current.Resources["TerminalFgBrush"],
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);

                drawingContext.DrawText(formattedText, new Point(12, -8));
            }
        }
    }
}