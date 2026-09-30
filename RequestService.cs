using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using NLog;
using Sandbox.Game;
using Torch.API;

namespace RequestBoard
{
    public class Result
    {
        public bool Ok;
        public string Message;
        public static Result Fail(string m) => new Result { Ok = false, Message = m };
        public static Result Success(string m) => new Result { Ok = true, Message = m };
    }

    public class RequestService
    {
        private const int Blue = 0x3498DB, Yellow = 0xF1C40F, Green = 0x2ECC71, Red = 0xE74C3C, Grey = 0x95A5A6, Orange = 0xE67E22;
        private static readonly TimeSpan TimerGrace = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(20);
        private const int ResyncEveryTicks = 5;
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly ITorchBase _torch;
        private readonly RequestBoardConfig _cfg;
        private readonly DiscordWebhook _hook;
        private readonly NexusBridge _nexus;
        private readonly string _file;
        private readonly object _lock = new object();
        private List<Request> _requests = new List<Request>();
        private int _nextId = 1;
        private Timer _timer;
        private Timer _syncTimer;
        private bool _started;
        private bool _connected;
        private bool _synced;
        private int _tickCount;
        private readonly HashSet<byte> _syncPending = new HashSet<byte>();

        public RequestService(ITorchBase torch, RequestBoardConfig cfg, DiscordWebhook hook, NexusBridge nexus, string file)
        {
            _torch = torch; _cfg = cfg; _hook = hook; _nexus = nexus; _file = file;
            _nexus.Connected += () => OnGameThread(OnNexusConnected);
            _nexus.StateReceived += list => OnGameThread(() => OnState(list));
            _nexus.SyncRequested += from => OnGameThread(() => OnSyncRequested(from));
            _nexus.SyncResponseReceived += (from, list, last) => OnGameThread(() => OnSyncResponse(from, list, last));
        }

        public byte MyServerId => _nexus.CurrentServerId;

        public void Start()
        {
            if (_started) return;
            _started = true;
            lock (_lock) Load();
            _timer = new Timer(_ => OnGameThread(Tick), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
        }

        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
            _syncTimer?.Dispose();
            _syncTimer = null;
            lock (_lock)
            {
                if (_started) Save();
                _started = false;
                _connected = false;
                _synced = false;
                _syncPending.Clear();
            }
        }

        private void OnGameThread(Action action)
        {
            try { _torch.Invoke(action); }
            catch (Exception e) { Log.Warn(e, "RequestBoard could not schedule work on the game thread"); }
        }

        private Result NotReady()
        {
            if (!_cfg.Enabled) return Result.Fail("Request Board is currently disabled.");
            if (!_started) return Result.Fail("Request Board is still starting, try again in a few seconds.");
            if (!_nexus.Active) return null;
            if (!_nexus.Enabled) return Result.Fail("Request Board is waiting for the Nexus connection, try again shortly.");
            if (!_synced) return Result.Fail("Request Board is syncing with the other sectors, try again in a few seconds.");
            return null;
        }

