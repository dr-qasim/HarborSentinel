using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarborSentinel.AdminConsole.Services;

public sealed class SshTunnelProfileStore : ISshTunnelProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IAdminConsoleStoragePaths _paths;

    public SshTunnelProfileStore(IAdminConsoleStoragePaths paths)
    {
        _paths = paths;
    }

    public async Task<SshTunnelProfilesDocument> LoadAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.RootDirectory);
        if (!File.Exists(_paths.SshTunnelProfilesPath))
            return new SshTunnelProfilesDocument();

        await using var stream = File.OpenRead(_paths.SshTunnelProfilesPath);
        var document = await JsonSerializer.DeserializeAsync<SshTunnelProfilesDocument>(stream, JsonOptions, ct).ConfigureAwait(false);
        return document ?? new SshTunnelProfilesDocument();
    }

    public async Task SaveAsync(SshTunnelProfilesDocument document, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        Directory.CreateDirectory(_paths.RootDirectory);

        await using var stream = File.Create(_paths.SshTunnelProfilesPath);
        await JsonSerializer.SerializeAsync(stream, document, JsonOptions, ct).ConfigureAwait(false);
    }
}
