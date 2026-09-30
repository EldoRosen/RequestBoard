using System.Net.Http.Json;
using System.Threading.Channels;

namespace RequestBoard.Service;

public sealed record EmbedField(string Name, string Value, bool Inline = true);

public sealed class DiscordNotifier : BackgroundService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly Channel<object> _queue = Channel.CreateBounded<object>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly SettingsStore _settings;
    private readonly ILogger<DiscordNotifier> _log;

    public DiscordNotifier(SettingsStore settings, ILogger<DiscordNotifier> log)
    {
        _settings = settings;
        _log = log;
    }

    public void Send(string title, string description, int color, params EmbedField[] fields)
    {
        if (string.IsNullOrWhiteSpace(_settings.Current.DiscordWebhookUrl)) return;
        _queue.Writer.TryWrite(new
        {
            allowed_mentions = new { parse = Array.Empty<string>() },
            embeds = new[]
            {
                new
                {
                    title = Trunc(title, 250),
                    description = Trunc(description, 1800),
                    color,
                    fields = fields.Select(f => new { name = f.Name, value = string.IsNullOrEmpty(f.Value) ? "-" : Trunc(f.Value, 1000), inline = f.Inline }),
                    timestamp = DateTime.UtcNow.ToString("o")
                }
            }
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await foreach (var payload in _queue.Reader.ReadAllAsync(stop))
        {
            var url = _settings.Current.DiscordWebhookUrl;
            if (string.IsNullOrWhiteSpace(url)) continue;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var resp = await Http.PostAsJsonAsync(url, payload, stop);
                    if ((int)resp.StatusCode == 429) { await Task.Delay(2000, stop); continue; }
                    if (!resp.IsSuccessStatusCode) _log.LogWarning("Discord webhook returned {Status}", (int)resp.StatusCode);
                    break;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception e)
                {
                    _log.LogWarning("Discord webhook post failed: {Message}", e.Message);
                    await Task.Delay(1000, stop);
                }
            }
            await Task.Delay(350, stop);
        }
    }

    private static string Trunc(string s, int max) => s == null ? "" : s.Length <= max ? s : s[..(max - 1)] + "…";
}
