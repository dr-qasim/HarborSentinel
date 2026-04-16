using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HarborSentinel.AdminConsole.Services;

public sealed class DpapiTunnelSecretStore : ITunnelSecretStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IAdminConsoleStoragePaths _paths;

    public DpapiTunnelSecretStore(IAdminConsoleStoragePaths paths)
    {
        _paths = paths;
    }

    public async Task<string?> GetSecretAsync(string profileId, SshTunnelSecretKind kind, CancellationToken ct)
    {
        var document = await LoadDocumentAsync(ct).ConfigureAwait(false);
        if (!document.EncryptedSecrets.TryGetValue(BuildKey(profileId, kind), out var protectedValue) ||
            string.IsNullOrWhiteSpace(protectedValue))
        {
            return null;
        }

        var encryptedBytes = Convert.FromBase64String(protectedValue);
        var bytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    public async Task SaveSecretAsync(string profileId, SshTunnelSecretKind kind, string secret, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            await DeleteSecretAsync(profileId, kind, ct).ConfigureAwait(false);
            return;
        }

        var document = await LoadDocumentAsync(ct).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(secret);
        var encryptedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        document.EncryptedSecrets[BuildKey(profileId, kind)] = Convert.ToBase64String(encryptedBytes);
        await SaveDocumentAsync(document, ct).ConfigureAwait(false);
    }

    public async Task DeleteSecretAsync(string profileId, SshTunnelSecretKind kind, CancellationToken ct)
    {
        var document = await LoadDocumentAsync(ct).ConfigureAwait(false);
        if (document.EncryptedSecrets.Remove(BuildKey(profileId, kind)))
            await SaveDocumentAsync(document, ct).ConfigureAwait(false);
    }

    public async Task<bool> HasSecretAsync(string profileId, SshTunnelSecretKind kind, CancellationToken ct)
    {
        var document = await LoadDocumentAsync(ct).ConfigureAwait(false);
        return document.EncryptedSecrets.ContainsKey(BuildKey(profileId, kind));
    }

    private async Task<TunnelSecretDocument> LoadDocumentAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.RootDirectory);
        if (!File.Exists(_paths.SshTunnelSecretsPath))
            return new TunnelSecretDocument();

        await using var stream = File.OpenRead(_paths.SshTunnelSecretsPath);
        var document = await JsonSerializer.DeserializeAsync<TunnelSecretDocument>(stream, JsonOptions, ct).ConfigureAwait(false);
        return document ?? new TunnelSecretDocument();
    }

    private async Task SaveDocumentAsync(TunnelSecretDocument document, CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.RootDirectory);
        await using var stream = File.Create(_paths.SshTunnelSecretsPath);
        await JsonSerializer.SerializeAsync(stream, document, JsonOptions, ct).ConfigureAwait(false);
    }

    private static string BuildKey(string profileId, SshTunnelSecretKind kind)
        => $"{profileId}:{kind}";
}
