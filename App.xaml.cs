using System.Windows;
using ObjectDetectTrackApp.Views;

namespace ObjectDetectTrackApp
{
    public partial class App : Application
    {
        /// <summary>
        /// StartupUri убран: сначала показываем ASCII-сплэш (Views/SplashWindow),
        /// он сам открывает MainWindow когда «загрузится» (или по Esc).
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            new SplashWindow().Show();
        }
    }
}
