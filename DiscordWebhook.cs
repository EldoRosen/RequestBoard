using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog;

namespace RequestBoard
{
    public class EmbedField
    {
        public string Name;
        public string Value;
        public bool Inline = true;
        public EmbedField(string name, string value, bool inline = true) { Name = name; Value = value; Inline = inline; }
    }

    /// <summary>Queues messages and posts them on a background thread so the game never waits on Discord.</summary>
    public class DiscordWebhook : IDisposable
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        private readonly Func<string> _urlProvider;
        private readonly BlockingCollection<string> _queue = new BlockingCollection<string>(200);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _worker;

        public DiscordWebhook(Func<string> urlProvider)
        {
            _urlProvider = urlProvider;
            _worker = Task.Run(Worker);
        }

        public void Send(string title, string description, int color, params EmbedField[] fields)
        {
            if (string.IsNullOrWhiteSpace(_urlProvider())) return;

            var embedFields = new List<object>();
            foreach (var f in fields)
                embedFields.Add(new { name = f.Name, value = string.IsNullOrEmpty(f.Value) ? "-" : Trunc(f.Value, 1000), inline = f.Inline });

            var payload = JsonConvert.SerializeObject(new
            {
                // Empty parse list = @everyone / @here in player text can never ping anyone.
                allowed_mentions = new { parse = new string[0] },
                embeds = new[]
                {
                    new
                    {
                        title = Trunc(title, 250),
                        description = Trunc(description, 1800),
                        color,
                        fields = embedFields,
                        timestamp = DateTime.UtcNow.ToString("o")
                    }
                }
            });

            if (!_queue.TryAdd(payload))
                Log.Warn("Discord queue full, dropping message.");
        }

        private async Task Worker()
        {
            try
            {
                foreach (var payload in _queue.GetConsumingEnumerable(_cts.Token))
                {
                    var url = _urlProvider();
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                            using (var resp = await Http.PostAsync(url, content, _cts.Token))
                            {
                                if ((int)resp.StatusCode == 429)
                                {
                                    await Task.Delay(2000, _cts.Token);
                                    continue;
                                }
                                if (!resp.IsSuccessStatusCode)
                                    Log.Warn($"Discord webhook returned {(int)resp.StatusCode}");
                                break;
                            }
                        }
                        catch (OperationCanceledException) { return; }
                        catch (Exception e) { Log.Warn(e, "Discord webhook post failed"); await Task.Delay(1000); }
                    }
                    await Task.Delay(350, _cts.Token); // stay well under Discord's rate limit
                }
            }
            catch (OperationCanceledException) { }
        }

        private static string Trunc(string s, int max) => s == null ? "" : (s.Length <= max ? s : s.Substring(0, max - 1) + "…");

        public void Dispose()
        {
            _cts.Cancel();
            _queue.CompleteAdding();
        }
    }
}
