namespace HarborSentinel.AdminConsole.Services;

public enum SshTunnelConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    Failed = 4
}
