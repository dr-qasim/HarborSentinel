using HarborSentinel.Bot.AI;
using HarborSentinel.Bot.Logging;
using HarborSentinel.Bot.Models;
using HarborSentinel.Bot.Options;
using HarborSentinel.Bot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using HarborSentinel.Bot.Hadith;

namespace HarborSentinel.Bot;

public sealed class UpdateHandler : IUpdateHandler
{
    private readonly ILogger<UpdateHandler> _log;
    private readonly IAuditSink _audit;
    private readonly IModerationService _moderation;
    private readonly IFlaggedDialogExporter _exporter;
    private readonly BotOptions _botOpt;
    private readonly IRecentMessagesCache _cache;
    private readonly IMessageStore _store;
    private readonly HadithCommandHandler _hadithHandler;

    private bool IsModerator(long? userId) =>
    userId.HasValue && _botOpt.ModeratorIds.Contains(userId.Value);

    public UpdateHandler(
        ILogger<UpdateHandler> log,
        IAuditSink audit,
        IModerationService moderation,
        IFlaggedDialogExporter exporter,
        IRecentMessagesCache cache,
        IMessageStore store,
        IOptions<BotOptions> botOpt,
        HadithCommandHandler hadithHandler)
    {
        _log = log;
        _audit = audit;
        _moderation = moderation;
        _exporter = exporter;
        _cache = cache;
        _botOpt = botOpt.Value;
        _store = store;
        _hadithHandler = hadithHandler;
    }

