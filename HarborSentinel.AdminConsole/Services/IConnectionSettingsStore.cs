namespace HarborSentinel.AdminConsole.Services;

public interface IConnectionSettingsStore
{
    Task<ConnectionSettingsDocument> LoadAsync(CancellationToken ct);
    Task SaveAsync(ConnectionSettingsDocument document, CancellationToken ct);
}
