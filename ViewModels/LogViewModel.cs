using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ObjectDetectTrackApp.Services;

namespace ObjectDetectTrackApp.ViewModels
{
    public partial class LogViewModel : ObservableObject
    {
        private readonly LogService _logService;

        /// <summary>Тот же экземпляр коллекции LogService — обновления приходят «живьём».</summary>
        public ObservableCollection<LogEntry> Logs { get; }

        public LogViewModel(LogService logService)
        {
            _logService = logService;
            Logs = _logService.Logs;
        }
    }
}
