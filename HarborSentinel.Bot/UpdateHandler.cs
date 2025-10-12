// UpdateHandler.cs
using HarborSentinel.Bot.AI;
using HarborSentinel.Bot.Logging;
using HarborSentinel.Bot.Models;
using HarborSentinel.Bot.Options;
using HarborSentinel.Bot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;          // важный using
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace HarborSentinel.Bot;

public sealed class UpdateHandler : IUpdateHandler
{
    private readonly ILogger<UpdateHandler> _log;
    private readonly IAuditSink _audit;
    private readonly IModerationService _moderation;
    private readonly IFlaggedDialogExporter _exporter;
    private readonly BotOptions _botOpt;
    private readonly IRecentMessagesCache _cache;

    public UpdateHandler(
        ILogger<UpdateHandler> log,
        IAuditSink audit,
        IModerationService moderation,
        IFlaggedDialogExporter exporter,
        IRecentMessagesCache cache,
        IOptions<BotOptions> botOpt)
    {
        _log = log;
        _audit = audit;
        _moderation = moderation;
        _exporter = exporter;
        _cache = cache;
        _botOpt = botOpt.Value;
    }

    public async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            if (update.Type != UpdateType.Message || update.Message is null) return;
            var m = update.Message;

            var record = MessageLogRecord.FromTelegram(update, m);
            _cache.Add(record); // кладём в скользящее окно (до модерации/записи на диск)
            _log.LogInformation("CacheAdd: chat={Chat} user={User} msgId={Msg} ts={Ts} hasText={HasText}",
            record.ChatId, record.FromId, record.MessageId, record.Timestamp, !string.IsNullOrEmpty(record.Text));

            if (m.Text is { } startText && startText.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
            {
                await bot.SendMessage(
                    chatId: m.Chat.Id,
                    text: "👋 Привет! Я фиксирую сообщения и проверяю их с помощью ИИ. При нарушениях уведомляю модераторов.",
                    cancellationToken: ct
                );
                return;
            }

            if (!string.IsNullOrWhiteSpace(record.Text))
            {
                var verdict = await _moderation.ModerateAsync(record.Text!, ct);
                record.Ai = verdict;
            }

            await _audit.EnqueueAsync(record, ct);

            if (record.Ai?.Flagged == true && _botOpt.ModeratorIds.Count > 0)
            {
                await _audit.FlushAsync(ct); // дождёмся, пока всё, что в очереди, точно окажется в файле
                var filePath = await _exporter.ExportAsync(record, record.ChatId, daysBack: 14, ct);

                var caption =
                    "⚠️ Обнаружено возможное нарушение законодательства РФ\n" +
                    $"Чат: {record.ChatTitle ?? record.ChatUsername ?? record.ChatId.ToString()} (id={record.ChatId})\n" +
                    $"Пользователь: {record.FromFirstName} {record.FromLastName} @{record.FromUsername} (id={record.FromId})\n" +
                    $"Вердикт: {record.Ai?.Label}  уверенность: {(record.Ai?.Confidence ?? 0):P0}\n" +
                    $"Причина: {record.Ai?.Reason}";

                await using var fs = System.IO.File.OpenRead(filePath);
                var doc = Telegram.Bot.Types.InputFile.FromStream(fs, System.IO.Path.GetFileName(filePath));

                foreach (var modId in _botOpt.ModeratorIds)
                {
                    try
                    {
                        await bot.SendDocument(
                            chatId: modId,
                            document: doc,
                            caption: caption,
                            cancellationToken: ct
                        );
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Failed to notify moderator {ModId}", modId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error while handling update");
        }
    }

    // ✅ правильная сигнатура для v22
    public Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, HandleErrorSource source, CancellationToken ct)
    {
        _log.LogError(exception, "Polling error (source={Source})", source);
        return Task.CompletedTask;
    }
}
