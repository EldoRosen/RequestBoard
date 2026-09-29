using System;
using System.Linq;
using NexusModAPI;
using Sandbox.ModAPI;
using VRageMath;
using NLog;

namespace RequestBoard
{
    /// <summary>
    /// Thin wrapper around the Nexus V3 ModAPI. Everything Nexus-specific lives
    /// here so the rest of the plugin only deals with simple events/calls.
    /// Only ever call this on the game thread, after the session has loaded.
    /// </summary>
    public class NexusBridge : IDisposable
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly RequestBoardConfig _cfg;
        private NexusAPI _api;
        private bool _registered;

        public event Action<NexusRequestDto> RequestEventReceived;
        public event Action<NexusRelayActionDto> RelayActionReceived;

        public NexusBridge(RequestBoardConfig cfg) { _cfg = cfg; }

        /// <summary>True once this API instance has connected to the local Nexus plugin.</summary>
        public bool Enabled => _cfg.NexusEnabled && _api != null && _api.Enabled;

        public byte CurrentServerId => _api?.CurrentServerID ?? 0;

        public string CurrentServerName
        {
            get
            {
                if (!Enabled) return _cfg.ServerName;
                var status = _api.GetServerStatus(_api.CurrentServerID);
                return string.IsNullOrEmpty(status.Item5) ? _cfg.ServerName : status.Item5;
            }
        }

        public void Start()
        {
            if (!_cfg.NexusEnabled || _api != null) return;
            _api = new NexusAPI(() => Log.Info("RequestBoard: connected to Nexus V3."));
            MyAPIGateway.Utilities.RegisterMessageHandler(_cfg.NexusChannelId, OnChannelMessage);
            _registered = true;
        }

        public void Stop()
        {
            if (_registered) MyAPIGateway.Utilities.UnregisterMessageHandler(_cfg.NexusChannelId, OnChannelMessage);
            _registered = false;
            _api?.Unload();
            _api = null;
        }

        public void Dispose() => Stop();

        public void BroadcastRequest(NexusRequestDto dto)
        {
            if (!Enabled) return;
            Send(new NexusPayload { Type = NexusMsgType.RequestEvent, Request = dto }, null);
        }

        public void SendRelayAction(byte targetServer, NexusRelayActionDto action)
        {
            if (!Enabled) return;
            Send(new NexusPayload { Type = NexusMsgType.RelayAction, Action = action }, targetServer);
        }

        /// <summary>Sector name for a world position, or null if Nexus can't place it.</summary>
        public string SectorNameFor(Vector3D position)
        {
            if (!Enabled) return null;
            try
            {
                var serverId = _api.GetTargetServer(position);
                if (serverId == 0) return null;
                var status = _api.GetServerStatus(serverId);
                return string.IsNullOrEmpty(status.Item5) ? null : status.Item5;
            }
            catch (Exception e) { Log.Warn(e, "RequestBoard: Nexus sector lookup failed"); return null; }
        }

        private void Send(NexusPayload payload, byte? targetServer)
        {
            try
            {
                var bytes = MyAPIGateway.Utilities.SerializeToBinary(payload);
                if (targetServer.HasValue) _api.SendModMsgToServer(bytes, _cfg.NexusChannelId, targetServer.Value);
                else _api.SendModMsgToAllServers(bytes, _cfg.NexusChannelId);
            }
            catch (Exception e) { Log.Warn(e, "RequestBoard: failed to send Nexus message"); }
        }

        private void OnChannelMessage(object obj)
        {
            try
            {
                var msg = MyAPIGateway.Utilities.SerializeFromBinary<NexusAPI.ModAPIMsg>((byte[])obj);
                if (msg?.msgData == null) return;
                var payload = MyAPIGateway.Utilities.SerializeFromBinary<NexusPayload>(msg.msgData);
                if (payload == null) return;

                if (payload.Type == NexusMsgType.RequestEvent && payload.Request != null)
                {
                    if (payload.Request.OriginServerId == CurrentServerId) return; // our own echo
                    RequestEventReceived?.Invoke(payload.Request);
                }
                else if (payload.Type == NexusMsgType.RelayAction && payload.Action != null)
                {
                    RelayActionReceived?.Invoke(payload.Action);
                }
            }
            catch (Exception e) { Log.Warn(e, "RequestBoard: bad Nexus message received"); }
        }
    }
}
