using System;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ObjectDetectTrackApp.Services
{
    public enum LogLevel
    {
        Info,
        Warn,
        Error
    }

    public partial class LogEntry : ObservableObject
    {
        [ObservableProperty]
        public string message;

        [ObservableProperty]
        public LogLevel level;

        [ObservableProperty]
        public DateTime timestamp;

        public LogEntry(string msg, LogLevel lvl)
        {
            Message = msg;
            Level = lvl;
            Timestamp = DateTime.Now;
        }

        public override string ToString()
        {
            string levelStr = Level switch
            {
                LogLevel.Info => "INFO",
                LogLevel.Warn => "WARN",
                LogLevel.Error => "ERROR",
                _ => "???"
            };
            return $"[{Timestamp:HH:mm:ss}] [{levelStr}] {Message}";
        }
    }

    /// <summary>
    /// Сервис логирования (потокобезопасный, ФОНОВЫЕ ПОТОКИ НЕ БЛОКИРУЕТ).
    /// Изменения коллекции — всегда на UI-потоке, но через BeginInvoke: с синхронным
    /// Dispatcher.Invoke помп умирал в дедлоке, когда UI ждал его в Stop()/экспорте.
    /// </summary>
    public class LogService
    {
        private readonly ObservableCollection<LogEntry> _logs = new();

        /// <summary>
        /// Живая коллекция (тот же экземпляр, а не копия!). Изменяется только в UI-потоке
        /// (AddLog инлайнит на UI и BeginInvoke из фона), поэтому безопасно биндить на ListBox.
        /// </summary>
        public ObservableCollection<LogEntry> Logs => _logs;

        public void Info(string message)
        {
            AddLog(new LogEntry(message, LogLevel.Info));
        }

        public void Warn(string message)
        {
            AddLog(new LogEntry(message, LogLevel.Warn));
        }

        public void Error(string message)
        {
            AddLog(new LogEntry(message, LogLevel.Error));
        }

        private void AddLog(LogEntry entry)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            if (dispatcher.CheckAccess())
            {
                InsertUi(entry); // уже на UI — пишем сразу
            }
            else
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    try { InsertUi(entry); } catch { } // UI может умирать вместе с приложением
                }));
            }
        }

        private void InsertUi(LogEntry entry)
        {
            _logs.Insert(0, entry);

            // Ограничиваем количество (последние 100)
            while (_logs.Count > 100)
            {
                _logs.RemoveAt(_logs.Count - 1);
            }
        }

        public void Clear()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            if (dispatcher.CheckAccess()) _logs.Clear();
            else dispatcher.BeginInvoke(new Action(() =>
            {
                try { _logs.Clear(); } catch { }
            }));
        }
    }
}