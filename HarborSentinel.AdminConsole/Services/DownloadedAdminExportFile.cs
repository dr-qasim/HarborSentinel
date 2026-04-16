namespace HarborSentinel.AdminConsole.Services;

public sealed class DownloadedAdminExportFile
{
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required byte[] Content { get; init; }
}
