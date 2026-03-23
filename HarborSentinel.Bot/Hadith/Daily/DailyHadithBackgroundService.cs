using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegram.Bot;

namespace HarborSentinel.Bot.Hadith.Daily
{
    public sealed class DailyHadithBackgroundService : BackgroundService
    {
        private readonly ILogger<DailyHadithBackgroundService> _log;
        private readonly ITelegramBotClient _bot;
        private readonly IDailyHadithSettingsStore _store;
        private readonly IHadithProvider _hadithProvider;
        private readonly ConcurrentDictionary<long, DateOnly> _sentTodayCache = new();

        public DailyHadithBackgroundService(
            ILogger<DailyHadithBackgroundService> log,
            ITelegramBotClient bot,
            IDailyHadithSettingsStore store,
            IHadithProvider hadithProvider)
        {
            _log = log;
            _bot = bot;
            _store = store;
            _hadithProvider = hadithProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _log.LogInformation("DailyHadithBackgroundService started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // нормальное завершение
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Error in DailyHadithBackgroundService loop");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // выходим из цикла
                }
            }

            _log.LogInformation("DailyHadithBackgroundService stopped");
        }

        private async Task TickAsync(CancellationToken ct)
        {
            var nowLocal = DateTime.Now;
            var nowUtc = DateTime.UtcNow;
            var utcDay = DateOnly.FromDateTime(nowUtc.Date);

            var settings = await _store.GetAllAsync(ct).ConfigureAwait(false);
            if (settings.Count == 0)
                return;

            foreach (var s in settings)
            {
                if (ct.IsCancellationRequested)
                    break;

                if (_sentTodayCache.TryGetValue(s.ChatId, out var sentDay) && sentDay == utcDay)
                    continue;

                // Уже отправляли сегодня?
                if (s.LastSentUtc.HasValue && s.LastSentUtc.Value.Date == nowUtc.Date)
                {
                    _sentTodayCache[s.ChatId] = utcDay;
                    continue;
                }

                // Время ещё не пришло
                if (nowLocal.Hour != s.HourLocal || nowLocal.Minute != s.MinuteLocal)
                    continue;

                var hadith = await _hadithProvider.GetRandomAsync(ct).ConfigureAwait(false);
                if (hadith is null)
                    continue;

                var text =
                    "Ежедневный хадис\n\n" +
                    hadith.TextRu;

                if (!string.IsNullOrWhiteSpace(hadith.ExplanationRu))
                {
                    text += "\n\nПояснение: " + hadith.ExplanationRu;
                }

                text += HarborSentinel.Bot.Hadith.HadithTextFormatter.BuildSourceLine(hadith.Source);

                try
                {
                    await _bot.SendMessage(
                        chatId: s.ChatId,
                        text: text,
                        cancellationToken: ct
                    ).ConfigureAwait(false);

                    _sentTodayCache[s.ChatId] = utcDay;
                    s.LastSentUtc = nowUtc;
                    await _store.UpsertAsync(s, ct).ConfigureAwait(false);

                    _log.LogInformation(
                        "Daily hadith sent to chat {ChatId} at {Time}",
                        s.ChatId,
                        nowLocal);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed to send daily hadith to chat {ChatId}", s.ChatId);
                }
            }
        }
    }
}
