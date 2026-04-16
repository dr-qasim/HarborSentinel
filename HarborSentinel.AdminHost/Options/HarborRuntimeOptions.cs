namespace HarborSentinel.AdminHost.Options;

public sealed class HarborRuntimeOptions
{
    public string DatabasePath { get; set; } = "data/harborsentinel.db";
    public string StorageBasePath { get; set; } = "data";
    public string TrackedExportsDir { get; set; } = "tracked_exports";
    public string AdminExportsDir { get; set; } = "admin_exports";
    public string AdminStateDir { get; set; } = "adminhost_state";
}