        public Result Create(long playerId, string playerName, string text, string hoursStr, string priceStr, double[] position = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return Result.Fail("Request text can't be empty.");
            text = text.Trim();
            if (text.Length > 300) return Result.Fail("Request text is too long (max 300 characters).");

            if (!double.TryParse(hoursStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) || hours <= 0 || hours > _cfg.MaxHours)
                return Result.Fail($"Time must be a number of hours between 0 and {_cfg.MaxHours}.");

            var unlimited = _cfg.MaxPrice <= 0;
            if (!long.TryParse(priceStr, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var price)
                || price < _cfg.MinPrice || (!unlimited && price > _cfg.MaxPrice))
                return Result.Fail(unlimited
                    ? $"Price must be at least {_cfg.MinPrice:N0} {_cfg.Currency}."
                    : $"Price must be between {_cfg.MinPrice:N0} and {_cfg.MaxPrice:N0} {_cfg.Currency}.");

            lock (_lock)
            {
                var notReady = NotReady();
                if (notReady != null) return notReady;

                if (_cfg.CooldownMinutes > 0)
                {
                    var last = _requests.Where(r => r.RequesterId == playerId).Select(r => (DateTime?)r.CreatedUtc).Max();
                    if (last.HasValue)
                    {
                        var wait = last.Value.AddMinutes(_cfg.CooldownMinutes) - DateTime.UtcNow;
                        if (wait > TimeSpan.Zero)
                        {
                            var left = wait.TotalMinutes >= 1
                                ? $"{Math.Ceiling(wait.TotalMinutes)} minute(s)"
                                : $"{Math.Ceiling(wait.TotalSeconds)} second(s)";
                            return Result.Fail($"Cooldown active: please wait {left} before posting another request.");
                        }
                    }
                }
                if (_requests.Count(r => r.RequesterId == playerId && r.IsActive) >= _cfg.MaxOpenPerPlayer)
                    return Result.Fail($"You already have {_cfg.MaxOpenPerPlayer} active requests.");
                if (Bank.Balance(playerId) < price)
                    return Result.Fail($"You need {price:N0} {_cfg.Currency} to post this request.");
                if (!Bank.Add(playerId, -price))
                    return Result.Fail("Could not withdraw the credits.");

                var now = DateTime.UtcNow;
                var deposit = Math.Ceiling(price * (double)_cfg.DepositPercent / 100.0);
                var me = MyServerId;
                var req = new Request
                {
                    Id = NextLocalId(me),
                    RequesterId = playerId,
                    RequesterName = playerName,
                    Text = text,
                    Hours = hours,
                    Price = price,
                    Deposit = deposit >= long.MaxValue ? long.MaxValue : (long)deposit,
                    OriginServerId = me,
                    OriginSectorName = _nexus.CurrentServerName,
                    Status = RequestStatus.Open,
                    HasLocation = position != null && _cfg.IncludeGps,
                    X = position != null ? position[0] : 0,
                    Y = position != null ? position[1] : 0,
                    Z = position != null ? position[2] : 0,
                    CreatedUtc = now,
                    OpenExpiresUtc = now.AddHours(_cfg.OpenExpiryHours)
                };
                _requests.Add(req);
                Commit(req);
                Announce(req, $"📦 New request #{req.Label}", Blue, null, true);
                return Result.Success($"Request #{req.Label} posted. {price:N0} {_cfg.Currency} is held in escrow until it is delivered, failed or cancelled.");
            }
        }

        public Result Accept(long playerId, string playerName, RequestKey key)
        {
            lock (_lock)
            {
                var notReady = NotReady();
                if (notReady != null) return notReady;
                var r = Find(key);
                if (r == null) return Result.Fail($"No request #{key}.");
                if (r.Status != RequestStatus.Open) return Result.Fail($"Request #{key} is no longer open.");
                if (DateTime.UtcNow >= r.OpenExpiresUtc) return Result.Fail($"Request #{key} has expired.");
                if (r.RequesterId == playerId) return Result.Fail("You can't accept your own request.");
                if (Bank.Balance(playerId) < r.Deposit)
                    return Result.Fail($"You need a deposit of {r.Deposit:N0} {_cfg.Currency} to accept this.");
                if (!Bank.Add(playerId, -r.Deposit))
                    return Result.Fail("Could not withdraw the deposit.");

                var now = DateTime.UtcNow;
                r.AccepterId = playerId;
                r.AccepterName = playerName;
                r.AccepterSectorName = _nexus.CurrentServerName;
                r.AcceptedOnServerId = MyServerId;
                r.Status = RequestStatus.Accepted;
                r.AcceptedUtc = now;
                r.DeadlineUtc = now.AddHours(r.Hours);
                Commit(r);
                Announce(r, $"🤝 Request #{r.Label} accepted", Yellow, null, true);
                return Result.Success($"You accepted request #{r.Label}. Deposit of {r.Deposit:N0} {_cfg.Currency} is held. Deadline: {r.Hours} h.");
            }
        }

