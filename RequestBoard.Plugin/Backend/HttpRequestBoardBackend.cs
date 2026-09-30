using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RequestBoard.Contracts;

namespace RequestBoard.Backend
{
    public class HttpRequestBoardBackend : IRequestBoardBackend
    {
        private const int Attempts = 2;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        private readonly Func<string> _baseUrl;

        public HttpRequestBoardBackend(Func<string> baseUrl) { _baseUrl = baseUrl; }

        public Task CheckConnectionAsync() => Get<object>("health");
        public Task<ApiResult> GetAsync(int id) => Get<ApiResult>($"requests/{id}");
        public Task<OpenListResult> ListOpenAsync() => Get<OpenListResult>("requests");
        public Task<ApiResult> CreateAsync(CreateCommand command) => Post<ApiResult>("requests", command);
        public Task<ApiResult> AcceptAsync(int id, AcceptCommand command) => Post<ApiResult>($"requests/{id}/accept", command);
        public Task<ApiResult> DeliverAsync(int id, PlayerCommand command) => Post<ApiResult>($"requests/{id}/deliver", command);
        public Task<ApiResult> FailAsync(int id, PlayerCommand command) => Post<ApiResult>($"requests/{id}/fail", command);
        public Task<ApiResult> CancelAsync(int id, PlayerCommand command) => Post<ApiResult>($"requests/{id}/cancel", command);
        public Task<ApiResult> AdminCancelAsync(int id, AdminCommand command) => Post<ApiResult>($"requests/{id}/admin-cancel", command);
        public Task<SyncResult> SyncAsync(SyncCommand command) => Post<SyncResult>("sync", command);
        public Task<SettingsResult> GetSettingsAsync() => Get<SettingsResult>("settings");
        public Task<SettingsResult> SaveSettingsAsync(SaveSettingsCommand command) => Post<SettingsResult>("settings", command);

        private Task<T> Get<T>(string path) =>
            SendAsync<T>(() => new HttpRequestMessage(HttpMethod.Get, Url(path)));

        private Task<T> Post<T>(string path, object body)
        {
            var json = JsonConvert.SerializeObject(body);
            return SendAsync<T>(() => new HttpRequestMessage(HttpMethod.Post, Url(path)) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }

        private Uri Url(string path)
        {
            var root = (_baseUrl() ?? "").Trim().TrimEnd('/');
            if (!Uri.TryCreate(root + "/" + path, UriKind.Absolute, out var uri))
                throw new BackendUnavailableException($"Service URL '{root}' is not valid.");
            return uri;
        }

        private static async Task<T> SendAsync<T>(Func<HttpRequestMessage> makeRequest)
        {
            Exception last = null;
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                try
                {
                    using (var request = makeRequest())
                    using (var response = await Http.SendAsync(request).ConfigureAwait(false))
                    {
                        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if ((int)response.StatusCode >= 500)
                        {
                            last = new BackendUnavailableException($"{request.Method} {request.RequestUri} returned HTTP {(int)response.StatusCode}: {text}");
                            continue;
                        }
                        if (response.StatusCode != HttpStatusCode.OK)
                            throw new BackendUnavailableException($"{request.Method} {request.RequestUri} returned HTTP {(int)response.StatusCode}: {text}");
                        var result = JsonConvert.DeserializeObject<T>(text);
                        if (result == null) throw new BackendUnavailableException($"{request.Method} {request.RequestUri} returned an empty response.");
                        return result;
                    }
                }
                catch (BackendUnavailableException) { throw; }
                catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is JsonException)
                {
                    last = e;
                }
            }
            throw new BackendUnavailableException("The request board service could not be reached: " + last?.Message, last);
        }
    }
}
