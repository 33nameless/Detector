using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ObjectDetectTrackApp.Models;
using ObjectDetectTrackApp.Services;

namespace ObjectDetectTrackApp.ViewModels
{
    public partial class SourcesViewModel : ObservableObject
    {
        private readonly FileSourceService _fileSourceService;
        private readonly LogService _logService;

        /// <summary>Общая очередь (для совместимости) + раздельные списки для UI.</summary>
        [ObservableProperty]
        public ObservableCollection<SourceItem> sources = new();

        [ObservableProperty]
        public ObservableCollection<SourceItem> images = new();

        [ObservableProperty]
        public ObservableCollection<SourceItem> videos = new();

        [ObservableProperty]
        public SourceItem selectedSource;

        // Отдельные выделения для каждого списка: два ListBox на одном SelectedSource
        // перетерали бы друг другу выбор null-ом.
        [ObservableProperty]
        public SourceItem imageSelected;

        [ObservableProperty]
        public SourceItem videoSelected;

        public ICommand AddImageCommand { get; }
        public ICommand AddVideoCommand { get; }
        public ICommand RemoveSourceCommand { get; }
        public ICommand RemoveSelectedCommand { get; }

        public SourcesViewModel(FileSourceService fileSourceService, LogService logService)
        {
            _fileSourceService = fileSourceService;
            _logService = logService;

            AddImageCommand = new AsyncRelayCommand(AddImageAsync);
            AddVideoCommand = new AsyncRelayCommand(AddVideoAsync);
            RemoveSourceCommand = new RelayCommand<SourceItem>(RemoveSource);
            RemoveSelectedCommand = new RelayCommand(RemoveSelected);
        }

        partial void OnImageSelectedChanged(SourceItem value)
        {
            if (value == null) return;      // снятие выделения игнорируем
            VideoSelected = null;
            SelectedSource = value;
        }

        partial void OnVideoSelectedChanged(SourceItem value)
        {
            if (value == null) return;
            ImageSelected = null;
            SelectedSource = value;
        }

        private bool AlreadyQueued(string path) =>
            Sources.Any(s => string.Equals(s.FilePath, path, StringComparison.OrdinalIgnoreCase));

        private async Task AddImageAsync()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Image files (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp",
                CheckFileExists = true,
                Multiselect = true
            };

            if (dialog.ShowDialog() == true)
            {
                foreach (var file in dialog.FileNames)
                {
                    if (!_fileSourceService.IsImageFile(file)) continue;
                    if (AlreadyQueued(file)) { _logService.Warn($"Уже в очереди: {System.IO.Path.GetFileName(file)}"); continue; }

                    var source = new SourceItem(file, SourceType.Image);
                    Sources.Add(source);
                    Images.Add(source);
                    ImageSelected = source;
                    _logService.Info($"Added image: {source.FileName}");
                }
            }
            await Task.CompletedTask;
        }

        private async Task AddVideoAsync()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Video files (*.mp4;*.avi;*.mov;*.mkv)|*.mp4;*.avi;*.mov;*.mkv",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                if (!_fileSourceService.IsVideoFile(dialog.FileName))
                {
                    await Task.CompletedTask;
                    return;
                }
                if (AlreadyQueued(dialog.FileName))
                {
                    _logService.Warn($"Уже в очереди: {System.IO.Path.GetFileName(dialog.FileName)}");
                    return;
                }

                // свой экземпляр для проверки, чтобы не переоткрывать capture основного потока
                var probe = new FileSourceService(_logService);
                try
                {
                    if (probe.OpenVideo(dialog.FileName, out int frameCount, out int fps, out int width, out int height))
                    {
                        var source = new SourceItem(dialog.FileName, SourceType.Video) { FrameCount = frameCount };
                        Sources.Add(source);
                        Videos.Add(source);
                        VideoSelected = source;
                        _logService.Info($"Added video: {source.FileName} ({frameCount} frames, {fps} fps, {width}x{height})");
                    }
                    else
                    {
                        _logService.Error($"Failed to open video: {dialog.FileName}");
                    }
                }
                finally { probe.Close(); }
            }
            await Task.CompletedTask;
        }

        private void RemoveSource(SourceItem source)
        {
            if (source == null) return;
            Sources.Remove(source);
            Images.Remove(source);
            Videos.Remove(source);
            if (ReferenceEquals(SelectedSource, source)) SelectedSource = null;
            _logService.Info($"Removed: {source.FileName}");
        }

        private void RemoveSelected()
        {
            if (SelectedSource == null)
            {
                _logService.Warn("Удаление: выберите источник в списке");
                return;
            }
            var victim = SelectedSource;
            RemoveSource(victim);
        }
    }
}
