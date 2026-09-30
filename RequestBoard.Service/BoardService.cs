using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RequestBoard.Contracts;

namespace RequestBoard.Service;

public sealed class BoardService
{
    private const int Blue = 0x3498DB, Yellow = 0xF1C40F, Green = 0x2ECC71, Red = 0xE74C3C, Grey = 0x95A5A6;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ServerSilence = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PruneEvery = TimeSpan.FromHours(1);

    private readonly Database _db;
    private readonly SettingsStore _settings;
    private readonly DiscordNotifier _discord;
    private readonly ILogger<BoardService> _log;
    private readonly List<Action> _afterCommit = new();
    private readonly Dictionary<string, DateTime> _lastSeen = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastPrune = DateTime.UtcNow;

    public BoardService(Database db, SettingsStore settings, DiscordNotifier discord, ILogger<BoardService> log)
    {
        _db = db;
        _settings = settings;
        _discord = discord;
        _log = log;
    }

    private BoardSettings R => _settings.Current;

    public ApiResult Get(int id)
    {
        lock (_db.Sync)
        {
            using var tx = _db.Begin();
            var r = _db.Get(tx, id);
            return r == null ? Fail($"No request #{id}.") : new ApiResult { Ok = true, Request = r };
        }
    }

    public OpenListResult ListOpen()
    {
        lock (_db.Sync)
        {
            using var tx = _db.Begin();
            var list = _db.Query(tx, "WHERE status = $s AND open_expires_utc > $now ORDER BY id",
                ("$s", (int)RequestStatus.Open), ("$now", DateTime.UtcNow.Ticks));
            return new OpenListResult { Requests = list, Currency = R.Currency };
        }
    }

    public ApiResult Create(CreateCommand c) => Run(c.OperationId, c.ServerName, c.PlayerName, "post a request", tx =>
    {
        var rules = R;
        var text = c.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return Fail("Request text can't be empty.");
        if (text.Length > 300) return Fail("Request text is too long (max 300 characters).");
        if (double.IsNaN(c.Hours) || c.Hours <= 0 || c.Hours > rules.MaxHours)
            return Fail($"Time must be a number of hours between 0 and {rules.MaxHours}.");
        var unlimited = rules.MaxPrice <= 0;
        if (c.Price < rules.MinPrice || (!unlimited && c.Price > rules.MaxPrice))
            return Fail(unlimited
                ? $"Price must be at least {rules.MinPrice:N0} {rules.Currency}."
                : $"Price must be between {rules.MinPrice:N0} and {rules.MaxPrice:N0} {rules.Currency}.");

        var now = DateTime.UtcNow;
        if (rules.CooldownMinutes > 0)
        {
            var lastTicks = _db.Scalar(tx, "SELECT MAX(created_utc) FROM requests WHERE requester_id = $p", ("$p", c.PlayerId));
            if (lastTicks > 0)
            {
                var wait = new DateTime(lastTicks, DateTimeKind.Utc).AddMinutes(rules.CooldownMinutes) - now;
                if (wait > TimeSpan.Zero)
                {
                    var left = wait.TotalMinutes >= 1 ? $"{Math.Ceiling(wait.TotalMinutes)} minute(s)" : $"{Math.Ceiling(wait.TotalSeconds)} second(s)";
                    return Fail($"Cooldown active: please wait {left} before posting another request.");
                }
            }
        }
        var active = _db.Scalar(tx, "SELECT COUNT(*) FROM requests WHERE requester_id = $p AND status IN ($o, $a)",
            ("$p", c.PlayerId), ("$o", (int)RequestStatus.Open), ("$a", (int)RequestStatus.Accepted));
        if (active >= rules.MaxOpenPerPlayer) return Fail($"You already have {rules.MaxOpenPerPlayer} active requests.");

        var deposit = Math.Ceiling(c.Price * (double)rules.DepositPercent / 100.0);
        if (deposit < 0 || deposit >= long.MaxValue - c.Price) return Fail("Price is too high.");
        var fee = rules.PostingFeeFor(c.Price);
        if (fee >= long.MaxValue - c.Price) return Fail("Price is too high.");
        if (c.Fee != fee) return Fail($"The posting fee is now {fee:N0} {rules.Currency}, please try again.");
        var includeGps = c.HasLocation && rules.IncludeGps;
        var r = new RequestDto
        {
            RequesterId = c.PlayerId,
            RequesterName = c.PlayerName,
            RequesterServer = c.ServerName,
            Text = text,
            Hours = c.Hours,
            Price = c.Price,
            Deposit = (long)deposit,
            Status = RequestStatus.Open,
            HasLocation = includeGps,
            X = includeGps ? c.X : 0,
            Y = includeGps ? c.Y : 0,
            Z = includeGps ? c.Z : 0,
            CreatedUtc = now,
            OpenExpiresUtc = now.AddHours(rules.OpenExpiryHours)
        };
        _db.Insert(tx, r);
        Event(r, $"📦 New request #{r.Id}", Blue, null, true,
            $"#{r.Id} posted by {r.RequesterName} on {c.ServerName}: {r.Price:N0} {rules.Currency}, {r.Hours} h, deposit {r.Deposit:N0}, fee {fee:N0} - \"{r.Text}\"");
        var feeText = fee > 0 ? $" A posting fee of {fee:N0} {rules.Currency} was charged." : "";
        return Ok($"Request #{r.Id} posted. {r.Price:N0} {rules.Currency} is held in escrow until it is delivered, failed or cancelled.{feeText}", r);
    });

