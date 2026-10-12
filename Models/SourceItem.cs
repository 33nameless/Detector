using CommunityToolkit.Mvvm.ComponentModel;

namespace ObjectDetectTrackApp.Models
{
    public partial class SourceItem : ObservableObject
    {
        private string _filePath;
        private string _fileName;
        private SourceType _sourceType;
        private int _frameCount;
        private int _currentFrame;
        private bool _isProcessing;

        public string FilePath
        {
            get => _filePath;
            set => SetProperty(ref _filePath, value);
        }

        public string FileName
        {
            get => _fileName;
            set => SetProperty(ref _fileName, value);
        }

        public SourceType SourceType
        {
            get => _sourceType;
            set => SetProperty(ref _sourceType, value);
        }

        public int FrameCount
        {
            get => _frameCount;
            set => SetProperty(ref _frameCount, value);
        }

        public int CurrentFrame
        {
            get => _currentFrame;
            set => SetProperty(ref _currentFrame, value);
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            set => SetProperty(ref _isProcessing, value);
        }

        public SourceItem(string path, SourceType type)
        {
            FilePath = path;
            FileName = System.IO.Path.GetFileName(path);
            SourceType = type;
            FrameCount = 0;
            CurrentFrame = 0;
            IsProcessing = false;
        }
    }

    public enum SourceType
    {
        Image,
        Video
    }
}