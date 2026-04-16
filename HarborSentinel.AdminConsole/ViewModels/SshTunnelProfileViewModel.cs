using HarborSentinel.AdminConsole.Infrastructure;
using HarborSentinel.AdminConsole.Services;

namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class SshTunnelProfileViewModel : ObservableObject
{
    private string _profileId = Guid.NewGuid().ToString("N");
    private string _profileName = "Новый профиль";
    private string _host = string.Empty;
    private int _port = 22;
    private string _username = string.Empty;
    private SshTunnelAuthMode _authMode = SshTunnelAuthMode.Password;
    private string _privateKeyPath = string.Empty;
    private int _localPort = 58421;
    private string _remoteHost = "127.0.0.1";
    private int _remotePort = 58421;
    private bool _autoConnectOnStartup;

    public string ProfileId
    {
        get => _profileId;
        set => SetProperty(ref _profileId, string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value);
    }

    public string ProfileName
    {
        get => _profileName;
        set => SetProperty(ref _profileName, string.IsNullOrWhiteSpace(value) ? "Новый профиль" : value);
    }

    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value?.Trim() ?? string.Empty);
    }

    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value?.Trim() ?? string.Empty);
    }

    public SshTunnelAuthMode AuthMode
    {
        get => _authMode;
        set => SetProperty(ref _authMode, value);
    }

    public string PrivateKeyPath
    {
        get => _privateKeyPath;
        set => SetProperty(ref _privateKeyPath, value?.Trim() ?? string.Empty);
    }

    public int LocalPort
    {
        get => _localPort;
        set => SetProperty(ref _localPort, value);
    }

    public string RemoteHost
    {
        get => _remoteHost;
        set => SetProperty(ref _remoteHost, value?.Trim() ?? string.Empty);
    }

    public int RemotePort
    {
        get => _remotePort;
        set => SetProperty(ref _remotePort, value);
    }

    public bool AutoConnectOnStartup
    {
        get => _autoConnectOnStartup;
        set => SetProperty(ref _autoConnectOnStartup, value);
    }

    public override string ToString() => ProfileName;

    public SshTunnelProfileRecord ToRecord()
    {
        return new SshTunnelProfileRecord
        {
            ProfileId = ProfileId,
            ProfileName = ProfileName,
            Host = Host,
            Port = Port,
            Username = Username,
            AuthMode = AuthMode,
            PrivateKeyPath = PrivateKeyPath,
            LocalPort = LocalPort,
            RemoteHost = RemoteHost,
            RemotePort = RemotePort,
            AutoConnectOnStartup = AutoConnectOnStartup
        };
    }

    public static SshTunnelProfileViewModel FromRecord(SshTunnelProfileRecord record)
    {
        return new SshTunnelProfileViewModel
        {
            ProfileId = record.ProfileId,
            ProfileName = record.ProfileName,
            Host = record.Host,
            Port = record.Port,
            Username = record.Username,
            AuthMode = record.AuthMode,
            PrivateKeyPath = record.PrivateKeyPath,
            LocalPort = record.LocalPort,
            RemoteHost = record.RemoteHost,
            RemotePort = record.RemotePort,
            AutoConnectOnStartup = record.AutoConnectOnStartup
        };
    }
}