    public ApiResult Accept(int id, AcceptCommand c) => Run(c.OperationId, c.ServerName, c.PlayerName, $"accept #{id}", tx =>
    {
        var r = _db.Get(tx, id);
        if (r == null) return Fail($"No request #{id}.");
        if (r.Status != RequestStatus.Open) return Fail($"Request #{id} is no longer open.");
        if (DateTime.UtcNow >= r.OpenExpiresUtc) return Fail($"Request #{id} has expired.");
        if (r.RequesterId == c.PlayerId) return Fail("You can't accept your own request.");
        if (r.Deposit != c.Deposit) return Fail($"The deposit for request #{id} is {r.Deposit:N0} {R.Currency}, please try again.");

        var now = DateTime.UtcNow;
        r.AccepterId = c.PlayerId;
        r.AccepterName = c.PlayerName;
        r.AccepterServer = c.ServerName;
        r.Status = RequestStatus.Accepted;
        r.AcceptedUtc = now;
        r.DeadlineUtc = now.AddHours(r.Hours);
        _db.Update(tx, r);
        Event(r, $"🤝 Request #{r.Id} accepted", Yellow, null, true,
            $"#{r.Id} accepted by {r.AccepterName} on {c.ServerName}, deposit {r.Deposit:N0} held, deadline {r.DeadlineUtc:u}");
        return Ok($"You accepted request #{r.Id}. Deposit of {r.Deposit:N0} {R.Currency} is held. Deadline: {r.Hours} h.", r);
    });

    public ApiResult Deliver(int id, PlayerCommand c) => Run(c.OperationId, c.ServerName, $"player {c.PlayerId}", $"deliver #{id}", tx =>
    {
        var r = _db.Get(tx, id);
        if (r == null) return Fail($"No request #{id}.");
        if (r.RequesterId != c.PlayerId) return Fail("Only the player who posted the request can confirm delivery.");
        if (r.Status != RequestStatus.Accepted) return Fail($"Request #{id} isn't in progress.");
        if (DateTime.UtcNow >= r.DeadlineUtc) return Fail($"The deadline for request #{id} has passed.");

        Close(tx, r, RequestStatus.Delivered);
        Event(r, $"✅ Request #{r.Id} delivered", Green, $"{r.AccepterName} was paid {r.Price:N0} {R.Currency}.", false,
            $"#{r.Id} delivered (confirmed on {c.ServerName}): paying {r.AccepterName} {r.Price + r.Deposit:N0}");
        return Ok($"Delivery confirmed. {r.AccepterName} was paid {r.Price:N0} {R.Currency}.", r,
            new Payout { RequestId = r.Id, IdentityId = r.AccepterId, Amount = r.Price + r.Deposit, Reason = $"Request #{r.Id} delivered: you were paid {r.Price:N0} {R.Currency} and your deposit of {r.Deposit:N0} was returned." });
    });

    public ApiResult Fail(int id, PlayerCommand c) => Run(c.OperationId, c.ServerName, $"player {c.PlayerId}", $"fail #{id}", tx =>
    {
        var r = _db.Get(tx, id);
        if (r == null) return Fail($"No request #{id}.");
        if (r.RequesterId != c.PlayerId) return Fail("Only the player who posted the request can mark it as failed.");
        if (r.Status != RequestStatus.Accepted) return Fail($"Request #{id} isn't in progress.");
        if (DateTime.UtcNow >= r.DeadlineUtc) return Fail($"The deadline for request #{id} has passed, it will be failed automatically.");

        var payout = FailCore(tx, r, "Marked as failed by the requester.");
        return Ok($"Request #{r.Id} marked as failed. Your {r.Price:N0} {R.Currency} was returned.", r, payout);
    });

