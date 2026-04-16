namespace HarborSentinel.AdminConsole.Services;

public interface ITunnelSecretStore
{
    Task<string?> GetSecretAsync(string profileId, SshTunnelSecretKind kind, CancellationToken ct);
    Task SaveSecretAsync(string profileId, SshTunnelSecretKind kind, string secret, CancellationToken ct);
    Task DeleteSecretAsync(string profileId, SshTunnelSecretKind kind, CancellationToken ct);
    Task<bool> HasSecretAsync(string profileId, SshTunnelSecretKind kind, CancellationToken ct);
}
