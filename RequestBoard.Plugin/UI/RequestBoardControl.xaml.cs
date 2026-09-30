using System.Windows.Controls;

namespace RequestBoard.UI
{
    public partial class RequestBoardControl : UserControl
    {
        public RequestBoardControl(RequestBoardPlugin plugin)
        {
            InitializeComponent();
            var board = new BoardSettingsControl(plugin);
            var service = new ServiceSettingsControl(plugin);
            service.Connected += board.PullSettings;
            InfoTab.Content = new InfoControl(plugin);
            ServiceTab.Content = service;
            BoardTab.Content = board;
        }
    }
}
