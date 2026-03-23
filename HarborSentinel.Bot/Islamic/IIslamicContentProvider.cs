namespace HarborSentinel.Bot.Islamic;

public interface IIslamicContentProvider
{
    Task<DuaEntry?> GetRandomDuaAsync(string? topicQuery, CancellationToken ct = default);
    Task<DuaEntry?> GetSuggestedDuaAsync(string? topicQuery, CancellationToken ct = default);
    Task<string[]> GetDuaTopicsAsync(CancellationToken ct = default);
    Task<DuaEntry?> GetDuaOfDayAsync(DateTimeOffset nowUtc, CancellationToken ct = default);
    Task<DuaEntry?> GetRandomWelcomeDuaAsync(CancellationToken ct = default);

    Task<AzkarEntry?> GetRandomAzkarAsync(string? topicQuery, CancellationToken ct = default);
    Task<AzkarEntry?> GetSuggestedAzkarAsync(string? topicQuery, CancellationToken ct = default);
    Task<string[]> GetAzkarTopicsAsync(CancellationToken ct = default);
    Task<AzkarEntry?> GetAzkarOfDayAsync(DateTimeOffset nowUtc, CancellationToken ct = default);

    Task<AyahEntry?> GetAyahOfDayAsync(DateTimeOffset nowUtc, CancellationToken ct = default);
}