        public Result Deliver(long playerId, RequestKey key)
        {
            lock (_lock)
            {
                var notReady = NotReady();
                if (notReady != null) return notReady;
                var r = Find(key);
                if (r == null) return Result.Fail($"No request #{key}.");
                if (r.RequesterId != playerId) return Result.Fail("Only the player who posted the request can confirm delivery.");
                if (r.Status != RequestStatus.Accepted) return Result.Fail($"Request #{key} isn't in progress.");
                if (r.DeadlineUtc.HasValue && DateTime.UtcNow >= r.DeadlineUtc.Value) return Result.Fail($"The deadline for request #{key} has passed.");

                Bank.Add(r.AccepterId, r.Price + r.Deposit);
                r.Status = RequestStatus.Delivered;
                Commit(r);
                Announce(r, $"✅ Request #{r.Label} delivered", Green, $"{r.AccepterName} was paid {r.Price:N0} {_cfg.Currency}.");
                return Result.Success($"Delivery confirmed. {r.AccepterName} was paid {r.Price:N0} {_cfg.Currency}.");
            }
        }

        public Result Fail(long playerId, RequestKey key)
        {
            lock (_lock)
            {
                var notReady = NotReady();
                if (notReady != null) return notReady;
                var r = Find(key);
                if (r == null) return Result.Fail($"No request #{key}.");
                if (r.RequesterId != playerId) return Result.Fail("Only the player who posted the request can mark it as failed.");
                if (r.Status != RequestStatus.Accepted) return Result.Fail($"Request #{key} isn't in progress.");
                if (r.DeadlineUtc.HasValue && DateTime.UtcNow >= r.DeadlineUtc.Value) return Result.Fail($"The deadline for request #{key} has passed, it will be failed automatically.");
                FailInternal(r, "Marked as failed by the requester.");
                return Result.Success($"Request #{r.Label} marked as failed. Your {r.Price:N0} {_cfg.Currency} was returned.");
            }
        }

        public Result Cancel(long playerId, RequestKey key)
        {
            lock (_lock)
            {
                var notReady = NotReady();
                if (notReady != null) return notReady;
                var r = Find(key);
                if (r == null) return Result.Fail($"No request #{key}.");
                if (r.RequesterId != playerId) return Result.Fail("That isn't your request.");
                if (r.Status != RequestStatus.Open) return Result.Fail("Only requests nobody has accepted yet can be cancelled.");
                if (DateTime.UtcNow >= r.OpenExpiresUtc) return Result.Fail($"Request #{key} has expired, it will be refunded automatically.");
                Bank.Add(r.RequesterId, r.Price);
                r.Status = RequestStatus.Cancelled;
                Commit(r);
                Announce(r, $"🚫 Request #{r.Label} cancelled", Grey);
                return Result.Success($"Request #{r.Label} cancelled and {r.Price:N0} {_cfg.Currency} refunded.");
            }
        }

        public Result AdminCancel(RequestKey key)
        {
            lock (_lock)
            {
                var notReady = NotReady();
                if (notReady != null) return notReady;
                var r = Find(key);
                if (r == null) return Result.Fail($"No request #{key}.");
                if (!r.IsActive) return Result.Fail($"Request #{key} is already closed.");
                Bank.Add(r.RequesterId, r.Price);
                if (r.Status == RequestStatus.Accepted) Bank.Add(r.AccepterId, r.Deposit);
                r.Status = RequestStatus.Cancelled;
                Commit(r);
                Announce(r, $"🚫 Request #{r.Label} cancelled by admin", Grey, "All parties were refunded.");
                return Result.Success($"Request #{r.Label} cancelled, everyone refunded.");
            }
        }

        public List<Request> GetOpen()
        {
            var now = DateTime.UtcNow;
            lock (_lock) return _requests.Where(r => r.Status == RequestStatus.Open && now < r.OpenExpiresUtc)
                .OrderBy(r => r.OriginServerId).ThenBy(r => r.Id).ToList();
        }

