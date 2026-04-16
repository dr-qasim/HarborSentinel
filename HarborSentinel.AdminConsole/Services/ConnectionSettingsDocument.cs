namespace HarborSentinel.AdminConsole.Services;

public sealed class ConnectionSettingsDocument
{
    public string? BaseUrl { get; set; }
    public string? ApiToken { get; set; }
    public int? RequestTimeoutSeconds { get; set; }
}
