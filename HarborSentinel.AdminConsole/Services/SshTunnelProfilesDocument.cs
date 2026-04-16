namespace HarborSentinel.AdminConsole.Services;

public sealed class SshTunnelProfilesDocument
{
    public string? LastSelectedProfileId { get; set; }
    public List<SshTunnelProfileRecord> Profiles { get; set; } = [];
}
