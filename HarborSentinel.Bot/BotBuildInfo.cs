using System.Reflection;

namespace HarborSentinel.Bot;

internal static class BotBuildInfo
{
    private static readonly Assembly Assembly = typeof(BotBuildInfo).Assembly;

    public static string Version =>
        Assembly.GetName().Version?.ToString() ?? "unknown";

    public static string InformationalVersion =>
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Version;

    public static string DisplayVersion =>
        string.IsNullOrWhiteSpace(InformationalVersion) ? Version : InformationalVersion;
}
