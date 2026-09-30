using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog;

namespace RequestBoard.Board
{
    public sealed class EmbedField
    {
        public EmbedField(string name, string value, bool inline = true)
        {
            Name = name;
            Value = value;
            Inline = inline;
        }

        public string Name { get; }
        public string Value { get; }
        public bool Inline { get; }
    }

    public sealed class DiscordNotifier : IDisposable
    {
        private const int MaxQueued = 500;
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        private readonly ConcurrentQueue<(string Url, string Json)> _queue = new ConcurrentQueue<(string, string)>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();

        public DiscordNotifier()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Task.Run(Worker);
        }

        public void Send(string webhookUrl, string title, string description, int color, params EmbedField[] fields)
        {
            if (string.IsNullOrWhiteSpace(webhookUrl) || _stop.IsCancellationRequested) return;
            var payload = new
            {
                allowed_mentions = new { parse = new string[0] },
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
            };
            while (_queue.Count >= MaxQueued && _queue.TryDequeue(out _)) { }
            _queue.Enqueue((webhookUrl.Trim(), JsonConvert.SerializeObject(payload)));
            _signal.Release();
        }

        public void Dispose() => _stop.Cancel();

        private async Task Worker()
        {
            var stop = _stop.Token;
            try
            {
                while (true)
                {
                    await _signal.WaitAsync(stop).ConfigureAwait(false);
                    if (!_queue.TryDequeue(out var item)) continue;
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            using (var content = new StringContent(item.Json, Encoding.UTF8, "application/json"))
                            using (var resp = await Http.PostAsync(item.Url, content, stop).ConfigureAwait(false))
                            {
                                if ((int)resp.StatusCode == 429) { await Task.Delay(2000, stop).ConfigureAwait(false); continue; }
                                if (!resp.IsSuccessStatusCode) Log.Warn($"RequestBoard: Discord webhook returned {(int)resp.StatusCode}");
                                break;
                            }
                        }
                        catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
                        catch (Exception e)
                        {
                            Log.Warn($"RequestBoard: Discord webhook post failed: {e.Message}");
                            await Task.Delay(1000, stop).ConfigureAwait(false);
                        }
                    }
                    await Task.Delay(350, stop).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }

        private static string Trunc(string s, int max) => s == null ? "" : s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
