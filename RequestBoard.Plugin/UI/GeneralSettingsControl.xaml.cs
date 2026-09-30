using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace RequestBoard.UI
{
    public partial class GeneralSettingsControl : UserControl
    {
        private readonly RequestBoardPlugin _plugin;
        private bool _busy;

        public event Action Connected;

        public GeneralSettingsControl(RequestBoardPlugin plugin)
        {
            InitializeComponent();
            _plugin = plugin;
            DataContext = plugin.ConfigData;
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Request board database",
                Filter = "SQLite database (*.db)|*.db|All files (*.*)|*.*",
                OverwritePrompt = false,
                FileName = "requestboard.db"
            };
            try
            {
                var current = Path.GetFullPath(Path.Combine(_plugin.StoragePath, _plugin.ConfigData.DatabasePath ?? ""));
                dialog.InitialDirectory = Path.GetDirectoryName(current);
                dialog.FileName = Path.GetFileName(current);
            }
            catch (Exception) { }
            if (dialog.ShowDialog() == true) _plugin.ConfigData.DatabasePath = dialog.FileName;
        }

        private void Save_Click(object sender, RoutedEventArgs e) => OpenDatabase("Saved.");

        private void Open_Click(object sender, RoutedEventArgs e) => OpenDatabase(null);

        private async void OpenDatabase(string prefix)
        {
            if (_busy) return;
            _busy = true;
            _plugin.SaveConfig();
            Status.Text = "Opening the database...";
            try
            {
                var path = await Task.Run(() => _plugin.Board.Open());
                Status.Text = (prefix != null ? prefix + " " : "") + "Using " + path;
                Connected?.Invoke();
            }
            catch (Exception ex)
            {
                Status.Text = (prefix != null ? prefix + " " : "") + "Could not open the database: " + ex.Message;
            }
            finally
            {
                _busy = false;
            }
        }
    }
}
