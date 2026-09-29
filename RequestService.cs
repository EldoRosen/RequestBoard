using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using NLog;
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

    /// <summary>
    /// All request rules and money movement live here.
    /// Escrow model:
    ///   request  -> requester's price is taken and held
    ///   accept   -> accepter's deposit is taken and held
    ///   deliver  -> accepter gets price + deposit back
    ///   fail     -> requester gets price back (+ deposit unless configured to burn it)
    ///
    /// When Nexus V3 is enabled, a request is always owned by the server it was
    /// created on ("home" server) - only that server ever moves credits for it.
    /// Other servers only keep a read-only mirror of its status (for "!requests")
    /// and relay player actions on it back to the home server.
    /// </summary>
    public class RequestService
    {
        private const int Blue = 0x3498DB, Yellow = 0xF1C40F, Green = 0x2ECC71, Red = 0xE74C3C, Grey = 0x95A5A6;
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

        // Read-only mirror of other servers' requests, for "!requests" only. Keyed by (origin server, origin local id).
        private readonly Dictionary<(byte, int), NexusRequestDto> _remote = new Dictionary<(byte, int), NexusRequestDto>();

        public RequestService(ITorchBase torch, RequestBoardConfig cfg, DiscordWebhook hook, NexusBridge nexus, string file)
        {
            _torch = torch; _cfg = cfg; _hook = hook; _nexus = nexus; _file = file;
            if (_nexus != null)
            {
                _nexus.RequestEventReceived += OnRemoteRequestEvent;
                _nexus.RelayActionReceived += OnRelayAction;
            }
        }

        /// <summary>Call only once the game session is loaded (the game thread must exist).</summary>
        public void Start()
        {
            if (_timer != null) return;
            Load();
            // Timer thread -> hop onto the game thread before touching balances.
            _timer = new Timer(_ =>
            {
                try { _torch.Invoke(Tick); }
                catch (Exception e) { Log.Warn(e, "RequestBoard could not schedule tick"); }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
        }

        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
            lock (_lock) Save();
        }

        // ---------------------------------------------------------------- player actions

        public Result Create(long playerId, string playerName, string text, string hoursStr, string priceStr, double[] position = null)
        {
            if (!_cfg.Enabled) return Result.Fail("Request Board is currently disabled.");
            if (string.IsNullOrWhiteSpace(text)) return Result.Fail("Request text can't be empty.");
            text = text.Trim();
            if (text.Length > 300) return Result.Fail("Request text is too long (max 300 characters).");

            if (!double.TryParse(hoursStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) || hours <= 0 || hours > _cfg.MaxHours)
                return Result.Fail($"Time must be a number of hours between 0 and {_cfg.MaxHours}.");

            var unlimited = _cfg.MaxPrice <= 0; // MaxPrice = 0 means no upper limit
            if (!long.TryParse(priceStr, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var price)
                || price < _cfg.MinPrice || (!unlimited && price > _cfg.MaxPrice))
                return Result.Fail(unlimited
                    ? $"Price must be at least {_cfg.MinPrice:N0} {_cfg.Currency}."
                    : $"Price must be between {_cfg.MinPrice:N0} and {_cfg.MaxPrice:N0} {_cfg.Currency}.");

            lock (_lock)
            {
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
                var originName = _nexus != null && _nexus.Enabled ? _nexus.CurrentServerName : _cfg.ServerName;
                var req = new Request
                {
                    Id = _nextId++,
                    RequesterId = playerId,
                    RequesterName = playerName,
                    Text = text,
                    Hours = hours,
                    Price = price,
                    Deposit = deposit >= long.MaxValue ? long.MaxValue : (long)deposit,
                    OriginServerId = _nexus?.CurrentServerId ?? 0,
                    OriginSectorName = originName,
                    Status = RequestStatus.Open,
                    HasLocation = position != null && _cfg.IncludeGps,
                    X = position != null ? position[0] : 0,
                    Y = position != null ? position[1] : 0,
                    Z = position != null ? position[2] : 0,
                    CreatedUtc = now,
                    OpenExpiresUtc = now.AddHours(_cfg.OpenExpiryHours)
                };
                _requests.Add(req);
                Save();
                Announce(req, $"📦 New request #{req.Id}", Blue, null, true);
                return Result.Success($"Request #{req.Id} posted. {price:N0} {_cfg.Currency} is held in escrow until it is delivered, failed or cancelled.");
            }
        }

        public Result Accept(long playerId, string playerName, int id, string accepterSectorName = null)
        {
            if (!_cfg.Enabled) return Result.Fail("Request Board is currently disabled.");
            lock (_lock)
            {
                var r = Find(id);
                if (r == null) return Result.Fail($"No request #{id}.");
                if (r.Status != RequestStatus.Open) return Result.Fail($"Request #{id} is no longer open.");
                if (r.RequesterId == playerId) return Result.Fail("You can't accept your own request.");
                if (Bank.Balance(playerId) < r.Deposit)
                    return Result.Fail($"You need a deposit of {r.Deposit:N0} {_cfg.Currency} to accept this.");
                if (!Bank.Add(playerId, -r.Deposit))
                    return Result.Fail("Could not withdraw the deposit.");

                r.AccepterId = playerId;
                r.AccepterName = playerName;
                r.AccepterSectorName = accepterSectorName ?? (_nexus != null && _nexus.Enabled ? _nexus.CurrentServerName : _cfg.ServerName);
                r.Status = RequestStatus.Accepted;
                r.AcceptedUtc = DateTime.UtcNow;
                r.DeadlineUtc = r.AcceptedUtc.Value.AddHours(r.Hours);
                Save();
                Announce(r, $"🤝 Request #{r.Id} accepted", Yellow, null, true);
                return Result.Success($"You accepted request #{id}. Deposit of {r.Deposit:N0} {_cfg.Currency} is held. Deadline: {r.Hours} h.");
            }
        }

        public Result Deliver(long playerId, int id)
        {
            lock (_lock)
            {
                var r = Find(id);
                if (r == null) return Result.Fail($"No request #{id}.");
                if (r.RequesterId != playerId) return Result.Fail("Only the player who posted the request can confirm delivery.");
                if (r.Status != RequestStatus.Accepted) return Result.Fail($"Request #{id} isn't in progress.");

                Bank.Add(r.AccepterId, r.Price + r.Deposit);
                r.Status = RequestStatus.Delivered;
                Save();
                Announce(r, $"✅ Request #{r.Id} delivered", Green, $"{r.AccepterName} was paid {r.Price:N0} {_cfg.Currency}.");
                return Result.Success($"Delivery confirmed. {r.AccepterName} was paid {r.Price:N0} {_cfg.Currency}.");
            }
        }

        public Result Fail(long playerId, int id)
        {
            lock (_lock)
            {
                var r = Find(id);
                if (r == null) return Result.Fail($"No request #{id}.");
                if (r.RequesterId != playerId) return Result.Fail("Only the player who posted the request can mark it as failed.");
                if (r.Status != RequestStatus.Accepted) return Result.Fail($"Request #{id} isn't in progress.");
                FailInternal(r, "Marked as failed by the requester.");
                return Result.Success($"Request #{id} marked as failed. Your {r.Price:N0} {_cfg.Currency} was returned.");
            }
        }

        public Result Cancel(long playerId, int id)
        {
            lock (_lock)
            {
                var r = Find(id);
                if (r == null) return Result.Fail($"No request #{id}.");
                if (r.RequesterId != playerId) return Result.Fail("That isn't your request.");
                if (r.Status != RequestStatus.Open) return Result.Fail("Only requests nobody has accepted yet can be cancelled.");
                Bank.Add(r.RequesterId, r.Price);
                r.Status = RequestStatus.Cancelled;
                Save();
                Announce(r, $"🚫 Request #{r.Id} cancelled", Grey);
                return Result.Success($"Request #{id} cancelled and {r.Price:N0} {_cfg.Currency} refunded.");
            }
        }

        /// <summary>Admin override: refunds everyone involved and closes the request.</summary>
        public Result AdminCancel(int id)
        {
            lock (_lock)
            {
                var r = Find(id);
                if (r == null) return Result.Fail($"No request #{id}.");
                if (!r.IsActive) return Result.Fail($"Request #{id} is already closed.");
                Bank.Add(r.RequesterId, r.Price);
                if (r.Status == RequestStatus.Accepted) Bank.Add(r.AccepterId, r.Deposit);
                r.Status = RequestStatus.Cancelled;
                Save();
                Announce(r, $"🚫 Request #{r.Id} cancelled by admin", Grey, "All parties were refunded.");
                return Result.Success($"Request #{id} cancelled, everyone refunded.");
            }
        }

        public List<Request> GetOpen()
        {
            lock (_lock) return _requests.Where(r => r.Status == RequestStatus.Open).OrderBy(r => r.Id).ToList();
        }

        public List<Request> GetActive()
        {
            lock (_lock) return _requests.Where(r => r.IsActive).OrderBy(r => r.Id).ToList();
        }

        // ---------------------------------------------------------------- internals

        private void Tick()
        {
            try
            {
                lock (_lock)
                {
                    var now = DateTime.UtcNow;
                    foreach (var r in _requests.Where(x => x.IsActive).ToList())
                    {
                        if (r.Status == RequestStatus.Open && now >= r.OpenExpiresUtc)
                        {
                            Bank.Add(r.RequesterId, r.Price);
                            r.Status = RequestStatus.Expired;
                            Save();
                            Announce(r, $"⌛ Request #{r.Id} expired", Grey, "Nobody accepted in time. The requester was refunded.");
                        }
                        else if (r.Status == RequestStatus.Accepted && r.DeadlineUtc.HasValue && now >= r.DeadlineUtc.Value)
                        {
                            FailInternal(r, "Deadline passed without delivery.");
                        }
                    }
                }
            }
            catch (Exception e) { Log.Error(e, "RequestBoard tick failed"); }
        }

        // Caller must hold _lock.
        private void FailInternal(Request r, string reason)
        {
            Bank.Add(r.RequesterId, r.Price);
            if (!_cfg.BurnDepositOnFail) Bank.Add(r.RequesterId, r.Deposit);
            r.Status = RequestStatus.Failed;
            Save();
            var lost = _cfg.BurnDepositOnFail ? "deposit was burned" : "deposit went to the requester";
            Announce(r, $"❌ Request #{r.Id} failed", Red, $"{reason} {r.AccepterName} lost {r.Deposit:N0} {_cfg.Currency} ({lost}).");
        }

        private Request Find(int id) => _requests.FirstOrDefault(r => r.Id == id);

        /// <summary>GPS string players can paste into the in-game GPS menu (also clickable in chat).</summary>
        public static string Gps(Request r) => string.Format(CultureInfo.InvariantCulture,
            "GPS:Request {0}:{1:F2}:{2:F2}:{3:F2}:#FF75C9F1:", r.Id, r.X, r.Y, r.Z);

        private void Announce(Request r, string title, int color, string extra = null, bool showLocation = false)
        {
            var desc = r.Text + (string.IsNullOrEmpty(extra) ? "" : "\n\n" + extra);
            var nexusOn = _nexus != null && _nexus.Enabled;
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

            if (nexusOn)
                _nexus.BroadcastRequest(ToDto(r));
        }

        private static NexusRequestDto ToDto(Request r) => new NexusRequestDto
        {
            OriginServerId = r.OriginServerId,
            OriginLocalId = r.Id,
            OriginSectorName = r.OriginSectorName,
            RequesterName = r.RequesterName,
            AccepterName = r.AccepterName,
            AccepterSectorName = r.AccepterSectorName,
            Text = r.Text,
            Hours = r.Hours,
            Price = r.Price,
            Deposit = r.Deposit,
            Status = r.Status.ToString(),
            HasLocation = r.HasLocation,
            X = r.X,
            Y = r.Y,
            Z = r.Z
        };

        // ---------------------------------------------------------------- Nexus cross-server

        /// <summary>A request on another server changed state - update our read-only mirror of it.</summary>
        private void OnRemoteRequestEvent(NexusRequestDto dto)
        {
            lock (_lock)
            {
                var key = (dto.OriginServerId, dto.OriginLocalId);
                if (dto.Status == RequestStatus.Open.ToString() || dto.Status == RequestStatus.Accepted.ToString())
                    _remote[key] = dto;
                else
                    _remote.Remove(key); // delivered/failed/expired/cancelled - no longer actionable
            }
        }

        /// <summary>
        /// A player on another server ran an action against a request that we are the home server for.
        /// Executes the same logic as the local command and lets Announce() report/broadcast the result.
        /// </summary>
        private void OnRelayAction(NexusRelayActionDto action)
        {
            try
            {
                switch (action.ActionName)
                {
                    case "accept": Accept(action.PlayerId, action.PlayerName, action.RequestLocalId, action.PlayerSectorName); break;
                    case "deliver": Deliver(action.PlayerId, action.RequestLocalId); break;
                    case "fail": Fail(action.PlayerId, action.RequestLocalId); break;
                    case "cancel": Cancel(action.PlayerId, action.RequestLocalId); break;
                }
            }
            catch (Exception e) { Log.Error(e, "RequestBoard: failed to process relayed Nexus action"); }
        }

        /// <summary>Open requests mirrored from other Nexus servers, for display in "!requests".</summary>
        public List<NexusRequestDto> GetRemoteOpen()
        {
            lock (_lock) return _remote.Values.Where(d => d.Status == RequestStatus.Open.ToString()).ToList();
        }

        /// <summary>Finds which remote server (if any) currently reports owning request number id. Null if none/ambiguous.</summary>
        public byte? FindRemoteOwner(int id)
        {
            lock (_lock)
            {
                var matches = _remote.Keys.Where(k => k.Item2 == id).Select(k => k.Item1).Distinct().ToList();
                return matches.Count == 1 ? matches[0] : (byte?)null;
            }
        }

        // Caller must hold _lock.
        private void Save()
        {
            try
            {
                var json = JsonConvert.SerializeObject(new StoreFile { NextId = _nextId, Requests = _requests }, Formatting.Indented);
                File.WriteAllText(_file, json);
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
                _nextId = Math.Max(data.NextId, _requests.Any() ? _requests.Max(r => r.Id) + 1 : 1);
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
