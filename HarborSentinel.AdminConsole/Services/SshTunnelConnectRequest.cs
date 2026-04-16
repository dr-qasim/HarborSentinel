namespace HarborSentinel.AdminConsole.Services;

public sealed class SshTunnelConnectRequest
{
    public required SshTunnelProfileRecord Profile { get; init; }
    public string? Password { get; init; }
    public string? PrivateKeyPassphrase { get; init; }
}
