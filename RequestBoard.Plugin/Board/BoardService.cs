using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.Linq;
using NLog;

namespace RequestBoard.Board
{
    public sealed class BoardService
    {
        private const int Blue = 0x3498DB, Yellow = 0xF1C40F, Green = 0x2ECC71, Red = 0xE74C3C, Grey = 0x95A5A6;
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly Database _db;
        private readonly DiscordNotifier _discord;
        private readonly RequestBoardConfig _cfg;
        private readonly List<Action> _afterCommit = new List<Action>();

        public BoardService(Database db, DiscordNotifier discord, RequestBoardConfig cfg)
        {
            _db = db;
            _discord = discord;
            _cfg = cfg;
        }

        private string Server => string.IsNullOrWhiteSpace(_cfg.ServerName) ? "unknown server" : _cfg.ServerName.Trim();

        public string Open()
        {
            lock (_db.Sync)
            {
                var path = _db.EnsureOpen();
                using (var tx = _db.BeginRead())
                {
                    var rules = _db.LoadSettings(tx);
                    Log.Info($"RequestBoard: rules: {Describe(rules)}, Discord webhook {WebhookState(rules)}");
                }
                return path;
            }
        }

        public void Close()
        {
            lock (_db.Sync) _db.Close();
        }

        public BoardResult Get(int id)
        {
            lock (_db.Sync)
            {
                using (var tx = _db.BeginRead())
                {
                    var r = _db.Get(tx, id);
                    return r == null ? Fail($"No request #{id}.") : new BoardResult { Ok = true, Request = r };
                }
            }
        }

        public OpenListResult ListOpen()
        {
            lock (_db.Sync)
            {
                using (var tx = _db.BeginRead())
                {
                    var list = _db.Query(tx, "WHERE status = @s AND expires_utc > @now ORDER BY id",
                        ("@s", (int)RequestStatus.Open), ("@now", DateTime.UtcNow.Ticks));
                    return new OpenListResult { Requests = list, Currency = _db.LoadSettings(tx).Currency };
                }
            }
        }

        public BoardSettings GetSettings()
        {
            lock (_db.Sync)
            {
                using (var tx = _db.BeginRead())
                    return _db.LoadSettings(tx);
            }
        }

        public SettingsResult SaveSettings(BoardSettings s)
        {
            if (s == null) return new SettingsResult { Ok = false, Message = "No settings were given." };
            s = s.Clone();
            s.Currency = s.Currency?.Trim();
            s.DiscordWebhookUrl = s.DiscordWebhookUrl?.Trim() ?? "";

            var error = Validate(s);
            lock (_db.Sync)
            {
                using (var tx = _db.BeginWrite())
                {
                    if (error != null)
                    {
                        Log.Info($"RequestBoard: settings change rejected - {error}");
                        return new SettingsResult { Ok = false, Message = error, Settings = _db.LoadSettings(tx) };
                    }
                    _db.SaveSettings(tx, s);
                    tx.Commit();
                }
            }
            Log.Info($"RequestBoard: [{Server}] settings updated: {Describe(s)}, Discord webhook {WebhookState(s)}");
            return new SettingsResult { Ok = true, Message = "Rules saved to the database. They apply to new requests on every server using it.", Settings = s.Clone() };
        }