        public List<Request> GetActive()
        {
            lock (_lock) return _requests.Where(r => r.IsActive).OrderBy(r => r.OriginServerId).ThenBy(r => r.Id).ToList();
        }

        public static string TimeLeft(DateTime untilUtc)
        {
            var left = untilUtc - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return "0m";
            if (left.TotalDays >= 1) return $"{(int)left.TotalDays}d {left.Hours}h";
            if (left.TotalHours >= 1) return $"{(int)left.TotalHours}h {left.Minutes}m";
            return $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m";
        }

        private void Tick()
        {
            try
            {
                lock (_lock)
                {
                    if (!_started || NotReady() != null) return;
                    var me = MyServerId;
                    var now = DateTime.UtcNow;
                    foreach (var r in _requests.Where(x => x.IsActive && x.OriginServerId == me).ToList())
                    {
                        if (r.Status == RequestStatus.Open && now >= r.OpenExpiresUtc + TimerGrace)
                        {
                            Bank.Add(r.RequesterId, r.Price);
                            r.Status = RequestStatus.Expired;
                            Commit(r);
                            Announce(r, $"⌛ Request #{r.Label} expired", Grey, "Nobody accepted in time. The requester was refunded.");
                        }
                        else if (r.Status == RequestStatus.Accepted && r.DeadlineUtc.HasValue && now >= r.DeadlineUtc.Value + TimerGrace)
                        {
                            FailInternal(r, "Deadline passed without delivery.");
                        }
                    }

                    if (_nexus.Enabled && ++_tickCount % ResyncEveryTicks == 0)
                        _nexus.BroadcastState(_requests.Where(r => r.IsActive).Select(NexusRequestDto.From).ToList());
                }
            }
            catch (Exception e) { Log.Error(e, "RequestBoard tick failed"); }
        }

        private void FailInternal(Request r, string reason)
        {
            Bank.Add(r.RequesterId, r.Price);
            if (!_cfg.BurnDepositOnFail) Bank.Add(r.RequesterId, r.Deposit);
            r.Status = RequestStatus.Failed;
            Commit(r);
            var lost = _cfg.BurnDepositOnFail ? "deposit was burned" : "deposit went to the requester";
            Announce(r, $"❌ Request #{r.Label} failed", Red, $"{reason} {r.AccepterName} lost {r.Deposit:N0} {_cfg.Currency} ({lost}).");
        }

        private Request Find(RequestKey key) => _requests.FirstOrDefault(r => r.Key == key);

        private int NextLocalId(byte origin)
        {
            var used = _requests.Where(r => r.OriginServerId == origin).Select(r => r.Id).DefaultIfEmpty(0).Max();
            _nextId = Math.Max(_nextId, used + 1);
            return _nextId++;
        }

        private void Commit(Request r)
        {
            r.Version++;
            r.ChangedUtc = DateTime.UtcNow;
            r.ChangedByServerId = MyServerId;
            Save();
            if (_nexus.Enabled) _nexus.BroadcastState(new[] { NexusRequestDto.From(r) });
        }

        public static string Gps(Request r) => string.Format(CultureInfo.InvariantCulture,
            "GPS:Request {0}:{1:F2}:{2:F2}:{3:F2}:#FF75C9F1:", r.Label, r.X, r.Y, r.Z);

