namespace HarborSentinel.AdminConsole.Services;

public interface ISshTunnelProfileStore
{
    Task<SshTunnelProfilesDocument> LoadAsync(CancellationToken ct);
    Task SaveAsync(SshTunnelProfilesDocument document, CancellationToken ct);
}
