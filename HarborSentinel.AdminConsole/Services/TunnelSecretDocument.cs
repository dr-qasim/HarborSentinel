namespace HarborSentinel.AdminConsole.Services;

public sealed class TunnelSecretDocument
{
    public Dictionary<string, string> EncryptedSecrets { get; set; } = [];
}