    public async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is { } cq)
            {
                var handledCallback = await _hadithHandler.TryHandleCallbackAsync(bot, cq, ct).ConfigureAwait(false);
                if (handledCallback)
                {
                    _log.LogInformation(
                        "Hadith callback handled: chat={Chat} msgId={Msg}",
                        cq.Message?.Chat.Id,
                        cq.Message?.MessageId);
                    return;
                }

                return;
            }

            if(update.Type == UpdateType.MyChatMember && update.MyChatMember is { } myChatMember)
            {
                await HandleMyChatMemberAsync(bot, myChatMember, ct).ConfigureAwait(false);
                return;
            }

            if (update.Type != UpdateType.Message || update.Message is null) return;
            var m = update.Message;

            var record = MessageLogRecord.FromTelegram(update, m);
            _cache.Add(record);
            _log.LogInformation("CacheAdd: chat={Chat} user={User} msgId={Msg} ts={Ts} hasText={HasText}",
            record.ChatId, record.FromId, record.MessageId, record.Timestamp, !string.IsNullOrEmpty(record.Text));

            if (m.Text is { } startText && startText.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
            {
                if (IsModerator(record.FromId))
                {
                    await bot.SendMessage(
                        chatId: m.Chat.Id,
                        text: "👋 Привет! Режим модератора включён. Бот фиксирует сообщения и уведомляет о нарушениях.",
                        cancellationToken: ct
                    );
                }
                else
                {
                    if (m.Chat.Type == ChatType.Private && _botOpt.ReplyOnlyToModerators)
                    {
                        var text = string.IsNullOrWhiteSpace(_botOpt.NonModeratorStartText)
                            ? "Ас-саляму алейкум!\n\n«Поистине, с тягостью — облегчение. Поистине, с тягостью — облегчение» (Коран 94:5–6)."
                            : _botOpt.NonModeratorStartText!;
                        await bot.SendMessage(chatId: m.Chat.Id, text: text, cancellationToken: ct);
                    }
                }
                return;
            }

            if (m.Text is { } adminText &&
                adminText.TrimStart().StartsWith("/admin_status", StringComparison.OrdinalIgnoreCase))
            {
                if (m.Chat.Type != ChatType.Private)
                    return;

                if (!IsModerator(record.FromId))
                {
                    return;
                }

                var text =
                    $"👋 Привет, {record.FromFirstName}!\n\n" +
                    "Это служебная сводка для модератора.\n\n" +
                    $"Ваш user id: {record.FromId}\n" +
                    $"Вы внесены в список модераторов бота: ✅ (всего модераторов: {_botOpt.ModeratorIds.Count})\n\n" +
                    "Бот запущен и принимает обновления.\n" +
                    "ИИ-модерация включена: новые текстовые сообщения в группах (которые бот видит) " +
                    "проходят проверку, при возможных нарушениях бот отправляет отчёты и файлы с логами модераторам.\n\n";

                await bot.SendMessage(
                    chatId: m.Chat.Id,
                    text: text,
                    cancellationToken: ct
                );

                _log.LogInformation(
                    "Admin status shown in private chat to moderator {UserId}",
                    record.FromId
                );

                return;
            }

            var handledHadith = await _hadithHandler.TryHandleAsync(bot, update, ct);
            if (handledHadith)
            {
                _log.LogInformation(
                    "Hadith command handled: chat={Chat} user={User} msgId={Msg}",
                    record.ChatId, record.FromId, record.MessageId);

                return;
            }

            var hasText = !string.IsNullOrWhiteSpace(record.Text);

            var isCommand =
                (m.Entities?.Any(e =>
                    e.Type == MessageEntityType.BotCommand &&
                    e.Offset == 0) == true)
                ||
                (hasText && record.Text!.TrimStart().StartsWith("/", StringComparison.Ordinal));

            if (hasText && !isCommand)
            {
                var verdict = await _moderation.ModerateAsync(record.Text!, ct);
                record.Ai = verdict;
            }

            await _audit.EnqueueAsync(record, ct);
            await _store.AppendAsync(record, ct);

            if (record.Ai?.Flagged == true && _botOpt.ModeratorIds.Count > 0)
            {
                await _audit.FlushAsync(ct);
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

    public Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, HandleErrorSource source, CancellationToken ct)
    {
        _log.LogError(exception, "Polling error (source={Source})", source);
        return Task.CompletedTask;
    }

    private async Task HandleMyChatMemberAsync(
    ITelegramBotClient bot,
    ChatMemberUpdated update,
    CancellationToken ct)
    {
        var chat = update.Chat;

        // Нас интересуют только группы и супергруппы
        if (chat.Type != ChatType.Group && chat.Type != ChatType.Supergroup)
            return;

        var oldStatus = update.OldChatMember.Status;
        var newStatus = update.NewChatMember.Status;

        // Был "снаружи" чата
        var wasOutside =
            oldStatus == ChatMemberStatus.Kicked ||
            oldStatus == ChatMemberStatus.Left;

        // Стал "внутри" чата
        var isInside =
            newStatus == ChatMemberStatus.Member ||
            newStatus == ChatMemberStatus.Administrator;

        // Реагируем только на момент, когда бота впервые добавили / вернули в чат
        if (!wasOutside || !isInside)
            return;

        var chatTitle = chat.Title ?? $"чат (id={chat.Id})";

        var aboutText =
            "Ас-саля̄му 'аляйкуму ва-рах̣мату -Лля̄хи ва-баракя̄туху\n\n" +
            "Спасибо, что добавили меня в эту группу. Я бот «Крепость Имана» и помогаю напоминаниями из Корана и Сунны.\n\n" +
            "Основные команды:\n" +
            "/hadith — получить хадис (случайный или по теме).\n" +
            "/hadith_topics — посмотреть доступные темы хадисов и полистать их кнопками.\n" +
            "/about — кратко рассказывает о боте и его возможностях.\n\n" +
            "Для работы команд достаточно, что бот добавлен в чат и может читать/отправлять сообщения.\n" +
            "По желанию, вы можете выдать боту минимальные права администратора (отправка сообщений, при необходимости — управление сообщениями), " +
            "если в будущем захотите, чтобы он помогал в модерации и наведении порядка в чате.";

        await bot.SendMessage(
            chatId: chat.Id,
            text: aboutText,
            cancellationToken: ct
        ).ConfigureAwait(false);

        _log.LogInformation("Bot was added to chat {ChatId} ({Title})", chat.Id, chatTitle);
    }
}