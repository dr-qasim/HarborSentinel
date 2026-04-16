using HarborSentinel.AdminContracts.Chats;

namespace HarborSentinel.AdminHost.Services;

public interface IChatQueryService
{
    Task<AdminChatListResponseDto> GetChatListAsync(
        int page,
        int pageSize,
        AdminChatActivityFilterDto activityFilter,
        AdminChatTrackingFilterDto trackingFilter,
        CancellationToken ct);

    Task<AdminChatDetailDto?> GetChatDetailAsync(long chatId, CancellationToken ct);
}
