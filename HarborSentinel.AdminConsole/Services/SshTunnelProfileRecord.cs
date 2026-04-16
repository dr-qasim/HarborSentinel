namespace HarborSentinel.AdminConsole.Services;

public sealed class SshTunnelProfileRecord
{
    public string ProfileId { get; set; } = Guid.NewGuid().ToString("N");
    public string ProfileName { get; set; } = "Основной VPS";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public SshTunnelAuthMode AuthMode { get; set; } = SshTunnelAuthMode.Password;
    public string PrivateKeyPath { get; set; } = string.Empty;
    public int LocalPort { get; set; } = 58421;
    public string RemoteHost { get; set; } = "127.0.0.1";
    public int RemotePort { get; set; } = 58421;
    public bool AutoConnectOnStartup { get; set; }
}
