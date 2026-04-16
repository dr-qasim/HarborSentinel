using System.IO;

namespace HarborSentinel.AdminConsole.Services;

public sealed class AdminConsoleStoragePaths : IAdminConsoleStoragePaths
{
    public AdminConsoleStoragePaths()
    {
        RootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HarborSentinel.AdminConsole");
    }

    public string RootDirectory { get; }

    public string SshTunnelProfilesPath => Path.Combine(RootDirectory, "ssh-tunnel-profiles.json");

    public string SshTunnelSecretsPath => Path.Combine(RootDirectory, "ssh-tunnel-secrets.json");

    public string ConnectionSettingsPath => Path.Combine(RootDirectory, "connection-settings.json");
}
