namespace HarborSentinel.AdminConsole.Services;

public interface IAdminConsoleStoragePaths
{
    string RootDirectory { get; }
    string SshTunnelProfilesPath { get; }
    string SshTunnelSecretsPath { get; }
    string ConnectionSettingsPath { get; }
}
