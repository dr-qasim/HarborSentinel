using System.IO;
using System.Text.Json;

namespace HarborSentinel.AdminConsole.Services;

public sealed class ConnectionSettingsStore : IConnectionSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IAdminConsoleStoragePaths _paths;

    public ConnectionSettingsStore(IAdminConsoleStoragePaths paths)
    {
        _paths = paths;
    }

    public async Task<ConnectionSettingsDocument> LoadAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.RootDirectory);
        if (!File.Exists(_paths.ConnectionSettingsPath))
            return new ConnectionSettingsDocument();

        await using var stream = File.OpenRead(_paths.ConnectionSettingsPath);
        var document = await JsonSerializer
            .DeserializeAsync<ConnectionSettingsDocument>(stream, JsonOptions, ct)
            .ConfigureAwait(false);
        return document ?? new ConnectionSettingsDocument();
    }

    public async Task SaveAsync(ConnectionSettingsDocument document, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        Directory.CreateDirectory(_paths.RootDirectory);

        await using var stream = File.Create(_paths.ConnectionSettingsPath);
        await JsonSerializer.SerializeAsync(stream, document, JsonOptions, ct).ConfigureAwait(false);
    }
}