    public ApiResult Cancel(int id, PlayerCommand c) => Run(c.OperationId, c.ServerName, $"player {c.PlayerId}", $"cancel #{id}", tx =>
    {
        var r = _db.Get(tx, id);
        if (r == null) return Fail($"No request #{id}.");
        if (r.RequesterId != c.PlayerId) return Fail("That isn't your request.");
        if (r.Status != RequestStatus.Open) return Fail("Only requests nobody has accepted yet can be cancelled.");
        if (DateTime.UtcNow >= r.OpenExpiresUtc) return Fail($"Request #{id} has expired, it will be refunded automatically.");

        Close(tx, r, RequestStatus.Cancelled);
        Event(r, $"🚫 Request #{r.Id} cancelled", Grey, null, false, $"#{r.Id} cancelled by {r.RequesterName} on {c.ServerName}: refunding {r.Price:N0}");
        return Ok($"Request #{r.Id} cancelled and {r.Price:N0} {R.Currency} refunded.", r,
            new Payout { RequestId = r.Id, IdentityId = r.RequesterId, Amount = r.Price, Reason = $"Request #{r.Id} cancelled, {r.Price:N0} {R.Currency} refunded." });
    });

    public ApiResult AdminCancel(int id, AdminCommand c) => Run(c.OperationId, c.ServerName, "admin", $"admin-cancel #{id}", tx =>
    {
        var r = _db.Get(tx, id);
        if (r == null) return Fail($"No request #{id}.");
        if (r.Status != RequestStatus.Open && r.Status != RequestStatus.Accepted) return Fail($"Request #{id} is already closed.");

        var payouts = new List<Payout> { new() { RequestId = r.Id, IdentityId = r.RequesterId, Amount = r.Price, Reason = $"Request #{r.Id} was cancelled by an admin, {r.Price:N0} {R.Currency} refunded." } };
        if (r.Status == RequestStatus.Accepted)
            payouts.Add(new Payout { RequestId = r.Id, IdentityId = r.AccepterId, Amount = r.Deposit, Reason = $"Request #{r.Id} was cancelled by an admin, your deposit of {r.Deposit:N0} {R.Currency} was refunded." });
        Close(tx, r, RequestStatus.Cancelled);
        Event(r, $"🚫 Request #{r.Id} cancelled by admin", Grey, "All parties were refunded.", false, $"#{r.Id} cancelled by an admin on {c.ServerName}: refunding everyone");
        return Ok($"Request #{r.Id} cancelled, everyone refunded.", r, payouts.ToArray());
    });

    public SyncResult Sync(SyncCommand c)
    {
        var server = string.IsNullOrWhiteSpace(c.ServerName) ? "unknown server" : c.ServerName;
        lock (_db.Sync)
        {
            var now = DateTime.UtcNow;
            if (!_lastSeen.TryGetValue(server, out var seen) || now - seen > ServerSilence)
                _log.LogInformation("Server '{Server}' is syncing", server);
            _lastSeen[server] = now;

            if (now - _lastPrune > PruneEvery)
            {
                _db.PruneOperations();
                _lastPrune = now;
            }

            var result = new SyncResult { Currency = R.Currency };
            using var tx = _db.Begin();
            try
            {
                var hasOp = !string.IsNullOrWhiteSpace(c.OperationId);
                if (hasOp)
                {
                    var stored = _db.FindOperation(tx, c.OperationId);
                    if (stored != null)
                    {
                        _log.LogInformation("[{Server}] repeated sync {Op}, returning the original payouts", server, c.OperationId);
                        return JsonSerializer.Deserialize<SyncResult>(stored, Json);
                    }
                }
                var due = _db.Query(tx, "WHERE (status = $o AND open_expires_utc <= $now) OR (status = $a AND deadline_utc <= $now) ORDER BY id",
                    ("$o", (int)RequestStatus.Open), ("$a", (int)RequestStatus.Accepted), ("$now", now.Ticks));
                foreach (var r in due)
                {
                    if (r.Status == RequestStatus.Open)
                    {
                        Close(tx, r, RequestStatus.Expired);
                        Event(r, $"⌛ Request #{r.Id} expired", Grey, "Nobody accepted in time. The requester was refunded.", false,
                            $"#{r.Id} expired: {server} refunds {r.RequesterName} {r.Price:N0}");
                        result.Payouts.Add(new Payout { RequestId = r.Id, IdentityId = r.RequesterId, Amount = r.Price, Reason = $"Nobody accepted request #{r.Id} in time, {r.Price:N0} {R.Currency} refunded." });
                    }
                    else
                    {
                        result.Payouts.Add(FailCore(tx, r, "Deadline passed without delivery.", server));
                    }
                }
                result.Active = _db.Query(tx, "WHERE status IN ($o, $a) ORDER BY id", ("$o", (int)RequestStatus.Open), ("$a", (int)RequestStatus.Accepted));
                result.Payouts.RemoveAll(p => p.Amount <= 0);
                if (hasOp && result.Payouts.Count > 0)
                    _db.SaveOperation(tx, c.OperationId, JsonSerializer.Serialize(result, Json));
                tx.Commit();
                FlushAfterCommit();
            }
            catch
            {
                _afterCommit.Clear();
                throw;
            }
            return result;
        }
    }

