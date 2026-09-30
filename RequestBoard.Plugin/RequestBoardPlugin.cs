using System;
using System.IO;
using System.Windows.Controls;
using NLog;
using Torch;
using Torch.API;
using Torch.API.Plugins;
using Torch.API.Session;
using RequestBoard.Board;
using RequestBoard.UI;

namespace RequestBoard
{
    public class RequestBoardPlugin : TorchPluginBase, IWpfPlugin
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public static RequestBoardPlugin Instance { get; private set; }

        private Persistent<RequestBoardConfig> _config;
        private RequestBoardControl _control;
        private ITorchSessionManager _sessions;
        private Database _database;
        private DiscordNotifier _discord;

        public RequestBoardConfig ConfigData => _config.Data;
        public Persistent<RequestBoardConfig> Config => _config;
        public BoardService Board { get; private set; }
        public RequestService Service { get; private set; }

        public override void Init(ITorchBase torch)
        {
            base.Init(torch);
            Instance = this;

            _config = Persistent<RequestBoardConfig>.Load(Path.Combine(StoragePath, "RequestBoard.cfg"));
            _database = new Database(() => _config.Data.DatabasePath, StoragePath);
            _discord = new DiscordNotifier();
            Board = new BoardService(_database, _discord, _config.Data);
            Service = new RequestService(torch, _config.Data, Board);

            try { Board.Open(); }
            catch (Exception e) { Log.Error(e, "RequestBoard: could not open the database, check the path in the Request Board tab"); }

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
            _discord?.Dispose();
            _database?.Dispose();
            _config?.Save();
            base.Dispose();
        }
    }
}
