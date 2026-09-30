using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using RequestBoard.Contracts;

namespace RequestBoard.UI
{
    public partial class BoardSettingsControl : UserControl
    {
        private readonly RequestBoardPlugin _plugin;
        private bool _rulesBusy;

        public BoardSettingsControl(RequestBoardPlugin plugin)
        {
            InitializeComponent();
            _plugin = plugin;
            IsVisibleChanged += (s, e) => { if ((bool)e.NewValue) PullSettings(); };
        }

        private void Pull_Click(object sender, RoutedEventArgs e) => PullSettings();

        public async void PullSettings()
        {
            if (_rulesBusy) return;
            _rulesBusy = true;
            RulesStatus.Text = "Loading rules from the service...";
            try
            {
                var result = await Task.Run(() => _plugin.Backend.GetSettingsAsync());
                ShowSettings(result.Settings);
                RulesStatus.Text = $"Loaded from the service at {DateTime.Now:HH:mm:ss}.";
            }
            catch (Exception ex)
            {
                RulesStatus.Text = "Could not load the rules: " + ex.Message;
            }
            finally
            {
                _rulesBusy = false;
            }
        }

        private async void Push_Click(object sender, RoutedEventArgs e)
        {
            if (_rulesBusy || !(RulesFields.DataContext is BoardSettings settings)) return;
            if (HasErrors(RulesFields))
            {
                RulesStatus.Text = "Fix the fields marked in red first.";
                return;
            }
            _rulesBusy = true;
            RulesStatus.Text = "Saving rules to the service...";
            try
            {
                var command = new SaveSettingsCommand { ServerName = _plugin.ConfigData.ServerName, Settings = settings };
                var result = await Task.Run(() => _plugin.Backend.SaveSettingsAsync(command));
                if (result.Ok)
                {
                    ShowSettings(result.Settings);
                    RulesStatus.Text = result.Message;
                }
                else
                {
                    RulesStatus.Text = "Not saved: " + result.Message;
                }
            }
            catch (Exception ex)
            {
                RulesStatus.Text = "Could not save the rules: " + ex.Message;
            }
            finally
            {
                _rulesBusy = false;
            }
        }

        private void ShowSettings(BoardSettings settings)
        {
            if (settings == null) return;
            RulesFields.DataContext = settings;
            RulesFields.IsEnabled = true;
            PushButton.IsEnabled = true;
        }

        private static bool HasErrors(DependencyObject root) =>
            Validation.GetHasError(root) || LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().Any(HasErrors);
    }
}