        private void Announce(Request r, string title, int color, string extra = null, bool showLocation = false)
        {
            var desc = r.Text + (string.IsNullOrEmpty(extra) ? "" : "\n\n" + extra);
            var nexusOn = _nexus.Enabled;
            var fields = new List<EmbedField>
            {
                new EmbedField("Requester", r.RequesterName),
                new EmbedField(nexusOn ? "Origin sector" : "Sector", r.OriginSectorName ?? _cfg.ServerName),
                new EmbedField("Price", $"{r.Price:N0} {_cfg.Currency}"),
            };
            if (!string.IsNullOrEmpty(r.AccepterName))
            {
                fields.Add(new EmbedField("Accepted by", r.AccepterName));
                if (nexusOn && !string.IsNullOrEmpty(r.AccepterSectorName))
                    fields.Add(new EmbedField("Accepter sector", r.AccepterSectorName));
                fields.Add(new EmbedField("Deposit", $"{r.Deposit:N0} {_cfg.Currency}"));
            }
            fields.Add(new EmbedField("Time limit", $"{r.Hours} h"));
            if (showLocation && r.HasLocation)
                fields.Add(new EmbedField("Location", "`" + Gps(r) + "`", false));
            _hook.Send(title, desc, color, fields.ToArray());
        }

        private void OnNexusConnected()
        {
            lock (_lock)
            {
                if (!_started || _connected || !_nexus.Enabled) return;
                _connected = true;
                var me = MyServerId;

                var legacy = _requests.Where(r => r.OriginServerId == 0).ToList();
                if (legacy.Count > 0)
                {
                    foreach (var r in legacy)
                    {
                        if (Find(new RequestKey(me, r.Id)) != null) r.Id = NextLocalId(me);
                        r.OriginServerId = me;
                        if (r.ChangedByServerId == 0) r.ChangedByServerId = me;
                        if (r.AccepterId != 0 && r.AcceptedOnServerId == 0) r.AcceptedOnServerId = me;
                    }
                    Log.Info($"RequestBoard: assigned {legacy.Count} request(s) created without Nexus to server {me}.");
                    Save();
                }

                var peers = _nexus.OnlinePeers();
                if (peers.Count == 0)
                {
                    FinishSync("no other servers online");
                    return;
                }
                _syncPending.Clear();
                foreach (var p in peers) _syncPending.Add(p);
                Log.Info($"RequestBoard: requesting the request board from server(s) {string.Join(", ", peers)}.");
                _nexus.SendSyncRequest();
                _syncTimer?.Dispose();
                _syncTimer = new Timer(_ => OnGameThread(() =>
                {
                    lock (_lock) if (_connected && !_synced) FinishSync($"timed out waiting for server(s) {string.Join(", ", _syncPending)}");
                }), null, SyncTimeout, Timeout.InfiniteTimeSpan);
            }
        }

        private void FinishSync(string reason)
        {
            _synced = true;
            _syncPending.Clear();
            _syncTimer?.Dispose();
            _syncTimer = null;
            Log.Info($"RequestBoard: sync complete ({reason}), {_requests.Count(r => r.IsActive)} active request(s).");
            _nexus.BroadcastState(_requests.Where(r => r.IsActive).Select(NexusRequestDto.From).ToList());
        }

        private void OnSyncRequested(byte from)
        {
            lock (_lock)
            {
                if (!_started) return;
                _nexus.SendSyncResponse(from, _requests.Select(NexusRequestDto.From).ToList());
            }
        }

        private void OnSyncResponse(byte from, List<NexusRequestDto> list, bool last)
        {
            lock (_lock)
            {
                if (!_started) return;
                MergeAll(list);
                if (!last || _synced) return;
                _syncPending.Remove(from);
                if (_syncPending.Count == 0) FinishSync("all servers answered");
            }
        }

        private void OnState(List<NexusRequestDto> list)
        {
            lock (_lock)
            {
                if (!_started) return;
                MergeAll(list);
            }
        }

        private void MergeAll(List<NexusRequestDto> list)
        {
            var changed = false;
            foreach (var dto in list)
            {
                try { changed |= Merge(dto.ToRequest()); }
                catch (Exception e) { Log.Error(e, $"RequestBoard: failed to merge request {dto.OriginServerId}/{dto.LocalId}"); }
            }
            if (changed) Save();
        }

