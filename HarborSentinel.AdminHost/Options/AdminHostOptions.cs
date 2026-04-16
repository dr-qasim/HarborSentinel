namespace HarborSentinel.AdminHost.Options;

public sealed class AdminHostOptions
{
    public string ListenUrl { get; set; } = "http://127.0.0.1:58421";
    public string ApiToken { get; set; } = string.Empty;
}
