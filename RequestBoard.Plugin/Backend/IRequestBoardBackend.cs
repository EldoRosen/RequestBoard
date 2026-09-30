using System.Threading.Tasks;
using RequestBoard.Contracts;

namespace RequestBoard.Backend
{
    public interface IRequestBoardBackend
    {
        Task CheckConnectionAsync();
        Task<ApiResult> GetAsync(int id);
        Task<OpenListResult> ListOpenAsync();
        Task<ApiResult> CreateAsync(CreateCommand command);
        Task<ApiResult> AcceptAsync(int id, AcceptCommand command);
        Task<ApiResult> DeliverAsync(int id, PlayerCommand command);
        Task<ApiResult> FailAsync(int id, PlayerCommand command);
        Task<ApiResult> CancelAsync(int id, PlayerCommand command);
        Task<ApiResult> AdminCancelAsync(int id, AdminCommand command);
        Task<SyncResult> SyncAsync(SyncCommand command);
        Task<SettingsResult> GetSettingsAsync();
        Task<SettingsResult> SaveSettingsAsync(SaveSettingsCommand command);
    }
}
