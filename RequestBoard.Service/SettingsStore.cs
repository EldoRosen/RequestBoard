using System.Text.Json;
using RequestBoard.Contracts;

namespace RequestBoard.Service;

public sealed class SettingsStore
{
    private const string Key = "board";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Database _db;
    private readonly ILogger<SettingsStore> _log;
    private volatile BoardSettings _current;

    public SettingsStore(Database db, IConfiguration config, ILogger<SettingsStore> log)
    {
        _db = db;
        _log = log;
        lock (db.Sync)
        {
            var stored = db.GetSetting(Key);
            if (stored != null)
            {
                _current = JsonSerializer.Deserialize<BoardSettings>(stored, Json);
                return;
            }
            var seed = new BoardSettings();
            config.GetSection("Rules").Bind(seed);
            seed.DiscordWebhookUrl = config["Discord:WebhookUrl"] ?? "";
            db.SetSetting(Key, JsonSerializer.Serialize(seed, Json));
            _current = seed;
        }
    }

    public BoardSettings Current => _current;

    public SettingsResult Get() => new() { Ok = true, Settings = Clone(_current) };

    public SettingsResult Save(SaveSettingsCommand c)
    {
        var server = string.IsNullOrWhiteSpace(c?.ServerName) ? "unknown server" : c.ServerName;
        var s = c?.Settings;
        if (s == null) return new SettingsResult { Ok = false, Message = "No settings were sent." };
        s.Currency = s.Currency?.Trim();
        s.DiscordWebhookUrl = s.DiscordWebhookUrl?.Trim() ?? "";

        var error = Validate(s);
        if (error != null)
        {
            _log.LogInformation("[{Server}] settings change rejected - {Message}", server, error);
            return new SettingsResult { Ok = false, Message = error, Settings = Clone(_current) };
        }

        lock (_db.Sync)
        {
            _db.SetSetting(Key, JsonSerializer.Serialize(s, Json));
            _current = s;
        }
        _log.LogInformation("[{Server}] settings updated: {Rules}, Discord webhook {Webhook}", server, Describe(s), WebhookState(s));
        return new SettingsResult { Ok = true, Message = "Settings saved to the service. They apply to new requests on every server.", Settings = Clone(s) };
    }

    public static string Describe(BoardSettings r) => string.Format(System.Globalization.CultureInfo.InvariantCulture,
        "price {0:N0}-{1} {2}, deposit {3}%, posting fee {10}, max {4} h, {5} active per player, open expiry {6} h, cooldown {7} min, burn deposit on fail: {8}, GPS: {9}",
        r.MinPrice, r.MaxPrice <= 0 ? "unlimited" : r.MaxPrice.ToString("N0"), r.Currency, r.DepositPercent, r.MaxHours,
        r.MaxOpenPerPlayer, r.OpenExpiryHours, r.CooldownMinutes, r.BurnDepositOnFail, r.IncludeGps,
        r.PostingFeeMode == PostingFeeMode.Percent ? $"{r.PostingFee}%" : $"{r.PostingFee:N0} {r.Currency}");

    public static string WebhookState(BoardSettings r) => string.IsNullOrWhiteSpace(r.DiscordWebhookUrl) ? "not configured" : "configured";

    private static string Validate(BoardSettings s)
    {
        if (string.IsNullOrEmpty(s.Currency) || s.Currency.Length > 16) return "Currency must be 1 to 16 characters.";
        if (s.DepositPercent < 0 || s.DepositPercent > 1000) return "Deposit percent must be between 0 and 1000.";
        if (!Enum.IsDefined(s.PostingFeeMode)) return "Unknown posting fee type.";
        if (s.PostingFeeMode == PostingFeeMode.Percent && (!(s.PostingFee >= 0) || s.PostingFee > 1000))
            return "Posting fee percent must be between 0 and 1000.";
        if (s.PostingFeeMode == PostingFeeMode.Flat && (!(s.PostingFee >= 0) || s.PostingFee > 1e15 || s.PostingFee != Math.Floor(s.PostingFee)))
            return "Flat posting fee must be a whole number between 0 and 1,000,000,000,000,000.";
        if (s.MinPrice < 1) return "Minimum price must be at least 1.";
        if (s.MaxPrice < 0) return "Maximum price can't be negative (use 0 for unlimited).";
        if (s.MaxPrice > 0 && s.MaxPrice < s.MinPrice) return "Maximum price must be 0 (unlimited) or at least the minimum price.";
        if (!(s.MaxHours > 0) || s.MaxHours > 8760) return "Max hours must be between 0 and 8760.";
        if (s.MaxOpenPerPlayer < 1) return "Active requests per player must be at least 1.";
        if (!(s.OpenExpiryHours > 0) || s.OpenExpiryHours > 8760) return "Open expiry must be between 0 and 8760 hours.";
        if (!(s.CooldownMinutes >= 0) || s.CooldownMinutes > 525600) return "Cooldown must be between 0 and 525600 minutes.";
        if (s.DiscordWebhookUrl.Length > 0 &&
            (!Uri.TryCreate(s.DiscordWebhookUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            return "The Discord webhook must be empty or an https:// URL.";
        return null;
    }

    private static BoardSettings Clone(BoardSettings s) =>
        JsonSerializer.Deserialize<BoardSettings>(JsonSerializer.Serialize(s, Json), Json);
}
