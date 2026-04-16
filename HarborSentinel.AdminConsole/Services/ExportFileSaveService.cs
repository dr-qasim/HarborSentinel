using System.IO;
using Microsoft.Win32;

namespace HarborSentinel.AdminConsole.Services;

public sealed class ExportFileSaveService : IExportFileSaveService
{
    public string? PickSavePath(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            OverwritePrompt = true,
            FileName = suggestedFileName,
            Filter = "GZip архив (*.gz)|*.gz|JSON (*.json)|*.json|Все файлы (*.*)|*.*"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public Task SaveAsync(string path, byte[] content, CancellationToken ct)
        => File.WriteAllBytesAsync(path, content, ct);
}
