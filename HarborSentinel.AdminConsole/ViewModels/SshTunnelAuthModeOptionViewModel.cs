using HarborSentinel.AdminConsole.Services;

namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class SshTunnelAuthModeOptionViewModel
{
    public required SshTunnelAuthMode Value { get; init; }
    public required string Label { get; init; }
}