        public BoardResult Create(long playerId, string playerName, string text, double hours, long price, long fee, double[] position) =>
            Run(playerName, "post a request", (tx, rules) =>
            {
                text = text?.Trim();
                if (string.IsNullOrEmpty(text)) return Fail("Request text can't be empty.");
                if (text.Length > 300) return Fail("Request text is too long (max 300 characters).");
                if (double.IsNaN(hours) || hours < rules.MinHours || hours > rules.MaxHours)
                    return Fail($"Time must be a number of hours between {rules.MinHours} and {rules.MaxHours}.");
                var unlimited = rules.MaxPrice <= 0;
                if (price < rules.MinPrice || (!unlimited && price > rules.MaxPrice))
                    return Fail(unlimited
                        ? $"Price must be at least {rules.MinPrice:N0} {rules.Currency}."
                        : $"Price must be between {rules.MinPrice:N0} and {rules.MaxPrice:N0} {rules.Currency}.");

                var now = DateTime.UtcNow;
                if (rules.CooldownMinutes > 0)
                {
                    var lastTicks = _db.Scalar(tx, "SELECT MAX(created_utc) FROM requests WHERE requester_id = @p", ("@p", playerId));
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
                var active = _db.Scalar(tx, "SELECT COUNT(*) FROM requests WHERE requester_id = @p AND status IN (@o, @a)",
                    ("@p", playerId), ("@o", (int)RequestStatus.Open), ("@a", (int)RequestStatus.Accepted));
                if (active >= rules.MaxOpenPerPlayer) return Fail($"You already have {rules.MaxOpenPerPlayer} active requests.");

                var deposit = Math.Ceiling(price * (double)rules.DepositPercent / 100.0);
                if (deposit < 0 || deposit >= long.MaxValue - price) return Fail("Price is too high.");
                var currentFee = rules.PostingFeeFor(price);
                if (currentFee >= long.MaxValue - price) return Fail("Price is too high.");
                if (fee != currentFee) return Fail($"The posting fee is now {currentFee:N0} {rules.Currency}, please try again.");
                var includeGps = position != null && rules.IncludeGps;
                var r = new BoardRequest
                {
                    RequesterId = playerId,
                    RequesterName = playerName,
                    RequesterServer = Server,
                    Text = text,
                    Hours = hours,
                    Price = price,
                    Deposit = (long)deposit,
                    Status = RequestStatus.Open,
                    HasLocation = includeGps,
                    X = includeGps ? position[0] : 0,
                    Y = includeGps ? position[1] : 0,
                    Z = includeGps ? position[2] : 0,
                    CreatedUtc = now,
                    ExpiresUtc = now.AddHours(hours)
                };
                _db.Insert(tx, r);
                Event(rules, r, $"📦 New request #{r.Id}", Blue, null, true,
                    $"#{r.Id} posted by {r.RequesterName} on {Server}: {r.Price:N0} {rules.Currency}, {r.Hours} h, deposit {r.Deposit:N0}, fee {fee:N0} - \"{r.Text}\"");
                var feeText = fee > 0 ? $" A posting fee of {fee:N0} {rules.Currency} was charged." : "";
                return Ok($"Request #{r.Id} posted. {r.Price:N0} {rules.Currency} is held in escrow until it is delivered, failed or cancelled.{feeText}", r);
            });

        public BoardResult Accept(int id, long playerId, string playerName, long deposit) =>
            Run(playerName, $"accept #{id}", (tx, rules) =>
            {
                var r = _db.Get(tx, id);
                if (r == null) return Fail($"No request #{id}.");
                if (r.Status != RequestStatus.Open) return Fail($"Request #{id} is no longer open.");
                if (DateTime.UtcNow >= r.ExpiresUtc) return Fail($"Request #{id} has expired.");
                if (r.RequesterId == playerId) return Fail("You can't accept your own request.");
                if (r.Deposit != deposit) return Fail($"The deposit for request #{id} is {r.Deposit:N0} {rules.Currency}, please try again.");

                var now = DateTime.UtcNow;
                r.AccepterId = playerId;
                r.AccepterName = playerName;
                r.AccepterServer = Server;
                r.Status = RequestStatus.Accepted;
                r.AcceptedUtc = now;
                _db.Update(tx, r);
                Event(rules, r, $"🤝 Request #{r.Id} accepted", Yellow, null, true,
                    $"#{r.Id} accepted by {r.AccepterName} on {Server}, deposit {r.Deposit:N0} held, deadline {r.ExpiresUtc:u}");
                return Ok($"You accepted request #{r.Id}. Deposit of {r.Deposit:N0} {rules.Currency} is held. Time left: {RequestService.TimeLeft(r.ExpiresUtc)}.", r);
            });

        public BoardResult Deliver(int id, long playerId) =>
            Run($"player {playerId}", $"deliver #{id}", (tx, rules) =>
            {
                var r = _db.Get(tx, id);
                if (r == null) return Fail($"No request #{id}.");
                if (r.RequesterId != playerId) return Fail("Only the player who posted the request can confirm delivery.");
                if (r.Status != RequestStatus.Accepted) return Fail($"Request #{id} isn't in progress.");
                if (DateTime.UtcNow >= r.ExpiresUtc) return Fail($"The deadline for request #{id} has passed.");

                Close(tx, r, RequestStatus.Delivered);
                Event(rules, r, $"✅ Request #{r.Id} delivered", Green, $"{r.AccepterName} was paid {r.Price:N0} {rules.Currency}.", false,
                    $"#{r.Id} delivered (confirmed on {Server}): paying {r.AccepterName} {r.Price + r.Deposit:N0}");
                return Ok($"Delivery confirmed. {r.AccepterName} was paid {r.Price:N0} {rules.Currency}.", r,
                    new Payout { RequestId = r.Id, IdentityId = r.AccepterId, Amount = r.Price + r.Deposit, Reason = $"Request #{r.Id} delivered: you were paid {r.Price:N0} {rules.Currency} and your deposit of {r.Deposit:N0} was returned." });
            });

        public BoardResult Fail(int id, long playerId) =>
            Run($"player {playerId}", $"fail #{id}", (tx, rules) =>
            {
                var r = _db.Get(tx, id);
                if (r == null) return Fail($"No request #{id}.");
                if (r.RequesterId != playerId) return Fail("Only the player who posted the request can mark it as failed.");
                if (r.Status != RequestStatus.Accepted) return Fail($"Request #{id} isn't in progress.");
                if (DateTime.UtcNow >= r.ExpiresUtc) return Fail($"The deadline for request #{id} has passed, it will be failed automatically.");

                var payout = FailCore(tx, rules, r, "Marked as failed by the requester.");
                return Ok($"Request #{r.Id} marked as failed. Your {r.Price:N0} {rules.Currency} was returned.", r, payout);
            });

        public BoardResult Cancel(int id, long playerId) =>
            Run($"player {playerId}", $"cancel #{id}", (tx, rules) =>
            {
                var r = _db.Get(tx, id);
                if (r == null) return Fail($"No request #{id}.");
                if (r.RequesterId != playerId) return Fail("That isn't your request.");
                if (r.Status != RequestStatus.Open) return Fail("Only requests nobody has accepted yet can be cancelled.");
                if (DateTime.UtcNow >= r.ExpiresUtc) return Fail($"Request #{id} has expired, it will be refunded automatically.");

                Close(tx, r, RequestStatus.Cancelled);
                Event(rules, r, $"🚫 Request #{r.Id} cancelled", Grey, null, false, $"#{r.Id} cancelled by {r.RequesterName} on {Server}: refunding {r.Price:N0}");
                return Ok($"Request #{r.Id} cancelled and {r.Price:N0} {rules.Currency} refunded.", r,
                    new Payout { RequestId = r.Id, IdentityId = r.RequesterId, Amount = r.Price, Reason = $"Request #{r.Id} cancelled, {r.Price:N0} {rules.Currency} refunded." });
            });

        public BoardResult AdminCancel(int id) =>
            Run("admin", $"admin-cancel #{id}", (tx, rules) =>
            {
                var r = _db.Get(tx, id);
                if (r == null) return Fail($"No request #{id}.");
                if (r.Status != RequestStatus.Open && r.Status != RequestStatus.Accepted) return Fail($"Request #{id} is already closed.");

                var payouts = new List<Payout> { new Payout { RequestId = r.Id, IdentityId = r.RequesterId, Amount = r.Price, Reason = $"Request #{r.Id} was cancelled by an admin, {r.Price:N0} {rules.Currency} refunded." } };
                if (r.Status == RequestStatus.Accepted)
                    payouts.Add(new Payout { RequestId = r.Id, IdentityId = r.AccepterId, Amount = r.Deposit, Reason = $"Request #{r.Id} was cancelled by an admin, your deposit of {r.Deposit:N0} {rules.Currency} was refunded." });
                Close(tx, r, RequestStatus.Cancelled);
                Event(rules, r, $"🚫 Request #{r.Id} cancelled by admin", Grey, "All parties were refunded.", false, $"#{r.Id} cancelled by an admin on {Server}: refunding everyone");
                return Ok($"Request #{r.Id} cancelled, everyone refunded.", r, payouts.ToArray());
            });

        public SyncResult Sync()
        {
            lock (_db.Sync)
            {
                try
                {
                    SyncResult result;
                    using (var tx = _db.BeginWrite())
                    {
                        var rules = _db.LoadSettings(tx);
                        var now = DateTime.UtcNow;
                        result = new SyncResult { Currency = rules.Currency };
                        var due = _db.Query(tx, "WHERE status IN (@o, @a) AND expires_utc <= @now ORDER BY id",
                            ("@o", (int)RequestStatus.Open), ("@a", (int)RequestStatus.Accepted), ("@now", now.Ticks));
                        foreach (var r in due)
                        {
                            if (r.Status == RequestStatus.Open)
                            {
                                Close(tx, r, RequestStatus.Expired);
                                Event(rules, r, $"⌛ Request #{r.Id} expired", Grey, "Nobody accepted in time. The requester was refunded.", false,
                                    $"#{r.Id} expired: {Server} refunds {r.RequesterName} {r.Price:N0}");
                                result.Payouts.Add(new Payout { RequestId = r.Id, IdentityId = r.RequesterId, Amount = r.Price, Reason = $"Nobody accepted request #{r.Id} in time, {r.Price:N0} {rules.Currency} refunded." });
                            }
                            else
                            {
                                result.Payouts.Add(FailCore(tx, rules, r, "Deadline passed without delivery.", Server));
                            }
                        }
                        result.Active = _db.Query(tx, "WHERE status IN (@o, @a) ORDER BY id", ("@o", (int)RequestStatus.Open), ("@a", (int)RequestStatus.Accepted));
                        result.Payouts.RemoveAll(p => p.Amount <= 0);
                        tx.Commit();
                    }
                    FlushAfterCommit();
                    return result;
                }
                finally
                {
                    _afterCommit.Clear();
                }
            }
        }

        private Payout FailCore(SQLiteTransaction tx, BoardSettings rules, BoardRequest r, string reason, string payingServer = null)
        {
            var refund = r.Price + (rules.BurnDepositOnFail ? 0 : r.Deposit);
            Close(tx, r, RequestStatus.Failed);
            var lost = rules.BurnDepositOnFail ? "deposit was burned" : "deposit went to the requester";
            Event(rules, r, $"❌ Request #{r.Id} failed", Red, $"{reason} {r.AccepterName} lost {r.Deposit:N0} {rules.Currency} ({lost}).", false,
                $"#{r.Id} failed ({reason}): {(payingServer != null ? payingServer + " " : "")}refunds {r.RequesterName} {refund:N0}, {r.AccepterName}'s {lost}");
            return new Payout { RequestId = r.Id, IdentityId = r.RequesterId, Amount = refund, Reason = $"Request #{r.Id} failed ({reason}) {refund:N0} {rules.Currency} returned to you." };
        }

        private void Close(SQLiteTransaction tx, BoardRequest r, RequestStatus status)
        {
            r.Status = status;
            r.ClosedUtc = DateTime.UtcNow;
            _db.Update(tx, r);
        }

        private BoardResult Run(string player, string action, Func<SQLiteTransaction, BoardSettings, BoardResult> body)
        {
            lock (_db.Sync)
            {
                try
                {
                    BoardResult result;
                    using (var tx = _db.BeginWrite())
                    {
                        result = body(tx, _db.LoadSettings(tx));
                        if (result.Ok) tx.Commit();
                    }
                    if (result.Ok) FlushAfterCommit();
                    else Log.Info($"RequestBoard: [{Server}] {player} tried to {action}: rejected - {result.Message}");
                    return result;
                }
                finally
                {
                    _afterCommit.Clear();
                }
            }
        }

        private void FlushAfterCommit()
        {
            foreach (var a in _afterCommit)
            {
                try { a(); }
                catch (Exception e) { Log.Warn(e, "RequestBoard: post-commit notification failed"); }
            }
            _afterCommit.Clear();
        }

        private void Event(BoardSettings rules, BoardRequest r, string title, int color, string extra, bool showLocation, string consoleLine)
        {
            var fields = new List<EmbedField>
            {
                new EmbedField("Requester", r.RequesterName),
                new EmbedField("Sector", r.RequesterServer),
                new EmbedField("Price", $"{r.Price:N0} {rules.Currency}")
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
            var webhook = rules.DiscordWebhookUrl;

            _afterCommit.Add(() =>
            {
                Log.Info("RequestBoard: " + consoleLine);
                _discord.Send(webhook, title, description, color, fieldArray);
            });
        }

        public static string Gps(BoardRequest r) => string.Format(CultureInfo.InvariantCulture,
            "GPS:Request {0}:{1:F2}:{2:F2}:{3:F2}:#FF75C9F1:", r.Id, r.X, r.Y, r.Z);

        public static string Describe(BoardSettings r) => string.Format(CultureInfo.InvariantCulture,
            "price {0:N0}-{1} {2}, deposit {3}%, posting fee {9}, time {10}-{4} h, {5} active per player, cooldown {6} min, burn deposit on fail: {7}, GPS: {8}",
            r.MinPrice, r.MaxPrice <= 0 ? "unlimited" : r.MaxPrice.ToString("N0"), r.Currency, r.DepositPercent, r.MaxHours,
            r.MaxOpenPerPlayer, r.CooldownMinutes, r.BurnDepositOnFail, r.IncludeGps,
            r.PostingFeeMode == PostingFeeMode.Percent ? $"{r.PostingFee}%" : $"{r.PostingFee:N0} {r.Currency}", r.MinHours);

        private static string WebhookState(BoardSettings r) => string.IsNullOrWhiteSpace(r.DiscordWebhookUrl) ? "not configured" : "configured";

        private static string Validate(BoardSettings s)
        {
            if (string.IsNullOrEmpty(s.Currency) || s.Currency.Length > 16) return "Currency must be 1 to 16 characters.";
            if (s.DepositPercent < 0 || s.DepositPercent > 1000) return "Deposit percent must be between 0 and 1000.";
            if (!Enum.IsDefined(typeof(PostingFeeMode), s.PostingFeeMode)) return "Unknown posting fee type.";
            if (s.PostingFeeMode == PostingFeeMode.Percent && (!(s.PostingFee >= 0) || s.PostingFee > 1000))
                return "Posting fee percent must be between 0 and 1000.";
            if (s.PostingFeeMode == PostingFeeMode.Flat && (!(s.PostingFee >= 0) || s.PostingFee > 1e15 || s.PostingFee != Math.Floor(s.PostingFee)))
                return "Flat posting fee must be a whole number between 0 and 1,000,000,000,000,000.";
            if (s.MinPrice < 1) return "Minimum price must be at least 1.";
            if (s.MaxPrice < 0) return "Maximum price can't be negative (use 0 for unlimited).";
            if (s.MaxPrice > 0 && s.MaxPrice < s.MinPrice) return "Maximum price must be 0 (unlimited) or at least the minimum price.";
            if (!(s.MinHours > 0) || s.MinHours > 8760) return "Min hours must be between 0 and 8760.";
            if (!(s.MaxHours > 0) || s.MaxHours > 8760) return "Max hours must be between 0 and 8760.";
            if (s.MaxHours < s.MinHours) return "Max hours must be at least the min hours.";
            if (s.MaxOpenPerPlayer < 1) return "Active requests per player must be at least 1.";
            if (!(s.CooldownMinutes >= 0) || s.CooldownMinutes > 525600) return "Cooldown must be between 0 and 525600 minutes.";
            if (s.DiscordWebhookUrl.Length > 0 &&
                (!Uri.TryCreate(s.DiscordWebhookUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
                return "The Discord webhook must be empty or an https:// URL.";
            return null;
        }

        private static BoardResult Fail(string message) => new BoardResult { Ok = false, Message = message };

        private static BoardResult Ok(string message, BoardRequest r, params Payout[] payouts) =>
            new BoardResult { Ok = true, Message = message, Request = r, Payouts = payouts.Where(p => p.Amount > 0).ToList() };
    }
}
