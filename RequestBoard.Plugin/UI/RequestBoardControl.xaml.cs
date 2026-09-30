using System.Windows.Controls;

namespace RequestBoard.UI
{
    public partial class RequestBoardControl : UserControl
    {
        public RequestBoardControl(RequestBoardPlugin plugin)
        {
            InitializeComponent();
            var board = new BoardSettingsControl(plugin);
            var general = new GeneralSettingsControl(plugin);
            general.Connected += board.PullSettings;
            InfoTab.Content = new InfoControl(plugin);
            GeneralTab.Content = general;
            BoardTab.Content = board;
        }
    }
}
