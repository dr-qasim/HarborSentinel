namespace HarborSentinel.AdminConsole.Services;

public interface ISshTunnelManager : IAsyncDisposable
{
    event EventHandler? StateChanged;

    SshTunnelConnectionState State { get; }
    string StatusText { get; }
    string? ActiveProfileId { get; }
    string? LastFailureReason { get; }

    Task ConnectAsync(SshTunnelConnectRequest request, CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
    Task ReconnectAsync(CancellationToken ct);
    Task TestConnectionAsync(SshTunnelConnectRequest request, CancellationToken ct);
}
