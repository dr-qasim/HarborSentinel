using System;
using System.Text.RegularExpressions;

namespace HarborSentinel.Bot.Utils
{
    public static class TextSanitizer
    {
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

        public static string MaskSensitive(string? input, int maxLen = 4000)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            var s = input.Trim();

            // Маскируем
            s = EmailRx.Replace(s, "***@***");
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
    }
}