        private bool Merge(Request incoming)
        {
            if (incoming.OriginServerId == 0 || incoming.Id <= 0) return false;
            var index = _requests.FindIndex(r => r.Key == incoming.Key);
            if (index < 0)
            {
                _requests.Add(incoming);
                return true;
            }

            var local = _requests[index];
            if (incoming.Version < local.Version) return false;
            if (incoming.Version == local.Version)
            {
                if (local.ChangedUtc == incoming.ChangedUtc && local.ChangedByServerId == incoming.ChangedByServerId) return false;
                var incomingWins = Wins(incoming, local);
                Log.Error($"RequestBoard: conflicting changes to request #{local.Label} at version {local.Version}: " +
                          $"local {Describe(local)} vs incoming {Describe(incoming)}. Keeping the {(incomingWins ? "incoming" : "local")} one.");
                if (!incomingWins) return false;
            }

            RefundIfOurAcceptLost(local, incoming);
            _requests[index] = incoming;
            return true;
        }

        private static bool Wins(Request a, Request b)
        {
            var aClosing = a.Status != RequestStatus.Accepted;
            var bClosing = b.Status != RequestStatus.Accepted;
            if (aClosing != bClosing) return aClosing;
            if (a.ChangedUtc != b.ChangedUtc) return a.ChangedUtc < b.ChangedUtc;
            return a.ChangedByServerId < b.ChangedByServerId;
        }

        private static string Describe(Request r) =>
            $"[{r.Status} by server {r.ChangedByServerId} at {r.ChangedUtc:O}" + (r.AccepterId != 0 ? $", accepter {r.AccepterName}" : "") + "]";

        private void RefundIfOurAcceptLost(Request local, Request incoming)
        {
            if (local.Status != RequestStatus.Accepted || local.AcceptedOnServerId != MyServerId) return;
            if (incoming.AccepterId == local.AccepterId && incoming.AcceptedOnServerId == local.AcceptedOnServerId && incoming.AcceptedUtc == local.AcceptedUtc) return;

            Bank.Add(local.AccepterId, local.Deposit);
            Log.Error($"RequestBoard: {local.AccepterName}'s accept of request #{local.Label} was overridden by another sector, refunded deposit of {local.Deposit:N0}.");
            Notify(local.AccepterId, $"Someone else took request #{local.Label} at the same moment on another sector. Your deposit of {local.Deposit:N0} {_cfg.Currency} was refunded.");
            _hook.Send($"⚠️ Request #{local.Label} accept overridden", local.Text, Orange,
                new EmbedField("Player", local.AccepterName),
                new EmbedField("Refunded", $"{local.Deposit:N0} {_cfg.Currency}"));
        }

        private static void Notify(long identityId, string message)
        {
            try { MyVisualScriptLogicProvider.SendChatMessage(message, "Request Board", identityId); }
            catch (Exception e) { Log.Warn(e, "RequestBoard: could not send chat message"); }
        }

        private void Save()
        {
            try
            {
                var json = JsonConvert.SerializeObject(new StoreFile { NextId = _nextId, Requests = _requests }, Formatting.Indented);
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(_file)) File.Replace(tmp, _file, null);
                else File.Move(tmp, _file);
            }
            catch (Exception e) { Log.Error(e, "Failed to save requests"); }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_file)) return;
                var data = JsonConvert.DeserializeObject<StoreFile>(File.ReadAllText(_file));
                if (data == null) return;
                _requests = data.Requests ?? new List<Request>();
                foreach (var r in _requests.Where(r => r.Version == 0))
                {
                    r.Version = 1;
                    r.ChangedUtc = r.AcceptedUtc ?? r.CreatedUtc;
                    r.ChangedByServerId = r.OriginServerId;
                    if (r.AccepterId != 0 && r.AcceptedOnServerId == 0) r.AcceptedOnServerId = r.OriginServerId;
                }
                _nextId = Math.Max(1, data.NextId);
            }
            catch (Exception e) { Log.Error(e, "Failed to load requests"); }
        }

        private class StoreFile
        {
            public int NextId { get; set; } = 1;
            public List<Request> Requests { get; set; }
        }
    }
}
