using System.Windows;
using ObjectDetectTrackApp.ViewModels;

namespace ObjectDetectTrackApp
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = new MainViewModel();
        }

        private void LogListBox_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var listBox = sender as System.Windows.Controls.ListBox;
            var viewModel = this.DataContext as MainViewModel;

            if (viewModel?.LogViewModel?.Logs == null || viewModel.LogViewModel.Logs.Count == 0)
            {
                MessageBox.Show("Логи пусты!", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Если клик по элементу - копируем этот элемент
            if (listBox?.SelectedItem != null)
            {
                var selectedLog = listBox.SelectedItem.ToString();
                CopyToClipboard(selectedLog);
                e.Handled = true;
                return;
            }

            // Если клик в пустое место - копируем все логи
            var allLogs = string.Join(System.Environment.NewLine,
                viewModel.LogViewModel.Logs.Select(log => log.ToString()));

            CopyToClipboard(allLogs);
            e.Handled = true;
        }

        private void CopyToClipboard(string text)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                MessageBox.Show("✓ Скопировано в буфер обмена!", "Success",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}