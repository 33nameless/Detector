using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ObjectDetectTrackApp.ViewModels;

namespace ObjectDetectTrackApp
{
    public partial class MainWindow : Window
    {
        // ---- Зум/пан превью ----
        private const double ZoomMin = 0.2;
        private const double ZoomMax = 10.0;
        private const double ZoomStep = 1.15;

        private bool _dragging;
        private Point _dragStart;
        private double _startH, _startV;

        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = new MainViewModel();

            if (DataContext is MainViewModel vm)
            {
                // «Шипы» детекций VM отдаёт снапшотом — обновляем кодом (Playhead/FrameCount приходят биндингом)
                vm.TimelineChanged += () => Dispatcher.BeginInvoke(new Action(() =>
                {
                    Tl.Hits = vm.GetTimelineHitsSnapshot();
                }));
            }
        }

        /// <summary>Клик по мини-таймлайну: позиция мыши -> индекс кадра -> переход.</summary>
        private void Timeline_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not MainViewModel vm || Tl.FrameCount < 2) return;

            double x = e.GetPosition(Tl).X;
            int idx = (int)Math.Round(x / Math.Max(1.0, Tl.ActualWidth) * (Tl.FrameCount - 1));
            idx = Math.Max(0, Math.Min(Tl.FrameCount - 1, idx));
            vm.JumpToTimelineFrame(idx);
            e.Handled = true;
        }

        private ScaleTransform Zoom => (ScaleTransform)PreviewImage.LayoutTransform;

        /// <summary>Чекбокс «🎯 следить» у объекта в списке DETECTIONS.</summary>
        private void Det_Track_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is ObjectDetectTrackApp.Models.Detection d &&
                DataContext is MainViewModel vm)
                vm.SetSelectionFor(d, cb.IsChecked == true);
        }

        /// <summary>Колесо мыши: приблизить/отдалить. Центр вида стараемся не «гулять».</summary>
        private void Preview_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double old = Zoom.ScaleX;
            double v = Math.Clamp(old * (e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep), ZoomMin, ZoomMax);
            if (Math.Abs(v - old) < 1e-9) { e.Handled = true; return; }

            // относительная точка под курсором — к ней и масштабируем
            Point under = e.GetPosition(PreviewScroll);
            double cx = PreviewScroll.HorizontalOffset + under.X;
            double cy = PreviewScroll.VerticalOffset + under.Y;

            Zoom.ScaleX = Zoom.ScaleY = v;
            PreviewScroll.UpdateLayout();

            double k = v / old;
            PreviewScroll.ScrollToHorizontalOffset(cx * k - under.X);
            PreviewScroll.ScrollToVerticalOffset(cy * k - under.Y);

            UpdateZoomLabel(v);
            e.Handled = true;
        }

        private void UpdateZoomLabel(double v)
        {
            if (Math.Abs(v - 1.0) < 1e-9)
            {
                ZoomLabel.Visibility = Visibility.Collapsed;
                return;
            }
            ZoomLabel.Text = $"{v * 100:F0}%";
            ZoomLabel.Visibility = Visibility.Visible;
        }

        // ---- Режим выбора: клик (без сдвига мыши) = выбрать/снять объект ----
        private bool _selDown;
        private Point _selDownPos;

        private void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is MainViewModel vm0 && vm0.SelectMode)
            {
                _selDown = true;
                _selDownPos = e.GetPosition(PreviewImage);
            }
            if (Zoom.ScaleX <= 1.0001) return;
            _dragging = true;
            _dragStart = e.GetPosition(PreviewScroll);
            _startH = PreviewScroll.HorizontalOffset;
            _startV = PreviewScroll.VerticalOffset;
            PreviewScroll.CaptureMouse();
            PreviewScroll.Cursor = Cursors.SizeAll;
        }

        private void Preview_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
            Point p = e.GetPosition(PreviewScroll);
            PreviewScroll.ScrollToHorizontalOffset(_startH - (p.X - _dragStart.X));
            PreviewScroll.ScrollToVerticalOffset(_startV - (p.Y - _dragStart.Y));
        }

        private void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_selDown)
            {
                _selDown = false;
                Point cur = e.GetPosition(PreviewImage);
                if (Math.Abs(cur.X - _selDownPos.X) + Math.Abs(cur.Y - _selDownPos.Y) <= 4 &&
                    DataContext is MainViewModel vm2 &&
                    PreviewImage.Source is System.Windows.Media.Imaging.BitmapSource bs &&
                    bs.PixelWidth > 0 && bs.PixelHeight > 0)
                {
                    // LayoutTransform зума GetPosition отменяет; остаётся Uniform-вписывание в ActualW/H
                    double k = Math.Min(PreviewImage.ActualWidth / bs.PixelWidth,
                                        PreviewImage.ActualHeight / bs.PixelHeight);
                    double ox = (PreviewImage.ActualWidth - bs.PixelWidth * k) / 2.0;
                    double oy = (PreviewImage.ActualHeight - bs.PixelHeight * k) / 2.0;
                    vm2.SelectAt((cur.X - ox) / k, (cur.Y - oy) / k,
                                 Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                    e.Handled = true;
                    return;
                }
            }
            if (!_dragging) return;
            _dragging = false;
            PreviewScroll.ReleaseMouseCapture();
            PreviewScroll.Cursor = null;
        }

        /// <summary>Двойной клик — вписать в окно (сброс зума).</summary>
        private void Preview_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            Zoom.ScaleX = Zoom.ScaleY = 1.0;
            UpdateZoomLabel(1.0);
            PreviewScroll.UpdateLayout();
            PreviewScroll.ScrollToHorizontalOffset(0);
            PreviewScroll.ScrollToVerticalOffset(0);
            e.Handled = true;
        }

        // ---- Логи: правый клик = копировать (этот обработчик был в репо, но не был
        //      привязан в XAML; теперь привязан на LogListBox) ----
        private void LogListBox_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var listBox = sender as System.Windows.Controls.ListBox;
            var viewModel = this.DataContext as MainViewModel;

            if (viewModel?.LogViewModel?.Logs == null || viewModel.LogViewModel.Logs.Count == 0)
            {
                MessageBox.Show("Логи пусты!", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Если клик по элементу - копируем этот элемент
            if (listBox?.SelectedItem != null)
            {
                var selectedLog = listBox.SelectedItem.ToString();
                CopyToClipboard(selectedLog);
                e.Handled = true;
                return;
            }

            // Если клик в пустое место - копируем все логи
            var allLogs = string.Join(System.Environment.NewLine,
                viewModel.LogViewModel.Logs.Select(log => log.ToString()));

            CopyToClipboard(allLogs);
            e.Handled = true;
        }

        private void CopyToClipboard(string text)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                MessageBox.Show("✓ Скопировано в буфер обмена!", "Success",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
