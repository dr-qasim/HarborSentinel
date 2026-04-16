namespace HarborSentinel.AdminHost.Services;

public sealed class ExportJobDownloadResult
{
    public required string FilePath { get; init; }
    public required string ContentType { get; init; }
    public required string DownloadFileName { get; init; }
}
