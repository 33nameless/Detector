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
    /// Сервис логирования (потокобезопасный)
    /// </summary>
    public class LogService
    {
        private readonly ObservableCollection<LogEntry> _logs = new();
        private readonly object _lockObj = new();

        public IReadOnlyList<LogEntry> Logs => _logs;

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
            lock (_lockObj)
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    _logs.Insert(0, entry);

                    // Ограничиваем количество (последние 100)
                    while (_logs.Count > 100)
                    {
                        _logs.RemoveAt(_logs.Count - 1);
                    }
                });
            }
        }

        public void Clear()
        {
            lock (_lockObj)
            {
                Application.Current?.Dispatcher?.Invoke(() => _logs.Clear());
            }
        }
    }
}