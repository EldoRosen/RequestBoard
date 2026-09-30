using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace RequestBoard.UI
{
    public partial class ServiceSettingsControl : UserControl
    {
        private readonly RequestBoardPlugin _plugin;

        public event Action Connected;

        public ServiceSettingsControl(RequestBoardPlugin plugin)
        {
            InitializeComponent();
            _plugin = plugin;
            DataContext = plugin.ConfigData;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _plugin.SaveConfig();
            Status.Text = "Saved.";
        }

        private async void Test_Click(object sender, RoutedEventArgs e)
        {
            _plugin.SaveConfig();
            Status.Text = "Connecting...";
            try
            {
                await Task.Run(() => _plugin.Backend.CheckConnectionAsync());
                Status.Text = "Connected to the request board service.";
                Connected?.Invoke();
            }
            catch (Exception ex)
            {
                Status.Text = ex.Message;
            }
        }
    }
}
