using HarborSentinel.AdminContracts.Users;

namespace HarborSentinel.AdminHost.Services;

public interface IUserQueryService
{
    Task<AdminUserListResponseDto> GetUsersAsync(
        int page,
        int pageSize,
        AdminUserListSourceFilterDto sourceFilter,
        string? searchQuery,
        CancellationToken ct);

    Task<AdminUserDetailDto?> GetUserDetailAsync(long userId, CancellationToken ct);
}
