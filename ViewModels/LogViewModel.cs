using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ObjectDetectTrackApp.Services;

namespace ObjectDetectTrackApp.ViewModels
{
    public partial class LogViewModel : ObservableObject
    {
        private readonly LogService _logService;

        [ObservableProperty]
        public ObservableCollection<LogEntry> logs;

        public LogViewModel(LogService logService)
        {
            _logService = logService;
            // Привязываемся к коллекции логов
            Logs = new ObservableCollection<LogEntry>(_logService.Logs);

            // Подписываемся на новые логи (через рефлексию или простой callback)
            // Для MVP достаточно привязать один раз к коллекции
        }
    }
}