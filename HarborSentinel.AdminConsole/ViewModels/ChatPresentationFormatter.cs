using System.Globalization;

namespace HarborSentinel.AdminConsole.ViewModels;

internal static class ChatPresentationFormatter
{
    public static string TranslateChatType(string? value)
    {
        return value switch
        {
            null or "" => "нет",
            "Private" => "Личный чат",
            "Group" => "Группа",
            "Supergroup" => "Супергруппа",
            "Channel" => "Канал",
            _ => value
        };
    }

    public static string TranslateMemberStatus(string? value)
    {
        return value switch
        {
            null or "" => "нет",
            "Creator" => "Создатель",
            "Administrator" => "Администратор",
            "Member" => "Участник",
            "Restricted" => "Ограничен",
            "Left" => "Покинул",
            "Kicked" => "Исключён",
            _ => value
        };
    }

    public static string FormatNullableBool(bool? value)
    {
        return value switch
        {
            true => "да",
            false => "нет",
            null => "нет данных"
        };
    }

    public static string FormatTimestamp(DateTimeOffset? value)
    {
        return value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "нет данных";
    }

    public static string FormatNumber(long? value)
        => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "нет данных";

    public static string FormatNumber(int? value)
        => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "нет данных";

    public static string FormatScore(double? value)
        => value?.ToString("0.00", CultureInfo.InvariantCulture) ?? "нет данных";

    public static string FormatScopeStatus(bool? inCurrentBotScope)
    {
        return inCurrentBotScope switch
        {
            true => "текущий контур",
            false => "исторический контекст",
            null => "нет данных"
        };
    }

    public static string FormatActivityState(bool? isActive)
    {
        return isActive switch
        {
            true => "активен",
            false => "неактивен",
            null => "нет данных"
        };
    }
}