    private Payout FailCore(SqliteTransaction tx, RequestDto r, string reason, string payingServer = null)
    {
        var rules = R;
        var refund = r.Price + (rules.BurnDepositOnFail ? 0 : r.Deposit);
        Close(tx, r, RequestStatus.Failed);
        var lost = rules.BurnDepositOnFail ? "deposit was burned" : "deposit went to the requester";
        Event(r, $"❌ Request #{r.Id} failed", Red, $"{reason} {r.AccepterName} lost {r.Deposit:N0} {rules.Currency} ({lost}).", false,
            $"#{r.Id} failed ({reason}): {(payingServer != null ? payingServer + " " : "")}refunds {r.RequesterName} {refund:N0}, {r.AccepterName}'s {lost}");
        return new Payout { RequestId = r.Id, IdentityId = r.RequesterId, Amount = refund, Reason = $"Request #{r.Id} failed ({reason}) {refund:N0} {rules.Currency} returned to you." };
    }

    private void Close(SqliteTransaction tx, RequestDto r, RequestStatus status)
    {
        r.Status = status;
        r.ClosedUtc = DateTime.UtcNow;
        _db.Update(tx, r);
    }

    private ApiResult Run(string opId, string server, string player, string action, Func<SqliteTransaction, ApiResult> body)
    {
        if (string.IsNullOrWhiteSpace(opId)) return Fail("Missing operation id.");
        lock (_db.Sync)
        {
            using var tx = _db.Begin();
            try
            {
                var stored = _db.FindOperation(tx, opId);
                if (stored != null)
                {
                    _log.LogInformation("[{Server}] repeated operation {Op} ({Action}), returning the original answer", server, opId, action);
                    return JsonSerializer.Deserialize<ApiResult>(stored, Json);
                }
                var result = body(tx);
                _db.SaveOperation(tx, opId, JsonSerializer.Serialize(result, Json));
                tx.Commit();
                if (result.Ok) FlushAfterCommit();
                else _log.LogInformation("[{Server}] {Player} tried to {Action}: rejected - {Message}", server, player, action, result.Message);
                _afterCommit.Clear();
                return result;
            }
            catch
            {
                _afterCommit.Clear();
                throw;
            }
        }
    }

    private void FlushAfterCommit()
    {
        foreach (var a in _afterCommit) a();
        _afterCommit.Clear();
    }

    private void Event(RequestDto r, string title, int color, string extra, bool showLocation, string consoleLine)
    {
        var rules = R;
        var fields = new List<EmbedField>
        {
            new("Requester", r.RequesterName),
            new("Sector", r.RequesterServer),
            new("Price", $"{r.Price:N0} {rules.Currency}")
        };
        if (!string.IsNullOrEmpty(r.AccepterName))
        {
            fields.Add(new EmbedField("Accepted by", r.AccepterName));
            if (!string.IsNullOrEmpty(r.AccepterServer)) fields.Add(new EmbedField("Accepter sector", r.AccepterServer));
            fields.Add(new EmbedField("Deposit", $"{r.Deposit:N0} {rules.Currency}"));
        }
        fields.Add(new EmbedField("Time limit", $"{r.Hours} h"));
        if (showLocation && r.HasLocation) fields.Add(new EmbedField("Location", "`" + Gps(r) + "`", false));
        var description = r.Text + (string.IsNullOrEmpty(extra) ? "" : "\n\n" + extra);
        var fieldArray = fields.ToArray();

        _afterCommit.Add(() =>
        {
            _log.LogInformation("{Line}", consoleLine);
            _discord.Send(title, description, color, fieldArray);
        });
    }

    public static string Gps(RequestDto r) => string.Format(CultureInfo.InvariantCulture,
        "GPS:Request {0}:{1:F2}:{2:F2}:{3:F2}:#FF75C9F1:", r.Id, r.X, r.Y, r.Z);

    private static ApiResult Fail(string message) => new() { Ok = false, Message = message };

    private static ApiResult Ok(string message, RequestDto r, params Payout[] payouts) =>
        new() { Ok = true, Message = message, Request = r, Payouts = payouts.Where(p => p.Amount > 0).ToList() };
}
