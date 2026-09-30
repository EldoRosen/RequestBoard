using System;
using System.Collections.Generic;
using System.Linq;
using NexusModAPI;
using Sandbox.ModAPI;
using VRageMath;
using NLog;

namespace RequestBoard
{
    public class NexusBridge : IDisposable
    {
        private const int ChunkSize = 25;
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly RequestBoardConfig _cfg;
        private NexusAPI _api;
        private long _channel;

        public event Action Connected;
        public event Action<List<NexusRequestDto>> StateReceived;
        public event Action<byte> SyncRequested;
        public event Action<byte, List<NexusRequestDto>, bool> SyncResponseReceived;

        public NexusBridge(RequestBoardConfig cfg) { _cfg = cfg; }

        public bool Active => _api != null;

        public bool Enabled => _api != null && _api.Enabled;

        public byte CurrentServerId => Enabled ? _api.CurrentServerID : (byte)0;

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
            _channel = _cfg.NexusChannelId;
            MyAPIGateway.Utilities.RegisterMessageHandler(_channel, OnChannelMessage);
            _api = new NexusAPI(OnApiEnabled);
        }

        public void Stop()
        {
            if (_api == null) return;
            MyAPIGateway.Utilities.UnregisterMessageHandler(_channel, OnChannelMessage);
            _api.Unload();
            _api = null;
        }

        public void Dispose() => Stop();

        public List<byte> OnlinePeers()
        {
            if (!Enabled) return new List<byte>();
            try
            {
                var me = _api.CurrentServerID;
                return (_api.GetAllOnlineServers() ?? new List<byte>()).Where(s => s != me).Distinct().ToList();
            }
            catch (Exception e)
            {
                Log.Warn(e, "RequestBoard: could not list online Nexus servers");
                return new List<byte>();
            }
        }

        public void BroadcastState(IEnumerable<NexusRequestDto> requests)
        {
            if (!Enabled) return;
            foreach (var chunk in Chunk(requests))
                Send(new NexusPayload { Type = NexusMsgType.State, Requests = chunk }, null);
        }

        public void SendSyncRequest()
        {
            if (!Enabled) return;
            Send(new NexusPayload { Type = NexusMsgType.SyncRequest }, null);
        }

        public void SendSyncResponse(byte target, IEnumerable<NexusRequestDto> requests)
        {
            if (!Enabled) return;
            var chunks = Chunk(requests).ToList();
            if (chunks.Count == 0) chunks.Add(new List<NexusRequestDto>());
            for (var i = 0; i < chunks.Count; i++)
                Send(new NexusPayload { Type = NexusMsgType.SyncResponse, Requests = chunks[i], Last = i == chunks.Count - 1 }, target);
        }

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

        private static IEnumerable<List<NexusRequestDto>> Chunk(IEnumerable<NexusRequestDto> source)
        {
            var chunk = new List<NexusRequestDto>(ChunkSize);
            foreach (var item in source)
            {
                chunk.Add(item);
                if (chunk.Count < ChunkSize) continue;
                yield return chunk;
                chunk = new List<NexusRequestDto>(ChunkSize);
            }
            if (chunk.Count > 0) yield return chunk;
        }

        private void OnApiEnabled()
        {
            Log.Info($"RequestBoard: connected to Nexus V3 as server {_api?.CurrentServerID}.");
            Connected?.Invoke();
        }

        private void Send(NexusPayload payload, byte? targetServer)
        {
            try
            {
                payload.Protocol = NexusPayload.CurrentProtocol;
                payload.FromServerId = _api.CurrentServerID;
                var bytes = MyAPIGateway.Utilities.SerializeToBinary(payload);
                var sent = targetServer.HasValue
                    ? _api.SendModMsgToServer(bytes, _channel, targetServer.Value)
                    : _api.SendModMsgToAllServers(bytes, _channel);
                if (!sent) Log.Warn($"RequestBoard: Nexus did not accept {payload.Type} message" + (targetServer.HasValue ? $" for server {targetServer.Value}" : ""));
            }
            catch (Exception e) { Log.Warn(e, "RequestBoard: failed to send Nexus message"); }
        }

        private void OnChannelMessage(object obj)
        {
            try
            {
                if (!Enabled || !(obj is byte[] raw)) return;
                var msg = MyAPIGateway.Utilities.SerializeFromBinary<NexusAPI.ModAPIMsg>(raw);
                if (msg?.msgData == null) return;
                var payload = MyAPIGateway.Utilities.SerializeFromBinary<NexusPayload>(msg.msgData);
                if (payload == null) return;
                if (payload.Protocol != NexusPayload.CurrentProtocol)
                {
                    Log.Warn($"RequestBoard: ignored Nexus message from server {msg.fromServerID} with protocol {payload.Protocol} (this server uses {NexusPayload.CurrentProtocol}). Update the plugin on every server.");
                    return;
                }
                var from = payload.FromServerId;
                if (from == _api.CurrentServerID) return;

                var requests = payload.Requests ?? new List<NexusRequestDto>();
                switch (payload.Type)
                {
                    case NexusMsgType.State: StateReceived?.Invoke(requests); break;
                    case NexusMsgType.SyncRequest: SyncRequested?.Invoke(from); break;
                    case NexusMsgType.SyncResponse: SyncResponseReceived?.Invoke(from, requests, payload.Last); break;
                }
            }
            catch (Exception e) { Log.Warn(e, "RequestBoard: bad Nexus message received"); }
        }
    }
}
