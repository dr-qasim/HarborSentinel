using System;

namespace HarborSentinel.Bot.Hadith.Daily
{
    public sealed class DailyHadithSettings
    {
        /// <summary>Идентификатор чата (group / supergroup).</summary>
        public long ChatId { get; set; }

        /// <summary>Часы локального времени сервера (0–23).</summary>
        public int HourLocal { get; set; }

        /// <summary>Минуты локального времени сервера (0–59).</summary>
        public int MinuteLocal { get; set; }

        /// <summary>Когда последний раз отправляли ежедневный хадис (UTC).</summary>
        public DateTime? LastSentUtc { get; set; }
    }
}
