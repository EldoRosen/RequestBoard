using System;
using System.Windows;
using System.Windows.Controls;
using RequestBoard.Contracts;

namespace RequestBoard.UI
{
    public partial class InfoControl : UserControl
    {
        private readonly RequestBoardPlugin _plugin;

        public InfoControl(RequestBoardPlugin plugin)
        {
            InitializeComponent();
            _plugin = plugin;
            _plugin.Service.Synced += () => Dispatcher.BeginInvoke(new Action(RefreshGrid));
            RefreshGrid();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            _plugin.Service.SyncNow();
            RefreshGrid();
        }

        private void AdminCancel_Click(object sender, RoutedEventArgs e)
        {
            if (!(RequestsGrid.SelectedItem is RequestDto selected)) return;
            if (!_plugin.Service.Running)
            {
                RequestsStatus.Text = "Start the server first, refunds can only be paid while the game is running.";
                return;
            }
            var id = selected.Id;
            _plugin.Service.AdminCancel(id, message => Dispatcher.BeginInvoke(new Action(() =>
            {
                RequestsStatus.Text = message;
                _plugin.Service.SyncNow();
            })));
        }

        private void RefreshGrid() => RequestsGrid.ItemsSource = _plugin.Service.Active;
    }
}
