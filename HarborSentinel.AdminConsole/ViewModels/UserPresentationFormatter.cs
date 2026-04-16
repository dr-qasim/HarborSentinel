namespace HarborSentinel.AdminConsole.ViewModels;

internal static class UserPresentationFormatter
{
    public static string BuildDisplayName(string? firstName, string? lastName, string? username, long userId)
    {
        var fullName = string.Join(" ", new[] { firstName, lastName }.Where(static part => !string.IsNullOrWhiteSpace(part)));
        if (!string.IsNullOrWhiteSpace(fullName))
            return fullName;

        if (!string.IsNullOrWhiteSpace(username))
            return $"@{username}";

        return $"Пользователь {userId}";
    }

    public static string FormatUsername(string? username)
        => string.IsNullOrWhiteSpace(username) ? "нет" : $"@{username}";

    public static string FormatPrivateStart(bool hasPrivateStart, int? startCount)
    {
        if (!hasPrivateStart)
            return "нет";

        if (startCount is > 1)
            return $"да, запусков: {ChatPresentationFormatter.FormatNumber(startCount)}";

        return "да";
    }

    public static string FormatPhonePresence(bool hasPhone)
        => hasPhone ? "сохранён" : "нет";
}
