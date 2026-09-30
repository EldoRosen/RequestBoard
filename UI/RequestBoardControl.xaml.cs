using System.Windows;
using System.Windows.Controls;

namespace RequestBoard.UI
{
    public partial class RequestBoardControl : UserControl
    {
        private readonly RequestBoardPlugin _plugin;

        public RequestBoardControl(RequestBoardPlugin plugin)
        {
            InitializeComponent();
            _plugin = plugin;
            DataContext = plugin.ConfigData;   // fields bind straight to the config
            RefreshGrid();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _plugin.SaveConfig();
            TestStatus.Text = "Saved.";
        }

        private void Test_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_plugin.ConfigData.WebhookUrl))
            {
                TestStatus.Text = "Enter a webhook URL first.";
                return;
            }
            _plugin.SaveConfig();
            _plugin.Webhook.Send("✅ Request Board test", "Webhook is working.", 0x2ECC71,
                new EmbedField("Sector", _plugin.ConfigData.ServerName));
            TestStatus.Text = "Test message queued - check your Discord channel.";
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshGrid();

        private void AdminCancel_Click(object sender, RoutedEventArgs e)
        {
            var selected = RequestsGrid.SelectedItem as Request;
            if (selected == null) return;
            var key = selected.Key;
            // Balance changes must run on the game thread.
            _plugin.Torch.Invoke(() =>
            {
                var result = _plugin.Service.AdminCancel(key);
                Dispatcher.Invoke(() => { TestStatus.Text = result.Message; RefreshGrid(); });
            });
        }

        private void RefreshGrid() => RequestsGrid.ItemsSource = _plugin.Service.GetActive();
    }
}
