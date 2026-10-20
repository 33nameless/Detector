using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ObjectDetectTrackApp.Models;
using ObjectDetectTrackApp.Services;
using OpenCvSharp;

namespace ObjectDetectTrackApp.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly FileSourceService _fileSourceService;
        private readonly LogService _logService;
        private readonly FramePump _framePump;
        private DetectorService _detectorService;
        private readonly RenderService _renderService;
        private readonly TrackerService _tracker;
        private CancellationTokenSource _cancellationTokenSource;
        private volatile bool _isRecording;          // пишем в SessionDetections только во время прогона
        private bool _sessionOverflowLogged;
        private const int MaxSessionItems = 20000;   // защита от раздувания памяти на длинном видео

        private volatile bool _isExporting;
        private string _currentSourcePath;

        // ---- Ручной выбор объектов (клик по превью) ----
        private readonly object _selLock = new object();
        private List<SelectionLogic.SelectionItem> _selection = new();
        private volatile int _lastPreviewFrameIndex = -1;
        private List<SelectionLogic.SelectionItem> _selectionStateCache = new();
        // ---- Плавный стрим: отрисовка по темпу помпа, инференс — отдельный потребитель ----
        private volatile List<Detection> _liveView = new List<Detection>();
        private FrameData _pendingFd;
        private readonly SemaphoreSlim _detSignal = new SemaphoreSlim(0, 1);
        private CancellationTokenSource _detCts;
        private Task _detTask;
        private volatile bool _pumpAlive;

        [ObservableProperty]
        private bool selectMode;

        [ObservableProperty]
        private int selectedCount;

        [ObservableProperty]
        private string trackingInfo = "all";
        // Отметки кадров для выборочного экспорта (SortedSet = сам дедуплицирует)
        private readonly SortedSet<int> _exportMarks = new();
        [ObservableProperty]
        private string exportMarksText = "";
        private int[] _tlHits = Array.Empty<int>(); // кол-во детекций на кадр (для мини-таймлайна)

        /// <summary>Просит окну перерисовать «шипы» таймлайна (Hits обновляем кодом, не биндингом).</summary>
        public event Action TimelineChanged;

        public LogViewModel LogViewModel { get; }
        public SourcesViewModel SourcesViewModel { get; }

        [ObservableProperty]
        public BitmapImage previewImage;

        [ObservableProperty]
        public string statusText = "Ready";

        [ObservableProperty]
        public double fps = 0.0;

        [ObservableProperty]
        public int currentFrameIndex = -1;

        // Режим подсветки: 0=Боксы, 1=Контур, 2=Боксы+Контур, 3=Маска+Контур
        [ObservableProperty]
        public int highlightModeIndex = 0;

        [ObservableProperty]
        public int timelineFrameCount = 0;

        [ObservableProperty]
        public bool hasTimeline = false;

        [ObservableProperty]
        public string resolution = "-";

        [ObservableProperty]
        public bool isPlaying;

        [ObservableProperty]
        public ObservableCollection<Detection> detections = new();

        /// <summary>Все детекции прогона (каждый кадр). Клик по строке -> переход к кадру.</summary>
        public ObservableCollection<Detection> SessionDetections { get; } = new();

        [ObservableProperty]
        public Detection selectedSessionDetection;

        [ObservableProperty]
        public float confidenceThreshold = 0.45f;

        [ObservableProperty]
        public float nmsThreshold = 0.45f;

        [ObservableProperty]
        public bool filterPersonOnly = false;

        [ObservableProperty]
        public int activeTracksCount = 0;

        [ObservableProperty]
        public bool enableTracking = true;

        public ICommand PlayCommand { get; }
        public ICommand PauseCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand ExportJsonCommand { get; }
        public ICommand ExportCsvCommand { get; }
        public ICommand ExportFramesCommand { get; }
        public ICommand MarkCurrentFrameCommand { get; }
        public ICommand ClearExportMarksCommand { get; }
        public ICommand ExportVideoCommand { get; }
        public ICommand ClearLogCommand { get; }
        public ICommand ClearSessionCommand { get; }

        public MainViewModel()
        {
            _logService = new LogService();
            _fileSourceService = new FileSourceService(_logService);
            _framePump = new FramePump(_fileSourceService, _logService);
            _renderService = new RenderService(_logService);
            _tracker = new TrackerService(_logService);

            LogViewModel = new LogViewModel(_logService);
            SourcesViewModel = new SourcesViewModel(_fileSourceService, _logService);
            SourcesViewModel.PropertyChanged += OnSourcesViewModelPropertyChanged;

            PlayCommand = new AsyncRelayCommand(PlayAsync);
            PauseCommand = new RelayCommand(() => _framePump.Pause());
            StopCommand = new RelayCommand(Stop);
            ExportJsonCommand = new RelayCommand(ExportJson);
            ExportCsvCommand = new RelayCommand(ExportCsv);
            ExportFramesCommand = new AsyncRelayCommand(ExportFramesAsync);
            MarkCurrentFrameCommand = new RelayCommand(MarkCurrentFrame);
            ClearExportMarksCommand = new RelayCommand(ClearExportMarks);
            ExportVideoCommand = new AsyncRelayCommand(ExportVideoAsync);
            ClearSessionCommand = new RelayCommand(() =>
            {
                SessionDetections.Clear();
                SelectedSessionDetection = null;
                _logService.Info("Результаты очищены");
            });
            ClearLogCommand = new RelayCommand(() => _logService.Clear());

            InitializeDetector();

            _framePump.FrameReady += FramePump_FrameReady;
            _framePump.PlaybackFinished += FramePump_PlaybackFinished;
            _framePump.ErrorOccurred += FramePump_ErrorOccurred;

            _logService.Info("✓ Application initialized");
        }

        private HighlightMode HlMode =>
            (HighlightMode)Math.Max(0, Math.Min(3, HighlightModeIndex));

        partial void OnHighlightModeIndexChanged(int value) => ApplyHighlightMode();

        /// <summary>Согласовать режим подсветки с моделью: для контуров/маски нужна
        /// seg-модель — при её отсутствии пробуем подхватить *seg*.onnx из Assets,
        /// не нашли — честно откатываемся на боксы с предупреждением.</summary>
        private void ApplyHighlightMode()
        {
            var d = _detectorService;
            if (d == null) return;
            if (HighlightModeIndex == 0) { d.BuildMasks = false; return; }

            if (!d.IsSegModel && !TrySwitchToSegModel())
            {
                _logService.Warn("Режим требует seg-модель: положите yolov8n-seg.onnx в Assets\\ — возвращаю боксы");
                HighlightModeIndex = 0;
                return;
            }
            d.BuildMasks = true;
            _logService.Info("Подсветка: " + new[] { "боксы", "контур", "боксы+контур", "маска+контур" }[HighlightModeIndex]);
        }

        private bool TrySwitchToSegModel()
        {
            try
            {
                string assetsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
                if (!Directory.Exists(assetsDir)) return false;

                var seg = Directory.GetFiles(assetsDir, "*.onnx")
                    .Where(f => Path.GetFileName(f).ToLowerInvariant().Contains("seg"))
                    .OrderByDescending(f => Path.GetFileName(f).ToLowerInvariant().Contains("yolov8") ? 1 : 0)
                    .FirstOrDefault();
                if (seg == null) return false;

                _detectorService?.Dispose();
                _detectorService = new DetectorService(seg, _logService);
                _logService.Info($"Модель: {Path.GetFileName(seg)} (переключение под сегментацию)");
                return true;
            }
            catch (Exception ex)
            {
                _logService.Error($"Не удалось загрузить seg-модель: {ex.Message}");
                return false;
            }
        }

        private void InitializeDetector()
        {
            try
            {
                // Ищем любую .onnx в Assets\: приоритет yolov8 -> yolo11 -> yolov5 -> первая попавшаяся.
                string assetsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
                string modelPath = null;

                if (Directory.Exists(assetsDir))
                {
                    var onnx = new List<string>(Directory.GetFiles(assetsDir, "*.onnx"));
                    onnx.Sort();

                    modelPath = FindBy(onnx, "yolov8")
                             ?? FindBy(onnx, "yolo11")
                             ?? FindBy(onnx, "yolov5")
                             ?? FindBy(onnx, "yolo")
                             ?? (onnx.Count > 0 ? onnx[0] : null);
                }

                static string FindBy(List<string> files, string key)
                    => files.Find(f => Path.GetFileName(f).ToLowerInvariant().Contains(key));

                if (modelPath == null)
                {
                    _logService.Warn("Модель не найдена: положите yolov8n.onnx (или yolov5n.onnx) в Detector\\Assets\\");
                    _logService.Warn("Экспорт: yolo export model=yolov8n.pt format=onnx imgsz=640");
                    return;
                }

                _logService.Info($"Модель: {Path.GetFileName(modelPath)}");

                _detectorService = new DetectorService(modelPath, _logService);
                ApplyHighlightMode();
                _logService.Info("✓ GridHound initialized successfully");
            }
            catch (Exception ex)
            {
                _logService.Error($"GridHound init failed: {ex.Message}");
            }
        }

        private async Task PlayAsync()
        {
            var source = SourcesViewModel.SelectedSource;
            if (source == null)
            {
                _logService.Warn("No source selected");
                return;
            }

            if (_framePump.IsPlaying)
            {
                if (string.Equals(_currentSourcePath, source.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    // Тот же файл: Play = возобновить, если на паузе (иначе — тихо игнор)
                    if (_framePump.IsPaused)
                    {
                        _framePump.Resume();
                        IsPlaying = true;
                        StatusText = $"Playing: {source.FileName}";
                    }
                    else
                    {
                        _logService.Info("Воспроизведение уже выполняется");
                    }
                    return;
                }

                // Другой файл — корректно останавливаем старый прогон и стартуем заново
                _logService.Info("Переключение источника: останавливаю текущий прогон");
                Stop();
                await Task.Delay(200);
            }

            _currentSourcePath = source.FilePath;
            // новый источник — старый выбор не переносим (иначе «захватит» похожий объект в другом видео)
            lock (_selLock) { _selection.Clear(); }
            SelectedCount = 0;
            TrackingInfo = "все объекты";
            _cancellationTokenSource = new CancellationTokenSource();
            _tracker.Reset(); // новый прогон = новые id
            ActiveTracksCount = 0;
            SessionDetections.Clear();
            SelectedSessionDetection = null;
            _sessionOverflowLogged = false;
            _isRecording = true;
            if (_tlHits.Length > 0) Array.Clear(_tlHits, 0, _tlHits.Length);
            HasTimeline = false; // включится, когда помп сообщит что источник — видео с >1 кадром
            TimelineChanged?.Invoke();
            IsPlaying = true;
            StatusText = $"Playing: {source.FileName}";
            _logService.Info("Запись результатов прогона: ON");

            _framePump.Play(source.FilePath, _cancellationTokenSource.Token);
        }

        private void Stop()
        {
            _framePump.Stop();
            StopDetConsumer();
            _cancellationTokenSource?.Cancel();
            _isRecording = false;
            _tracker.Reset();
            _pumpAlive = false;
            ActiveTracksCount = 0;
            IsPlaying = false;
            StatusText = "Stopped";
        }

        /// <summary>Клик по превью (координаты в пикселях кадра) с режимом выбора.</summary>
        public void SelectAt(double frameX, double frameY, bool additive)
        {
            if (!SelectMode) return;
            var snap = new List<Detection>(Detections);
            lock (_selLock)
            {
                int hit = SelectionLogic.HitTest(snap, frameX, frameY);
                int count;
                if (hit < 0)
                {
                    if (!additive) _selection.Clear();
                    count = _selection.Count;
                }
                else
                {
                    count = SelectionLogic.Toggle(_selection, snap[hit], additive);
                }
                SelectedCount = count;
                UpdateTrackingInfo();
                _logService?.Info(count > 0
                    ? $"Выбор: следим за {count} объект(ами) — подсвечиваем и треким только их"
                    : "Выбор: снят — рисуем все объекты");
            }
            // На паузе/фото — мгновенный перерисов кадра; в игре следующий кадр сам применит выбор.
            if (!_pumpAlive && !IsPlaying) RefreshCurrentFrame();
        }

        /// <summary>Чекбокс «следить» в списке DETECTIONS.</summary>
        public void SetSelectionFor(Detection d, bool on)
        {
            if (d == null) return;
            bool changed;
            lock (_selLock)
            {
                changed = SelectionLogic.Set(_selection, d, on);
                SelectedCount = _selection.Count;
                UpdateTrackingInfo();
            }
            if (changed)
                _logService?.Info(on
                    ? $"Выбор: «{(d.TrackId is int t && t > 0 ? d.ClassName + " #" + t : d.ClassName)}» — трекинг включён 🎯"
                    : $"Выбор: {d.ClassName} — трекинг выключен");
            if (!_pumpAlive && !IsPlaying) RefreshCurrentFrame();
        }

        private void UpdateTrackingInfo()
        {
            TrackingInfo = SelectedCount > 0 ? $"🎯 отслеживаем: {SelectedCount}" : "все объекты";
        }

        [RelayCommand]
        private void ClearSelection()
        {
            bool had;
            lock (_selLock) { had = _selection.Count > 0; _selection.Clear(); }
            SelectedCount = 0;
            UpdateTrackingInfo();
            if (had) _logService?.Info("Выбор: снят — рисуем все объекты");
            if (!_pumpAlive && !IsPlaying) RefreshCurrentFrame();
        }

        /// <summary>Перерисовать текущий кадр (пауза/фото): ShowFrameAt тем же кадром.</summary>
        private void RefreshCurrentFrame()
        {
            if (string.IsNullOrEmpty(_currentSourcePath) || !System.IO.File.Exists(_currentSourcePath)) return;
            _framePump.ShowFrameAt(_currentSourcePath, Math.Max(0, _lastPreviewFrameIndex));
        }

        private List<SelectionLogic.SelectionItem> SnapshotSelection()
        {
            lock (_selLock) { return _selection.ConvertAll(it => it.Clone()); }
        }

        private void StoreSelection(List<SelectionLogic.SelectionItem> updated)
        {
            lock (_selLock) { _selection = updated; }
        }

        private void FramePump_FrameReady(object sender, FrameData frameData)
        {
            try
            {
                _lastPreviewFrameIndex = frameData.FrameIndex;
                _pumpAlive = IsPlaying;

                if (frameData.IsStream)
                {
                    // ===== Поток (Play): рисуем последний снапшот, НЕ дожидаясь сети =====
                    EnsureDetConsumer();
                    var view = _liveView;
                    byte[] displayBytes = frameData.FrameBytes;
                    Mat fr = frameData.Frame;
                    if (fr != null && !fr.IsDisposed)
                    {
                        if (view != null && view.Count > 0)
                        {
                            using Mat annotated = _renderService.DrawDetections(fr, view, HlMode);
                            if (!annotated.Empty()) displayBytes = annotated.ToBytes(".jpg");
                            else displayBytes = fr.ToBytes(".jpg");
                        }
                        else if (displayBytes == null)
                        {
                            displayBytes = fr.ToBytes(".jpg");
                        }
                    }
                    ShowBitmap(displayBytes, frameData.Width, frameData.Height);
                    StatusText = $"Frame {frameData.FrameIndex} • dets: {(view?.Count ?? 0)}";
                    SubmitToDetector(frameData); // дальше кадр принадлежит потребителю (он и диспозит)
                    return;
                }

                // ===== Одиночный кадр (фото/пауза/seek): полный синхронный путь =====
                var dets = new List<Detection>();
                try
                {
                    if (_detectorService != null && frameData.Frame != null && !frameData.Frame.IsDisposed)
                    {
                        dets = _detectorService.Detect(frameData.Frame, ConfidenceThreshold, NmsThreshold);
                        foreach (var d in dets) d.FrameIndex = frameData.FrameIndex;
                        if (FilterPersonOnly)
                            dets = dets.FindAll(d => string.Equals(d.ClassName, "person", StringComparison.OrdinalIgnoreCase));
                        if (EnableTracking)
                            dets = _tracker.Update(dets);
                    }

                    var selSnap = SnapshotSelection();
                    if (selSnap.Count > 0)
                    {
                        foreach (var d in dets) d.IsSelected = false;
                        SelectionLogic.Apply(dets, selSnap);
                        StoreSelection(selSnap);
                    }
                    var drawSet = selSnap.Count > 0 ? dets.FindAll(dd => dd.IsSelected) : dets;
                    _liveView = drawSet; // чтобы старт прогона с паузы был со свежими боксами

                    byte[] displayBytes = frameData.FrameBytes;
                    if (drawSet.Count > 0 && frameData.Frame != null && !frameData.Frame.IsDisposed)
                    {
                        using Mat annotated = _renderService.DrawDetections(frameData.Frame, drawSet, HlMode);
                        if (!annotated.Empty())
                            displayBytes = annotated.ToBytes(".jpg");
                    }
                    ShowBitmap(displayBytes, frameData.Width, frameData.Height);
                    StatusText = $"Frame {frameData.FrameIndex} • dets: {dets.Count}";
                }
                finally
                {
                    try { frameData.Frame?.Dispose(); } catch { } // закрыта утечка Mat одиночных кадров
                }

                int activeTracks = EnableTracking ? _tracker.GetActiveTrackCount() : 0;
                int frameIdxSnap = frameData.FrameIndex;
                var detsUi = dets;
                // ВАЖНО: BeginInvoke, а не Invoke (помп не должен ждать UI-поток).
                Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    Detections.Clear();
                    foreach (var det in detsUi) Detections.Add(det);
                    ActiveTracksCount = activeTracks;
                    UpdateTimeline(frameIdxSnap);
                    if (_isRecording && detsUi.Count > 0)
                    {
                        if (SessionDetections.Count + detsUi.Count <= MaxSessionItems)
                        {
                            foreach (var det in detsUi) SessionDetections.Add(det);
                        }
                        else if (!_sessionOverflowLogged)
                        {
                            _sessionOverflowLogged = true;
                            _logService.Warn($"Список результатов ограничен {MaxSessionItems} записями (длинное видео)");
                        }
                    }
                }));
            }
            catch (Exception ex)
            {
                _logService.Error($"Frame processing error: {ex.Message}");
            }
        }

        private void ShowBitmap(byte[] bytes, int w, int h)
        {
            if (bytes == null || bytes.Length == 0) return;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new System.IO.MemoryStream(bytes);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            PreviewImage = bitmap;
            Resolution = $"{w}x{h}";
        }

        // ---------- потребитель инференса для режима Play ----------
        private void EnsureDetConsumer()
        {
            if (_detTask != null && !_detTask.IsCompleted) return;
            var cts = new CancellationTokenSource();
            _detCts = cts;
            _detTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try { await _detSignal.WaitAsync(cts.Token).ConfigureAwait(false); }
                    catch { break; }
                    FrameData fd;
                    while ((fd = Interlocked.Exchange(ref _pendingFd, null)) != null)
                    {
                        // важен последний кадр; накопленные старые неактуальны
                        try { ProcessStreamFrame(fd); }
                        catch (Exception ex) { _logService?.Error("inference: " + ex.Message); }
                        finally { try { fd.Frame?.Dispose(); } catch { } }
                        if (cts.IsCancellationRequested) break;
                    }
                }
                _liveView = new List<Detection>();
            }, cts.Token);
        }

        private void SubmitToDetector(FrameData fd)
        {
            var old = Interlocked.Exchange(ref _pendingFd, fd);
            if (old != null) { try { old.Frame?.Dispose(); } catch { } } // сеть не обязана видеть каждый кадр
            try { _detSignal.Release(); } catch (SemaphoreFullException) { }
        }

        private void ProcessStreamFrame(FrameData fd)
        {
            if (_detectorService == null || fd.Frame == null || fd.Frame.IsDisposed)
            {
                _liveView = new List<Detection>();
                return;
            }
            var dets = _detectorService.Detect(fd.Frame, ConfidenceThreshold, NmsThreshold);
            foreach (var d in dets) d.FrameIndex = fd.FrameIndex;
            if (FilterPersonOnly)
                dets = dets.FindAll(d => string.Equals(d.ClassName, "person", StringComparison.OrdinalIgnoreCase));
            if (EnableTracking)
                dets = _tracker.Update(dets);

            var selSnap = SnapshotSelection();
            if (selSnap.Count > 0)
            {
                foreach (var d in dets) d.IsSelected = false;
                SelectionLogic.Apply(dets, selSnap);
                StoreSelection(selSnap);
            }
            var drawSet = selSnap.Count > 0 ? dets.FindAll(dd => dd.IsSelected) : dets;
            _liveView = drawSet;

            int activeTracks = EnableTracking ? _tracker.GetActiveTrackCount() : 0;
            int fi = fd.FrameIndex;
            var detsSnap = dets;
            _ = Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                Detections.Clear();
                foreach (var det in detsSnap) Detections.Add(det);
                ActiveTracksCount = activeTracks;
                UpdateTimeline(fi);
                if (_isRecording && detsSnap.Count > 0)
                {
                    if (SessionDetections.Count + detsSnap.Count <= MaxSessionItems)
                    {
                        foreach (var det in detsSnap) SessionDetections.Add(det);
                    }
                    else if (!_sessionOverflowLogged)
                    {
                        _sessionOverflowLogged = true;
                        _logService.Warn($"Список результатов ограничен {MaxSessionItems} записями (длинное видео)");
                    }
                }
            }));
        }

        private void StopDetConsumer()
        {
            try { _detCts?.Cancel(); } catch { }
            _detCts = null;
            var old = Interlocked.Exchange(ref _pendingFd, null);
            if (old != null) { try { old.Frame?.Dispose(); } catch { } }
            _liveView = new List<Detection>();
        }

        private void FramePump_PlaybackFinished(object sender, EventArgs e)
        {
            StopDetConsumer();
            _isRecording = false;
            IsPlaying = false;
            StatusText = "Playback finished";
            _logService.Info($"Результаты сохранены в сессии: {SessionDetections.Count} детекций — " +
                             "список RESULTS справа, клик по строке — переход к кадру");
        }

        /// <summary>Клик по строке RESULTS: показать тот кадр (видео: переоткрыть + seek).</summary>
        partial void OnSelectedSessionDetectionChanged(Detection value)
        {
            if (value == null) return;
            JumpToFrame(value.FrameIndex, $"RESULTS: {value.ClassName}");
        }

        /// <summary>Клик по мини-таймлайну.</summary>
        public void JumpToTimelineFrame(int frameIndex) => JumpToFrame(frameIndex, "TIMELINE");

        // ---------- Превью по выбору источника (без Play) ----------
        private void OnSourcesViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SourcesViewModel.SelectedSource))
                ShowPreviewForSelected();
        }

        /// <summary>
        /// Выбор видео/картинки в SOURCES сразу показывает его в PREVIEW: картинку —
        /// как есть, видео — нулевой кадр с детекциями/боксами. Прогон при этом НЕ стартует.
        /// Если играет выбранный же файл — прогону не мешаем; если другой — Stop() (он
        /// неблокирующий) и показ нового.
        /// </summary>
        private void ShowPreviewForSelected()
        {
            var src = SourcesViewModel.SelectedSource;
            if (src == null || _isExporting) return;

            if (_framePump.IsPlaying)
            {
                if (string.Equals(_currentSourcePath, src.FilePath, StringComparison.OrdinalIgnoreCase))
                    return; // это он и так на экране, в движении
                Stop();
            }

            _framePump.ShowFrameAt(src.FilePath, 0);
            StatusText = $"Preview: {src.FileName}";
            _logService.Info($"Превью {src.FileName} без прогона (Play — для детекции в движении)");
        }

        private void JumpToFrame(int frameIndex, string reason)
        {
            var src = SourcesViewModel.SelectedSource;
            if (src == null)
            {
                _logService.Warn("Источник не выбран — перейти к кадру нельзя");
                return;
            }

            if (_framePump.IsPlaying)
                Stop();

            _framePump.ShowFrameAt(src.FilePath, frameIndex);
            _logService.Info($"Переход к кадру {frameIndex} ({reason})");
        }

        private void UpdateTimeline(int frameIndex)
        {
            CurrentFrameIndex = frameIndex;

            if (!_framePump.IsVideoSource) return;

            int fc = _framePump.CurrentVideoFrameCount;
            if (fc > 1)
            {
                HasTimeline = true;
                TimelineFrameCount = fc;
            }

            int idx = frameIndex;
            if (idx < 0) return;

            if (_isRecording && idx >= _tlHits.Length)
                Array.Resize(ref _tlHits, Math.Max(64, Math.Max(_tlHits.Length * 2, idx + 1)));

            if (_isRecording && idx < _tlHits.Length)
            {
                _tlHits[idx]++;
                TimelineChanged?.Invoke();
            }
        }

        public int[] GetTimelineHitsSnapshot() => (int[])_tlHits.Clone();


        // ---------- Отметки кадров для выборочного экспорта ----------
        private void MarkCurrentFrame()
        {
            if (CurrentFrameIndex < 0)
            {
                _logService.Warn("Нет текущего кадра — сначала откройте источник");
                return;
            }
            // toggle: повторное нажатие на тот же кадр снимает отметку (без дублей)
            if (!_exportMarks.Add(CurrentFrameIndex)) _exportMarks.Remove(CurrentFrameIndex);
            UpdateMarksUi();
        }

        private void ClearExportMarks()
        {
            _exportMarks.Clear();
            UpdateMarksUi();
        }

        private void UpdateMarksUi()
        {
            string text = _exportMarks.Count == 0 ? "" :
                $"marked {_exportMarks.Count}: " + string.Join(", ", _exportMarks.Take(10)) +
                (_exportMarks.Count > 10 ? " …" : "");
            try { Application.Current?.Dispatcher?.BeginInvoke(new Action(() => ExportMarksText = text)); }
            catch { ExportMarksText = text; }
            _logService.Info(_exportMarks.Count == 0
                ? "Отметки кадров сняты — экспорт будет по всем кадрам с детекциями"
                : $"Кадр {CurrentFrameIndex}: всего отмечено {_exportMarks.Count} — экспорт сохранит только их");
        }

        /// <summary>Экспорт кадров с боксами в выбранную папку. Если есть отметки — только они
        /// (по одному файлу на кадр, без повторов); иначе — все кадры с детекциями. Прогресс —
        /// в строке статуса; экспорт не блокирует UI и не ждёт помп синхронно.</summary>
        private async Task ExportFramesAsync()
        {
            var src = SourcesViewModel.SelectedSource;
            if (src == null) { _logService.Warn("Экспорт кадров: источник не выбран"); return; }
            if (_detectorService == null) { _logService.Error("Экспорт: модель не загружена"); return; }
            if (_isExporting) { _logService.Warn("Экспорт уже выполняется"); return; }
            _isExporting = true; // со ЩИТА до диалога: двойной клик не откроет два окна

            try
            {
                if (_framePump.IsPlaying)
                {
                    _logService.Info("Останавливаю воспроизведение ради экспорта");
                    Stop();
                    await Task.Delay(150);
                }

                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Куда сохранить кадры" };
                if (dlg.ShowDialog() != true) return;
                string folder = dlg.FolderName;

                var wanted = _exportMarks.ToList(); // копия: набор может меняться с UI
                bool selective = wanted.Count > 0;
                _logService.Info(selective
                    ? $"Экспорт кадров начат: выборочно {wanted.Count} шт…"
                    : "Экспорт кадров начат: все кадры с детекциями…");

                await Task.Run(() =>
                {
                    var fss = new FileSourceService(_logService); // свой capture — не мешает UI-потоку
                    var tracker = EnableTracking ? new TrackerService(null) : null;
                    int saved = 0, processed = 0;
                    int total = selective ? wanted.Count : Math.Max(1, _framePump.CurrentVideoFrameCount);
                    var swTick = System.Diagnostics.Stopwatch.StartNew();
                    string baseName = Path.GetFileNameWithoutExtension(src.FileName);

                    void Report()
                    {
                        _ = Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                            StatusText = $"export {processed}/{total} · saved {saved}"));
                    }

                    void Process(FrameData fd, bool forceSave)
                    {
                        processed++;
                        try
                        {
                            var dets = _detectorService.Detect(fd.Frame, ConfidenceThreshold, NmsThreshold);
                            if (FilterPersonOnly)
                                dets = dets.FindAll(d => string.Equals(d.ClassName, "person", StringComparison.OrdinalIgnoreCase));
                            if (tracker != null)
                                dets = tracker.Update(dets);

                            if (dets.Count > 0 || forceSave)
                            {
                                using Mat ann = _renderService.DrawDetections(fd.Frame, dets, HlMode);
                                string path = Path.Combine(folder, $"{baseName}_f{fd.FrameIndex:00000}.jpg");
                                if (ann.SaveImage(path, new ImageEncodingParam(ImwriteFlags.JpegQuality, 92)))
                                    saved++;
                            }
                        }
                        finally { fd.Frame?.Dispose(); }

                        if (processed % 50 == 0 || swTick.ElapsedMilliseconds > 400)
                        {
                            swTick.Restart();
                            Report();
                            if (processed % 250 == 0)
                                _logService.Info($"Экспорт кадров: обработано {processed}/{total}, сохранено {saved}");
                        }
                    }

                    try
                    {
                        if (fss.IsImageFile(src.FilePath))
                        {
                            if (fss.OpenImage(src.FilePath, out _, out _))
                            {
                                var fd = fss.GetCurrentImageFrame();
                                if (fd != null) Process(fd, true);
                            }
                            else throw new Exception("Не удалось открыть изображение");
                        }
                        else
                        {
                            if (!fss.OpenVideo(src.FilePath, out int fc, out _, out _, out _))
                                throw new Exception("Не удалось открыть видео");
                            if (!selective && fc > 0) total = fc; // честный знаменатель прогресса

                            if (selective)
                            {
                                // только отмеченные: seek+read по индексу; Set уже без дублей,
                                // лишние (за границами) отбрасываем; повторный idx физически невозможен
                                foreach (int idx in wanted.Where(i => i >= 0 && i < fc).Distinct())
                                {
                                    if (!fss.SeekFrame(idx)) { _logService.Warn($"Seek к кадру {idx} не удался — пропуск"); continue; }
                                    if (fss.ReadNextFrame(out var fd)) Process(fd, true);
                                }
                            }
                            else
                            {
                                while (fss.ReadNextFrame(out var fd))
                                    Process(fd, false);
                            }

                            _logService.Info($"Экспорт: видео {fc} кадров, обработано {processed}");
                        }

                        _logService.Info($"✓ Кадры сохранены: {saved} шт. в {folder}");
                        Report();
                        _ = Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                            StatusText = $"export done: {saved} files"));
                    }
                    finally { fss.Close(); }
                });
            }
            catch (Exception ex)
            {
                _logService.Error($"Ошибка экспорта кадров: {ex.Message}");
                _ = Application.Current?.Dispatcher?.BeginInvoke(new Action(() => StatusText = "export failed"));
            }
            finally
            {
                _isExporting = false;
            }
        }

        // ---------- Экспорт видео с обводкой ----------
        private async Task ExportVideoAsync()
        {
            var src = SourcesViewModel.SelectedSource;
            if (src == null) { _logService.Warn("Экспорт видео: источник не выбран"); return; }
            if (_detectorService == null) { _logService.Error("Экспорт видео: модель не загружена"); return; }
            if (_isExporting) { _logService.Warn("Экспорт уже выполняется"); return; }
            if (_fileSourceService.IsImageFile(src.FilePath))
            {
                _logService.Warn("Экспорт видео: выбранное изображение — возьмите «Frames»");
                return;
            }
            _isExporting = true;

            try
            {
                if (_framePump.IsPlaying)
                {
                    _logService.Info("Останавливаю воспроизведение ради экспорта видео");
                    Stop();
                    await Task.Delay(150);
                }

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Сохранить видео с обводкой",
                    Filter = "MP4 (H.264 при доступности, иначе MPEG-4)|*.mp4|AVI (Motion JPEG — гарантированно)|*.avi",
                    FileName = Path.GetFileNameWithoutExtension(src.FileName) + "_annotated",
                    DefaultExt = ".mp4",
                    AddExtension = true
                };
                if (dlg.ShowDialog() != true) return;
                string outPath = dlg.FileName;

                _logService.Info("Экспорт видео начат… (режим подсветки: " +
                                 new[] { "боксы", "контур", "боксы+контур", "маска+контур" }[(int)HlMode] + ")");
                StatusText = "video export: preparing…";

                int written = await Task.Run(() => ExportVideoCore(src.FilePath, outPath));
                _logService.Info(written > 0
                    ? $"✓ Видео сохранено: {written} кадров → {outPath}"
                    : "Экспорт видео не выполнен (см. сообщения выше)");
                StatusText = written > 0 ? $"video export done: {written} frames" : "video export failed";
            }
            catch (Exception ex)
            {
                _logService.Error($"Ошибка экспорта видео: {ex.Message}");
                StatusText = "video export failed";
            }
            finally
            {
                _isExporting = false;
            }
        }

        private int ExportVideoCore(string srcPath, string outPath)
        {
            var fss = new FileSourceService(_logService); // свой capture, не мешаем UI
            var tracker = EnableTracking ? new TrackerService(null) : null;
            // выбор переносим СНИПШОТОМ (класс+бокс) — ре-захват по первому кадру экспорта
            var selExport = SnapshotSelection();
            int written = 0;
            VideoWriter writer = null;
            try
            {
                if (!fss.OpenVideo(srcPath, out int fc, out int fps, out _, out _))
                    throw new Exception("Не удалось открыть видео");
                fps = Math.Max(1, fps);

                bool avi = string.Equals(Path.GetExtension(outPath), ".avi", StringComparison.OrdinalIgnoreCase);
                // mp4v/MJPG гарантированно пишет встроенный ffmpeg-бэкенд OpenCvSharp4;
                // avc1 — вторым номером (если H.264-энкодер в сборке есть).
                string[] codecIds = avi ? new[] { "MJPG" } : new[] { "mp4v", "avc1" };

                var swTick = System.Diagnostics.Stopwatch.StartNew();
                long lastUi = 0;
                int processed = 0;
                try
                {
                    while (fss.ReadNextFrame(out var fd))
                    {
                        try
                        {
                            var dets = new List<Detection>();
                            if (fd.Frame != null && !fd.Frame.IsDisposed)
                            {
                                dets = _detectorService.Detect(fd.Frame, ConfidenceThreshold, NmsThreshold);
                                if (FilterPersonOnly)
                                    dets = dets.FindAll(d => string.Equals(d.ClassName, "person", StringComparison.OrdinalIgnoreCase));
                                if (tracker != null) dets = tracker.Update(dets);
                            }
                            if (selExport.Count > 0)
                            {
                                foreach (var d in dets) d.IsSelected = false;
                                SelectionLogic.Apply(dets, selExport);
                                dets = dets.FindAll(dd => dd.IsSelected); // в экспорт — только выбранные
                            }
                            using Mat ann = _renderService.DrawDetections(fd.Frame ?? new Mat(), dets, HlMode);
                            if (ann.Empty()) continue;

                            if (writer == null)
                            {
                                // размер берём из ПЕРВОГО реального кадра, не из свойств потока:
                                // рассогласование размера с пикселями = «битый» MP4
                                OpenCvSharp.Size fsize = new OpenCvSharp.Size(ann.Width, ann.Height);
                                foreach (var id in codecIds)
                                {
                                    // FourCC руками — как cv::VideoWriter::fourcc, без P/Invoke при пробe
                                    int fcc = id[0] | (id[1] << 8) | (id[2] << 16) | (id[3] << 24);
                                    var tw = new VideoWriter(outPath, fcc, fps, fsize, true);
                                    if (tw.IsOpened())
                                    {
                                        writer = tw;
                                        _logService.Info($"Видеоэкспорт: кодек {id}, {fsize.Width}x{fsize.Height} @ {fps} fps");
                                        break;
                                    }
                                    tw.Dispose();
                                    try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                                }
                                if (writer == null)
                                    throw new Exception("Ни один видео-кодек не открылся (mp4v/avc1 или MJPG для .avi)");
                            }

                            writer.Write(ann);
                            written++;
                        }
                        finally { fd.Frame?.Dispose(); }

                        processed++;
                        if (processed % 50 == 0 || swTick.ElapsedMilliseconds - lastUi > 400)
                        {
                            lastUi = swTick.ElapsedMilliseconds;
                            int done = processed, totalF = Math.Max(fc, processed + 1);
                            _ = Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                                StatusText = $"video export {done}/{totalF}"));
                        }
                    }
                }
                finally
                {
                    writer?.Release();   // дописать атом-бокс mp4 — без этого файл «битый»
                    writer?.Dispose();
                }

                // если не записалось ни кадра — не оставлять битый файл
                if (written == 0)
                {
                    try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                    throw new Exception("Не записано ни одного кадра — пустой файл удалён");
                }
                try
                {
                    long len = new FileInfo(outPath).Length;
                    if (len < 4096)
                        _logService.Warn($"Файл подозрительно маленький ({len} Б) — попробуйте AVI/MJPG");
                }
                catch { }
            }
            finally { fss.Close(); }
            return written;
        }

        // ---------- Экспорт результатов ----------
        private string ExportBaseName() =>
            Path.GetFileNameWithoutExtension(SourcesViewModel.SelectedSource?.FileName ?? "detections") + "_detections";

        private List<Detection> ExportSnapshot() =>
            SessionDetections.Count > 0 ? SessionDetections.ToList() : Detections.ToList();

        private void ExportCsv()
        {
            var data = ExportSnapshot();
            if (data.Count == 0) { _logService.Warn("Экспорт: нечего сохранять (прогон ещё не выполнен)"); return; }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV файл|*.csv|Все файлы|*.*",
                FileName = ExportBaseName() + ".csv"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("frame;time;class;confidence;x;y;width;height;track_id");
                foreach (var d in data)
                    sb.AppendLine($"{d.FrameIndex};{d.Timestamp:HH:mm:ss.fff};{d.ClassName};{d.Confidence:F3};" +
                                  $"{d.X};{d.Y};{d.Width};{d.Height};{(d.TrackId?.ToString() ?? "")}");
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true)); // BOM — Excel дружит с ; и кириллицей
                _logService.Info($"CSV сохранён: {dlg.FileName} ({data.Count} записей)");
            }
            catch (Exception ex) { _logService.Error($"Ошибка CSV: {ex.Message}"); }
        }

        private void ExportJson()
        {
            var data = ExportSnapshot();
            if (data.Count == 0) { _logService.Warn("Экспорт: нечего сохранять (прогон ещё не выполнен)"); return; }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON файл|*.json|Все файлы|*.*",
                FileName = ExportBaseName() + ".json"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var payload = data.Select(d => new
                {
                    frame = d.FrameIndex,
                    time = d.Timestamp.ToString("HH:mm:ss.fff"),
                    cls = d.ClassName,
                    conf = (float)Math.Round(d.Confidence, 3),
                    box = new { x = d.X, y = d.Y, w = d.Width, h = d.Height },
                    track = d.TrackId
                }).ToList();
                string json = System.Text.Json.JsonSerializer.Serialize(payload,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(dlg.FileName, json, new UTF8Encoding(false));
                _logService.Info($"JSON сохранён: {dlg.FileName} ({data.Count} записей)");
            }
            catch (Exception ex) { _logService.Error($"Ошибка JSON: {ex.Message}"); }
        }

        private void FramePump_ErrorOccurred(object sender, string errorMessage)
        {
            _logService.Error(errorMessage);
            IsPlaying = false;
        }
    }
}