namespace HarborSentinel.AdminConsole.Services;

public interface IExportFileSaveService
{
    string? PickSavePath(string suggestedFileName);

    Task SaveAsync(string path, byte[] content, CancellationToken ct);
}
