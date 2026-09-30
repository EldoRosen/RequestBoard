using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog;
using RequestBoard.Backend;
using RequestBoard.Contracts;
using Sandbox.Game;
using Torch.API;

namespace RequestBoard
{
    public class RequestService
    {
        private const string Unreachable = "Request failed: the request board service can't be reached right now. Please try again later.";
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly ITorchBase _torch;
        private readonly RequestBoardConfig _cfg;
        private readonly IRequestBoardBackend _backend;
        private readonly object _syncGate = new object();
        private Timer _timer;
        private volatile bool _started;
        private int _syncing;

        public List<RequestDto> Active { get; private set; } = new List<RequestDto>();
        public string Currency { get; private set; } = "SC";
        public DateTime? LastSyncUtc { get; private set; }
        public bool Running => _started;

        public event Action Synced;

        public RequestService(ITorchBase torch, RequestBoardConfig cfg, IRequestBoardBackend backend)
        {
            _torch = torch;
            _cfg = cfg;
            _backend = backend;
        }

        public void Start()
        {
            lock (_syncGate)
            {
                if (_started) return;
                _started = true;
                _timer = new Timer(_ => SyncNow(), null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
            }
        }

        public void Stop()
        {
            lock (_syncGate)
            {
                _started = false;
                _timer?.Dispose();
                _timer = null;
            }
        }

        public void Create(long playerId, string playerName, string text, string hoursStr, string priceStr, double[] position, Action<string> reply)
        {
            if (!double.TryParse(hoursStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) || double.IsNaN(hours) || double.IsInfinity(hours) || hours <= 0)
            {
                reply("Time must be a positive number of hours.");
                return;
            }
            if (!long.TryParse(priceStr, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var price) || price <= 0)
            {
                reply("Price must be a positive whole number.");
                return;
            }
            if (Bank.Balance(playerId) < price) { reply($"You need {price:N0} {Currency} to post this request."); return; }
            if (!Bank.Add(playerId, -price)) { reply("Could not withdraw the credits."); return; }

            var cmd = new CreateCommand
            {
                OperationId = NewOp(),
                ServerName = _cfg.ServerName,
                PlayerId = playerId,
                PlayerName = playerName,
                Text = text,
                Hours = hours,
                Price = price,
                HasLocation = position != null,
                X = position?[0] ?? 0,
                Y = position?[1] ?? 0,
                Z = position?[2] ?? 0
            };
            Call(() => _backend.CreateAsync(cmd),
                result =>
                {
                    if (!result.Ok) Refund(playerId, price, "rejected request");
                    reply(result.Message);
                },
                e =>
                {
                    Log.Error(e, $"RequestBoard: posting a request for {playerName} failed, refunding {price:N0}");
                    Refund(playerId, price, "unreachable service");
                    reply(Unreachable + " Your credits were refunded.");
                });
        }

        public void Accept(long playerId, string playerName, int id, Action<string> reply)
        {
            Call(() => _backend.GetAsync(id),
                lookup =>
                {
                    if (!lookup.Ok) { reply(lookup.Message); return; }
                    var r = lookup.Request;
                    if (r.Status != RequestStatus.Open) { reply($"Request #{id} is no longer open."); return; }
                    if (r.RequesterId == playerId) { reply("You can't accept your own request."); return; }
                    var deposit = r.Deposit;
                    if (Bank.Balance(playerId) < deposit) { reply($"You need a deposit of {deposit:N0} {Currency} to accept this."); return; }
                    if (!Bank.Add(playerId, -deposit)) { reply("Could not withdraw the deposit."); return; }

                    var cmd = new AcceptCommand { OperationId = NewOp(), ServerName = _cfg.ServerName, PlayerId = playerId, PlayerName = playerName, Deposit = deposit };
                    Call(() => _backend.AcceptAsync(id, cmd),
                        result =>
                        {
                            if (!result.Ok) Refund(playerId, deposit, $"rejected accept of #{id}");
                            reply(result.Message);
                        },
                        e =>
                        {
                            Log.Error(e, $"RequestBoard: accepting #{id} for {playerName} failed, refunding deposit {deposit:N0}");
                            Refund(playerId, deposit, "unreachable service");
                            reply(Unreachable + " Your deposit was refunded.");
                        });
                },
                e =>
                {
                    Log.Error(e, $"RequestBoard: looking up #{id} for {playerName} failed");
                    reply(Unreachable);
                });
        }

        public void Deliver(long playerId, int id, Action<string> reply) =>
            Settle(() => _backend.DeliverAsync(id, new PlayerCommand { OperationId = NewOp(), ServerName = _cfg.ServerName, PlayerId = playerId }), playerId, $"deliver #{id}", reply);

        public void Fail(long playerId, int id, Action<string> reply) =>
            Settle(() => _backend.FailAsync(id, new PlayerCommand { OperationId = NewOp(), ServerName = _cfg.ServerName, PlayerId = playerId }), playerId, $"fail #{id}", reply);

        public void Cancel(long playerId, int id, Action<string> reply) =>
            Settle(() => _backend.CancelAsync(id, new PlayerCommand { OperationId = NewOp(), ServerName = _cfg.ServerName, PlayerId = playerId }), playerId, $"cancel #{id}", reply);

        public void AdminCancel(int id, Action<string> reply) =>
            Settle(() => _backend.AdminCancelAsync(id, new AdminCommand { OperationId = NewOp(), ServerName = _cfg.ServerName }), 0, $"admin-cancel #{id}", reply);

        public void ListOpen(Action<OpenListResult> onResult, Action<string> reply)
        {
            Call(() => _backend.ListOpenAsync(),
                result =>
                {
                    if (!string.IsNullOrEmpty(result.Currency)) Currency = result.Currency;
                    onResult(result);
                },
                e =>
                {
                    Log.Error(e, "RequestBoard: listing requests failed");
                    reply(Unreachable);
                });
        }

        public void SyncNow()
        {
            if (!_started || Interlocked.Exchange(ref _syncing, 1) == 1) return;
            var cmd = new SyncCommand { OperationId = NewOp(), ServerName = _cfg.ServerName };
            Call(() => _backend.SyncAsync(cmd),
                result =>
                {
                    foreach (var p in result.Payouts) Pay(p, 0);
                    Active = result.Active ?? new List<RequestDto>();
                    if (!string.IsNullOrEmpty(result.Currency)) Currency = result.Currency;
                    LastSyncUtc = DateTime.UtcNow;
                    Synced?.Invoke();
                },
                e => Log.Error(e, "RequestBoard: sync with the request board service failed"),
                FinishSync);
        }

        private void FinishSync()
        {
            Interlocked.Exchange(ref _syncing, 0);
            lock (_syncGate)
            {
                if (!_started || _timer == null) return;
                _timer.Change(TimeSpan.FromSeconds(Math.Max(10, _cfg.SyncIntervalSeconds)), Timeout.InfiniteTimeSpan);
            }
        }

        private void Settle(Func<Task<ApiResult>> call, long callerId, string action, Action<string> reply)
        {
            Call(call,
                result =>
                {
                    if (result.Ok)
                        foreach (var p in result.Payouts) Pay(p, callerId);
                    reply(result.Message);
                },
                e =>
                {
                    Log.Error(e, $"RequestBoard: {action} failed");
                    reply(Unreachable);
                });
        }

        private void Pay(Payout p, long callerId)
        {
            if (p.Amount <= 0) return;
            if (Bank.Add(p.IdentityId, p.Amount))
            {
                Log.Info($"RequestBoard: paid {p.Amount:N0} to identity {p.IdentityId} for request #{p.RequestId}");
                if (p.IdentityId != callerId) Notify(p.IdentityId, p.Reason);
            }
            else
            {
                Log.Error($"RequestBoard: PAYOUT FAILED - identity {p.IdentityId} is owed {p.Amount:N0} for request #{p.RequestId} ({p.Reason}). Pay it manually.");
            }
        }

        private void Refund(long identityId, long amount, string why)
        {
            if (Bank.Add(identityId, amount)) return;
            Log.Error($"RequestBoard: REFUND FAILED ({why}) - identity {identityId} is owed {amount:N0}. Pay it manually.");
        }

        private void Call<T>(Func<Task<T>> call, Action<T> onResult, Action<Exception> onError, Action always = null)
        {
            Task.Run(call).ContinueWith(t =>
            {
                Action work;
                object dropped;
                if (t.IsFaulted || t.IsCanceled)
                {
                    var error = (Exception)t.Exception?.GetBaseException() ?? new BackendUnavailableException("The request was cancelled.");
                    work = () => onError(error);
                    dropped = new { Error = error.Message };
                }
                else
                {
                    var value = t.Result;
                    work = () => onResult(value);
                    dropped = value;
                }
                try
                {
                    _torch.Invoke(() =>
                    {
                        try { work(); }
                        catch (Exception e) { Log.Error(e, "RequestBoard: handling a service response failed"); }
                    });
                }
                catch (Exception e) { Log.Error(e, $"RequestBoard: could not return to the game thread, this service response was NOT applied (pay any payouts manually): {JsonConvert.SerializeObject(dropped)}"); }
                always?.Invoke();
            });
        }

        private static void Notify(long identityId, string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            try { MyVisualScriptLogicProvider.SendChatMessage(message, "Request Board", identityId); }
            catch (Exception e) { Log.Warn(e, "RequestBoard: could not send chat message"); }
        }

        private static string NewOp() => Guid.NewGuid().ToString("N");

        public static string Gps(RequestDto r) => string.Format(CultureInfo.InvariantCulture,
            "GPS:Request {0}:{1:F2}:{2:F2}:{3:F2}:#FF75C9F1:", r.Id, r.X, r.Y, r.Z);

        public static string TimeLeft(DateTime untilUtc)
        {
            var left = untilUtc - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return "0m";
            if (left.TotalDays >= 1) return $"{(int)left.TotalDays}d {left.Hours}h";
            if (left.TotalHours >= 1) return $"{(int)left.TotalHours}h {left.Minutes}m";
            return $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m";
        }
    }
}
