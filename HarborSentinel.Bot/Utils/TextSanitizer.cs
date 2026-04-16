using System;
using System.Text.RegularExpressions;

namespace HarborSentinel.Bot.Utils
{
    public static class TextSanitizer
    {
        private const string TelegramHandlePlaceholder = "@participant";
        private const string TelegramLinkPlaceholder = "[telegram_link]";
        private const string TelegramInvitePlaceholder = "[telegram_invite]";
        private const string TelegramFilePlaceholder = "[telegram_file].pdf";
        private const string UrlPlaceholder = "[url]";
        private const string IpPlaceholder = "[ip]";

        // телефоны (простая эвристика: 7–15 цифр с возможными '+', пробелами, скобками, дефисами)
        private static readonly Regex PhoneRx = new(
            @"(?<!\d)(\+?\d[\d\-\s\(\)]{6,}\d)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // email
        private static readonly Regex EmailRx = new(
            @"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // простая маска паспорт/серия-номер РФ (очень приблизительно)
        private static readonly Regex PassportRx = new(
            @"(?<!\d)(\d{2}\s?\d{2}\s?\d{6})(?!\d)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // любые длинные последовательности цифр (например, карты) — мягкая маска
        private static readonly Regex LongDigitsRx = new(
            @"(?<!\d)(\d{12,})(?!\d)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex TelegramInviteRx = new(
            @"(?<!\w)(?:https?://)?(?:t|telegram)\.me/(?:joinchat/|\+)[A-Z0-9_\-]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex TelegramLinkRx = new(
            @"(?<!\w)(?:https?://)?(?:t|telegram)\.me/[A-Z0-9_./+\-]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex UrlRx = new(
            @"(?<!\w)(?:https?://|www\.)\S+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex TelegramHandlePdfRx = new(
            @"(?<!\w)@[A-Z0-9_]{3,64}\.pdf\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex TelegramHandleRx = new(
            @"(?<![\w/])@[A-Z0-9_]{3,64}\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex Ipv4Rx = new(
            @"\b(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex WhitespaceRx = new(
            @"\s{2,}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string MaskSensitive(string? input, int maxLen = 4000)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            var s = input.Trim();

            // Маскируем
            s = EmailRx.Replace(s, "***@***");
            s = TelegramInviteRx.Replace(s, TelegramInvitePlaceholder);
            s = TelegramLinkRx.Replace(s, TelegramLinkPlaceholder);
            s = UrlRx.Replace(s, UrlPlaceholder);
            s = TelegramHandlePdfRx.Replace(s, TelegramFilePlaceholder);
            s = TelegramHandleRx.Replace(s, TelegramHandlePlaceholder);
            s = Ipv4Rx.Replace(s, IpPlaceholder);
            s = PhoneRx.Replace(s, m => MaskMiddle(m.Value));
            s = PassportRx.Replace(s, m => MaskMiddle(m.Value));
            s = LongDigitsRx.Replace(s, m => MaskMiddle(m.Value));

            // Ограничим длину — экономия токенов и таймаутов
            if (s.Length > maxLen) s = s[..maxLen];

            return s;
        }

        private static string MaskMiddle(string value)
        {
            // сохраняем первые и последние 2 символа, остальное — '*'
            if (value.Length <= 4) return new string('*', value.Length);
            return value[..2] + new string('*', value.Length - 4) + value[^2..];
        }

        public static string NormalizeReasonText(string? input, int maxLen = 320)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            var normalized = MaskSensitive(input, maxLen: Math.Max(maxLen * 2, 512))
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();

            normalized = WhitespaceRx.Replace(normalized, " ");
            if (normalized.Length > maxLen)
                normalized = normalized[..maxLen].TrimEnd() + "...";

            return normalized;
        }

        public static string CreateSafeLogSnippet(string? input, int maxLen = 220)
        {
            return NormalizeReasonText(input, maxLen);
        }
    }
}
