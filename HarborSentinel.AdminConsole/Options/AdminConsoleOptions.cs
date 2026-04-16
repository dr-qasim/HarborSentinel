namespace HarborSentinel.AdminConsole.Options;

public sealed class AdminConsoleOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:58421";
    public string ApiToken { get; set; } = string.Empty;
    public int RequestTimeoutSeconds { get; set; } = 8;
    public string OperatorId { get; set; } = string.Empty;
}
