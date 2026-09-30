using System.IO;
using System.Windows.Controls;
using Torch;
using Torch.API;
using Torch.API.Plugins;
using Torch.API.Session;
using RequestBoard.Backend;
using RequestBoard.UI;

namespace RequestBoard
{
    public class RequestBoardPlugin : TorchPluginBase, IWpfPlugin
    {
        public static RequestBoardPlugin Instance { get; private set; }

        private Persistent<RequestBoardConfig> _config;
        private RequestBoardControl _control;
        private ITorchSessionManager _sessions;

        public RequestBoardConfig ConfigData => _config.Data;
        public Persistent<RequestBoardConfig> Config => _config;
        public IRequestBoardBackend Backend { get; private set; }
        public RequestService Service { get; private set; }

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            Instance = this;

            _config = Persistent<RequestBoardConfig>.Load(Path.Combine(StoragePath, "RequestBoard.cfg"));
            Backend = new HttpRequestBoardBackend(() => _config.Data.ServiceUrl);
            Service = new RequestService(torch, _config.Data, Backend);

            // Don't start timers until the game session exists.
            _sessions = torch.Managers.GetManager(typeof(ITorchSessionManager)) as ITorchSessionManager;
            if (_sessions != null) _sessions.SessionStateChanged += OnSessionStateChanged;
        }

        private void OnSessionStateChanged(ITorchSession session, TorchSessionState state)
        {
            if (state == TorchSessionState.Loaded) Service.Start();
            else if (state == TorchSessionState.Unloading) Service.Stop();
        }

        public UserControl GetControl() => _control ?? (_control = new RequestBoardControl(this));

        public void SaveConfig() => _config.Save();

        public override void Dispose()
        {
            if (_sessions != null) _sessions.SessionStateChanged -= OnSessionStateChanged;
            Service?.Stop();
            _config?.Save();
            base.Dispose();
        }
    }
}
