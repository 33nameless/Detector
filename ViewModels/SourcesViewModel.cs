using System;
using System.Collections.ObjectModel;
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

        [ObservableProperty]
        public ObservableCollection<SourceItem> sources = new();

        [ObservableProperty]
        public SourceItem selectedSource;

        public ICommand AddImageCommand { get; }
        public ICommand AddVideoCommand { get; }
        public ICommand RemoveSourceCommand { get; }

        public SourcesViewModel(FileSourceService fileSourceService, LogService logService)
        {
            _fileSourceService = fileSourceService;
            _logService = logService;

            AddImageCommand = new AsyncRelayCommand(AddImageAsync);
            AddVideoCommand = new AsyncRelayCommand(AddVideoAsync);
            RemoveSourceCommand = new RelayCommand<SourceItem>(RemoveSource);
        }

        private async Task AddImageAsync()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Image files (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                if (_fileSourceService.IsImageFile(dialog.FileName))
                {
                    var source = new SourceItem(dialog.FileName, SourceType.Image);
                    Sources.Add(source);
                    SelectedSource = source;
                    _logService.Info($"Added image: {source.FileName}");
                }
            }
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
                if (_fileSourceService.IsVideoFile(dialog.FileName))
                {
                    var source = new SourceItem(dialog.FileName, SourceType.Video);

                    if (_fileSourceService.OpenVideo(dialog.FileName,
                        out int frameCount, out int fps, out int width, out int height))
                    {
                        source.FrameCount = frameCount;
                        Sources.Add(source);
                        SelectedSource = source;
                        _logService.Info($"Added video: {source.FileName} ({frameCount} frames, {fps} fps)");
                    }
                    else
                    {
                        _logService.Error($"Failed to open video: {dialog.FileName}");
                    }
                }
            }
        }

        private void RemoveSource(SourceItem source)
        {
            if (source != null)
            {
                Sources.Remove(source);
                _logService.Info($"Removed: {source.FileName}");
            }
        }
    }
}