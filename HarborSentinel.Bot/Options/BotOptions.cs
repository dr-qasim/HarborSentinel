using System;


namespace HarborSentinel.Bot.Options
{
    public sealed class BotOptions
    {
        /// <summary>Список Telegram ID модераторов, которым слать уведомления.</summary>
        public List<long> ModeratorIds { get; set; } = new();
        public bool ReplyOnlyToModerators { get; set; } = true;
        public string? NonModeratorStartText { get; set; }

    }
}
