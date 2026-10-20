using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ObjectDetectTrackApp.Views
{
    /// <summary>
    /// Стартовый сплэш в стиле основного терминального интерфейса (те же цвета темы).
    /// Дождь символов считается по реальному размеру окна (без «обрезка справа») и
    /// заполняет всё окно; нижняя панель непрозрачна — прогресс-бар всегда чистый.
    /// Шаги настоящие: скан Assets/*.onnx в фоне. После 100% — пауза «WELCOME»
    /// кармином и только затем открывается MainWindow (загрузка модели идёт под сплэшем).
    /// </summary>
    public partial class SplashWindow : Window
    {
        private const int TotalTicks = 56;      // ~2.8 с: GRIDHOUND · TRACK UTILITY = 25 симв. × 2 тика
        private const int WelcomeTicks = 16;    // пауза WELCOME ~0.8 с
        private const string RainAlphabet = "0123456789ABCDEF#$%&*+=/\\<>|";
        private const string ScrambleAlphabet = "#%&$@01<>/\\*+=ABCDEF";
        private const string TitleTarget = "GRIDHOUND · TRACK UTILITY";
        private static readonly char[] RainChars = RainAlphabet.ToCharArray();
        private static readonly char[] ScrambleChars = ScrambleAlphabet.ToCharArray();
        private static readonly Random Rng = new Random();

        private char[,] _rain = new char[0, 0];
        private int _rainRows, _rainCols;
        private DispatcherTimer _timer;
        private int _tick;
        private bool _modelChecked, _finishing, _welcomed;
        private string _modelLine = "[....] model assets";

        public SplashWindow()
        {
            InitializeComponent();

            Loaded += (s, e) => { ComputeRainGrid(); StartTimer(); };
            SizeChanged += (s, e) => ComputeRainGrid();
            MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Finish(); };
        }

        private void StartTimer()
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _timer.Tick += OnTick;
            _timer.Start();
        }

        /// <summary>Размер поля дождя — по фактической ширине глифа Consolas и размеру окна,
        /// поэтому символы упираются ровно в поля, «обрезанного хвоста» нет.</summary>
        private void ComputeRainGrid()
        {
            try
            {
                double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var typeface = new Typeface("Consolas");
                var probe = new FormattedText("0", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 13.0, Brushes.White, dpi);
                double charW = Math.Max(1.0, probe.WidthIncludingTrailingWhitespace);

                _rainCols = Math.Max(24, (int)((ActualWidth - 24) / charW));
                _rainRows = Math.Max(8, (int)((ActualHeight - 8 - 4) / 15.0)); // всё окно: 15 = LineHeight
                _rain = new char[_rainRows, _rainCols];
                for (int r = 0; r < _rainRows; r++)
                    for (int c = 0; c < _rainCols; c++)
                        _rain[r, c] = RainChars[Rng.Next(RainChars.Length)];
            }
            catch
            {
                _rainCols = 100; _rainRows = 28;
                _rain = new char[_rainRows, _rainCols];
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            _tick++;

            // 1) дождь — на всё окно (панель снизу его накрывает)
            var sb = new StringBuilder(_rainRows * (_rainCols + 1));
            for (int c = 0; c < _rainCols; c++)
            {
                if (Rng.Next(100) >= 85) continue; // у части колонок «пауза» — живее
                for (int r = _rainRows - 1; r > 0; r--) _rain[r, c] = _rain[r - 1, c];
                _rain[0, c] = RainChars[Rng.Next(RainChars.Length)];
            }
            for (int r = 0; r < _rainRows; r++)
            {
                for (int c = 0; c < _rainCols; c++) sb.Append(_rain[r, c]);
                sb.Append('\n');
            }
            Rain.Text = sb.ToString();

            // 2) финальная фаза: WELCOME кармином, ничего не скрамблим
            if (_welcomed)
            {
                if (_tick >= TotalTicks + WelcomeTicks) Finish();
                return;
            }

            int revealed = Math.Min(TitleTarget.Length, _tick / 2);
            var t = new StringBuilder(TitleTarget.Length);
            for (int i = 0; i < TitleTarget.Length; i++)
                t.Append(i < revealed ? TitleTarget[i] : ScrambleChars[Rng.Next(ScrambleChars.Length)]);
            Title.Text = t.ToString();

            // 3) реальный шаг: скан Assets (один раз, в фоне)
            if (_tick == 3 && !_modelChecked)
            {
                _modelChecked = true;
                Task.Run(() =>
                {
                    string info = ScanModel();
                    Dispatcher.BeginInvoke(new Action(() => { _modelLine = info; }));
                });
            }

            // 4) прогресс и чек-лист
            double p = Math.Min(1.0, (double)_tick / TotalTicks);
            const int width = 36;
            int filled = (int)Math.Round(p * width);
            Bar.Text = "[" + new string('█', filled) + new string('░', width - filled) + "] " +
                       $"{p * 100:00}%";

            int step = _tick < 10 ? 0 : _tick < 26 ? 1 : _tick < 40 ? 2 : 3;
            string model = step >= 1 || _modelChecked ? _modelLine : "[....] model assets";
            string warm = step >= 2 ? "[ OK ] runtime warmup" : "[....] runtime warmup";
            Steps.Text = "[ OK ] theme · resources\n" + model + "\n" + warm;
            StatusLine.Text = step >= 3 ? "[ OK ] ready" : "loading…";

            if (_tick >= TotalTicks)
            {
                _welcomed = true;
                Bar.Text = new string('█', width + 2) + " 100%";
                Title.Text = "WELCOME";
                Title.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x14, 0x3C)); // crimson / кармин
                Title.FontSize = 46;
                StatusLine.Text = "launching main utility…";
            }
        }

        private static string ScanModel()
        {
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
                if (!Directory.Exists(dir)) return "[!!] model: Assets\\ отсутствует";

                var files = Directory.GetFiles(dir, "*.onnx").OrderBy(f => f).ToList();
                Func<string, string[]> pick = key =>
                    files.Where(f => Path.GetFileName(f).ToLowerInvariant().Contains(key))
                         .Select(f => Path.GetFileName(f)).ToArray();

                string found = pick("yolov8").FirstOrDefault() ?? pick("yolo11").FirstOrDefault()
                            ?? pick("yolov5").FirstOrDefault() ?? pick("yolo").FirstOrDefault()
                            ?? (files.Count > 0 ? Path.GetFileName(files[0]) : null);

                if (found == null) return "[!!] model: .onnx не найден";
                long size = new FileInfo(Path.Combine(dir, found)).Length;
                return $"[ OK ] model: {found} · {size / 1048576.0:F1} MB";
            }
            catch (Exception ex)
            {
                return "[!!] model: " + ex.Message;
            }
        }

        private void Finish()
        {
            if (_finishing) return;
            _finishing = true;
            _timer?.Stop();

            try
            {
                var main = new MainWindow(); // модель грузится здесь — сплэш ещё виден
                Application.Current.MainWindow = main;
                main.Show();          // сначала показываем main…
                Close();              // …затем гасим сплэш: без «0 окон» (OnLastWindowClose)
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось открыть главное окно:\n" + ex, "Splash",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
            }
        }
    }
}
