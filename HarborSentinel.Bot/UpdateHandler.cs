using HarborSentinel.Bot.AI;
using HarborSentinel.Bot.Hadith;
using HarborSentinel.Bot.Hadith.Daily;
using HarborSentinel.Bot.Islamic;
using HarborSentinel.Bot.Logging;
using HarborSentinel.Bot.Models;
using HarborSentinel.Bot.Options;
using HarborSentinel.Bot.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using System.Net;
using System.Text.RegularExpressions;

namespace HarborSentinel.Bot;

public sealed class UpdateHandler : IUpdateHandler
{
    private const int PersistenceRetryAttempts = 5;
    private const int PersistenceRetryBaseDelayMs = 100;
    private static readonly TimeSpan TrackedStateCacheTtl = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan WelcomeSendCooldown = TimeSpan.FromSeconds(45);
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };
    private static readonly Regex PrivateAdminCopyableTokenRegex = new(
        @"\[id=-?\d{5,}\]|(?:\b(?:chat|user|id|phone)=)(?:@?[A-Za-z0-9_+\-]{3,}|-?\d{5,})|/[A-Za-z0-9_:@.\-]+|@[A-Za-z0-9_]{3,}|(?<![\w])[-+]?\d{5,}(?![\w])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILogger<UpdateHandler> _log;
    private readonly IAuditSink _audit;
    private readonly IModerationService _moderation;
    private readonly IFlaggedDialogExporter _exporter;
    private readonly ITrackedChatExportService _trackedChatExport;
    private readonly IModeratorDeliverySink _moderatorDelivery;
    private readonly BotOptions _botOpt;
    private readonly ModerationOptions _moderationOpt;
    private readonly IRecentMessagesCache _cache;
    private readonly IMessageStore _store;
    private readonly HadithCommandHandler _hadithHandler;
    private readonly IDailyHadithSettingsStore _dailySettings;
    private readonly IIslamicContentProvider _islamicProvider;
    private readonly IChatRegistry _chatRegistry;
    private readonly string _emergencyBackupPath;
    private readonly string _adminExportsPath;
    private readonly object _backupFileSync = new();
    private readonly ConcurrentDictionary<long, TrackedStateCacheItem> _trackedStateCache = new();
    private readonly ConcurrentDictionary<long, DateTimeOffset> _recentWelcomeByChat = new();
    private string? _cachedBotUsername;


    private bool IsModerator(long? userId) =>
    userId.HasValue && _botOpt.ModeratorIds.Contains(userId.Value);

    public UpdateHandler(
        ILogger<UpdateHandler> log,
        IAuditSink audit,
        IModerationService moderation,
        IFlaggedDialogExporter exporter,
        ITrackedChatExportService trackedChatExport,
        IModeratorDeliverySink moderatorDelivery,
        IRecentMessagesCache cache,
        IMessageStore store,
        IChatRegistry chatRegistry,
        IOptions<BotOptions> botOpt,
        IOptions<ModerationOptions> moderationOpt,
        IOptions<StorageOptions> storageOpt,
        IDailyHadithSettingsStore dailySettings,
        IIslamicContentProvider islamicProvider,
        HadithCommandHandler hadithHandler)
    {
        _log = log;
        _audit = audit;
        _moderation = moderation;
        _exporter = exporter;
        _trackedChatExport = trackedChatExport;
        _moderatorDelivery = moderatorDelivery;
        _cache = cache;
        _botOpt = botOpt.Value;
        _moderationOpt = moderationOpt.Value;
        _store = store;
        _chatRegistry = chatRegistry;
        _dailySettings = dailySettings;
        _islamicProvider = islamicProvider;
        _hadithHandler = hadithHandler;

        var basePath = storageOpt.Value.BasePath;
        if (string.IsNullOrWhiteSpace(basePath))
            basePath = "data";

        var fullBasePath = Path.GetFullPath(basePath);
        Directory.CreateDirectory(fullBasePath);
        _emergencyBackupPath = Path.Combine(fullBasePath, "messages-emergency-backup.jsonl");

        var adminExportsDir = storageOpt.Value.AdminExportsDir;
        if (string.IsNullOrWhiteSpace(adminExportsDir))
            adminExportsDir = "admin_exports";

        _adminExportsPath = Path.Combine(fullBasePath, adminExportsDir);
        Directory.CreateDirectory(_adminExportsPath);
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

            if (update.Type == UpdateType.ChatMember && update.ChatMember is { } chatMember)
            {
                await HandleChatMemberAsync(chatMember, ct).ConfigureAwait(false);
                return;
            }

            if (update.Type != UpdateType.Message || update.Message is null) return;
            var m = update.Message;
            var textCommand = m.Text is { } rawText ? ExtractCommandName(rawText) : null;

            var record = MessageLogRecord.FromTelegram(update, m);
            _cache.Add(record);
            _log.LogInformation("CacheAdd: chat={Chat} user={User} msgId={Msg} ts={Ts} hasText={HasText}",
            record.ChatId, record.FromId, record.MessageId, record.Timestamp, !string.IsNullOrEmpty(record.Text));
            await TrackChatFromMessageAsync(m, ToUtcDateTimeOffset(m.Date), ct).ConfigureAwait(false);
            await TryTrackParticipantFromMessageAsync(m, record, ct).ConfigureAwait(false);

            if (m.Chat.Type == ChatType.Private &&
                m.Text is not null &&
                string.Equals(textCommand, "/start", StringComparison.OrdinalIgnoreCase))
            {
                await TryRegisterPrivateStartAsync(record, m, ct).ConfigureAwait(false);
                await SendPrivateStartAsync(bot, m, record, ct).ConfigureAwait(false);
                return;
            }

            if (m.Chat.Type == ChatType.Private)
            {
                await TryUpdatePrivateStartProfileFromContactAsync(record, m, ct).ConfigureAwait(false);

                if (m.Text is null)
                    return;

                var privateCommand = textCommand;
                var isStartCommand = string.Equals(privateCommand, "/start", StringComparison.OrdinalIgnoreCase);
                if (!isStartCommand)
                {
                    if (!IsModerator(record.FromId))
                        return;

                    var isDisabledExportCommand = IsDisabledExportCommand(privateCommand);
                    var isAdminCommand =
                        string.Equals(privateCommand, "/admin_status", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(privateCommand, "/admin_groups", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(privateCommand, "/admin_tracked", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(privateCommand, "/admin_track", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(privateCommand, "/admin_untrack", StringComparison.OrdinalIgnoreCase) ||
                        isDisabledExportCommand ||
                        string.Equals(privateCommand, "/admin_participants", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(privateCommand, "/admin_flagged_users", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(privateCommand, "/admin_user_chats", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(privateCommand, "/admin_started_users", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(privateCommand, "/admin_health", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(privateCommand, "/admin_chat_summary", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(privateCommand, "/admin_incidents", StringComparison.OrdinalIgnoreCase);

                    if (!isAdminCommand)
                        return;
                }
            }

            // Приветствуем новых участников (если есть)
            if (m.NewChatMembers is { Length: > 0 })
            {
                await HandleNewChatMembersAsync(bot, m, ct).ConfigureAwait(false);
            }

            if (m.Text is not null && IsDisabledExportCommand(textCommand))
            {
                if (m.Chat.Type != ChatType.Private)
                    return;

                if (!IsModerator(record.FromId))
                    return;

                await SendDisabledExportSurfaceMessageAsync(bot, m.Chat.Id, ct).ConfigureAwait(false);
                return;
            }

            if (m.Text is not null &&
                string.Equals(textCommand, "/admin_status", StringComparison.OrdinalIgnoreCase))
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
                    $"Версия бота: {BotBuildInfo.DisplayVersion}\n" +
                    $"Ваш user id: {record.FromId}\n" +
                    $"Вы внесены в список модераторов бота: ✅ (всего модераторов: {_botOpt.ModeratorIds.Count})\n\n" +
                    "Бот запущен.\n" +
                    "Служебный контур активен, уведомления и рабочие материалы доставляются модераторам.\n\n";

                await SendPrivateAdminMessageAsync(bot, m.Chat.Id, text, ct);

                _log.LogInformation(
                    "Admin status shown in private chat to moderator {UserId}",
                    record.FromId
                );

                return;
            }

            if (m.Text is { } groupsText &&
                string.Equals(textCommand, "/admin_groups", StringComparison.OrdinalIgnoreCase))
            {
                if (m.Chat.Type != ChatType.Private)
                    return;

                if (!IsModerator(record.FromId))
                    return;

                await HandleAdminGroupsAsync(bot, m, groupsText, ct).ConfigureAwait(false);
                return;
            }

            if (m.Text is { } trackedText &&
                string.Equals(textCommand, "/admin_tracked", StringComparison.OrdinalIgnoreCase))
            {
                if (m.Chat.Type != ChatType.Private)
                    return;

                if (!IsModerator(record.FromId))
                    return;

                await HandleAdminTrackedAsync(bot, m, trackedText, ct).ConfigureAwait(false);
                return;
            }

            if (m.Text is { } trackText &&
                string.Equals(textCommand, "/admin_track", StringComparison.OrdinalIgnoreCase))
            {
                if (m.Chat.Type != ChatType.Private)
                    return;

                if (!IsModerator(record.FromId))
                    return;

                await HandleAdminTrackToggleAsync(bot, m, trackText, isTracked: true, ct).ConfigureAwait(false);
                return;
            }

            if (m.Text is { } untrackText &&
                string.Equals(textCommand, "/admin_untrack", StringComparison.OrdinalIgnoreCase))
            {
                if (m.Chat.Type != ChatType.Private)
                    return;

                if (!IsModerator(record.FromId))
                    return;

                await HandleAdminTrackToggleAsync(bot, m, untrackText, isTracked: false, ct).ConfigureAwait(false);
                return;
            }

            if (m.Text is { } participantsText &&
                string.Equals(textCommand, "/admin_participants", StringComparison.OrdinalIgnoreCase))
            {
                if (m.Chat.Type != ChatType.Private)
                    return;

                if (!IsModerator(record.FromId))
                    return;

                await HandleAdminParticipantsAsync(bot, m, participantsText, ct).ConfigureAwait(false);
                return;
            }

            if (m.Text is { } flaggedUsersText &&
                string.Equals(textCommand, "/admin_flagged_users", StringComparison.OrdinalIgnoreCase))
            {
                if (m.Chat.Type != ChatType.Private)
                    return;

                if (!IsModerator(record.FromId))
                    return;

                await HandleAdminFlaggedUsersAsync(bot, m, flaggedUsersText, ct).ConfigureAwait(false);
                return;
            }

            if (m.Text is { } userChatsText &&
                string.Equals(textCommand, "/admin_user_chats", StringComparison.OrdinalIgnoreCase))
            {
                if (m.Chat.Type != ChatType.Private)
                    return;

                if (!IsModerator(record.FromId))
                    return;

                await HandleAdminUserChatsAsync(bot, m, userChatsText, ct).ConfigureAwait(false);
                return;
            }

            if (m.Text is { } startedUsersText &&
                string.Equals(textCommand, "/admin_started_users", StringComparison.OrdinalIgnoreCase))
            {
                if (m.Chat.Type != ChatType.Private)
                    return;

                if (!IsModerator(record.FromId))
                    return;

                await HandleAdminStartedUsersAsync(bot, m, startedUsersText, ct).ConfigureAwait(false);
                return;
            }

            if (m.Text is { } healthText &&
                string.Equals(textCommand, "/admin_health", StringComparison.OrdinalIgnoreCase))
            {
                if (m.Chat.Type != ChatType.Private)
                    return;

                if (!IsModerator(record.FromId))
                    return;

                await HandleAdminHealthAsync(bot, m, ct).ConfigureAwait(false);
                return;
            }

            if (m.Text is { } chatSummaryText &&
                string.Equals(textCommand, "/admin_chat_summary", StringComparison.OrdinalIgnoreCase))
            {
                if (m.Chat.Type != ChatType.Private)
                    return;

                if (!IsModerator(record.FromId))
                    return;

                await HandleAdminChatSummaryAsync(bot, m, chatSummaryText, ct).ConfigureAwait(false);
                return;
            }

            if (m.Text is { } incidentsText &&
                string.Equals(textCommand, "/admin_incidents", StringComparison.OrdinalIgnoreCase))
            {
                if (m.Chat.Type != ChatType.Private)
                    return;

                if (!IsModerator(record.FromId))
                    return;

                await HandleAdminIncidentsAsync(bot, m, incidentsText, ct).ConfigureAwait(false);
                return;
            }

            // Ежедневный хадис: /iman_daily_on и /iman_daily_off
            if (m.Text is { } dailyText &&
                (string.Equals(textCommand, "/iman_daily_on", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(textCommand, "/iman_daily_off", StringComparison.OrdinalIgnoreCase)))
            {
                // Работает только в группах/супергруппах
                if (m.Chat.Type != ChatType.Group && m.Chat.Type != ChatType.Supergroup)
                    return;

                // Право включать/выключать есть у любого админа чата (но не у обычных участников)
                var isAdmin = await IsChatAdminAsync(bot, m.Chat.Id, record.FromId, ct).ConfigureAwait(false);
                if (!isAdmin)
                    return;

                var trimmed = dailyText.Trim();

                // Отключение: /iman_daily_off
                if (trimmed.StartsWith("/iman_daily_off", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await _dailySettings.DeleteAsync(record.ChatId, ct).ConfigureAwait(false);

                        await bot.SendMessage(
                            chatId: m.Chat.Id,
                            text: "Ежедневное напоминание хадисом в этом чате отключено.",
                            cancellationToken: ct
                        ).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Failed to disable daily hadith for chat {ChatId}", record.ChatId);
                        await bot.SendMessage(
                            chatId: m.Chat.Id,
                            text: "Не удалось отключить ежедневное напоминание. Повторите команду позже.",
                            cancellationToken: ct
                        ).ConfigureAwait(false);
                    }

                    return;
                }

                // Включение: /iman_daily_on [HH:mm]
                string? timePart = null;
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2)
                {
                    timePart = parts[1];
                }

                int hour = 9;
                int minute = 0;

                if (!string.IsNullOrEmpty(timePart))
                {
                    if (!TryParseTime(timePart, out hour, out minute))
                    {
                        await bot.SendMessage(
                            chatId: m.Chat.Id,
                            text: "Не понял время. Укажите его в формате ЧЧ:ММ, например:\n" +
                                  "/iman_daily_on 09:00",
                            cancellationToken: ct
                        ).ConfigureAwait(false);
                        return;
                    }
                }

                hour = Math.Clamp(hour, 0, 23);
                minute = Math.Clamp(minute, 0, 59);

                var settings = await _dailySettings.GetForChatAsync(record.ChatId, ct).ConfigureAwait(false)
                               ?? new DailyHadithSettings { ChatId = record.ChatId };

                settings.HourLocal = hour;
                settings.MinuteLocal = minute;

                try
                {
                    await _dailySettings.UpsertAsync(settings, ct).ConfigureAwait(false);

                    await bot.SendMessage(
                        chatId: m.Chat.Id,
                        text: $"Ежедневное напоминание хадисом включено для этого чата.\n" +
                              $"Время (по серверу): {hour:00}:{minute:00}.",
                        cancellationToken: ct
                    ).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed to enable daily hadith for chat {ChatId}", record.ChatId);
                    await bot.SendMessage(
                        chatId: m.Chat.Id,
                        text: "Не удалось включить ежедневное напоминание. Повторите команду позже.",
                        cancellationToken: ct
                    ).ConfigureAwait(false);
                }

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
            ChatVigilanceState? chatVigilanceState = null;

            var isCommand =
                (m.Entities?.Any(e =>
                    e.Type == MessageEntityType.BotCommand &&
                    e.Offset == 0) == true)
                ||
                (hasText && record.Text!.TrimStart().StartsWith("/", StringComparison.Ordinal));

            if (hasText && !isCommand)
            {
                chatVigilanceState = await TryGetChatVigilanceStateAsync(record, ct).ConfigureAwait(false);
                var verdict = _moderationOpt.ContextEnabled
                    ? await _moderation.ModerateAsync(await BuildModerationRequestAsync(record, m, chatVigilanceState, ct).ConfigureAwait(false), ct).ConfigureAwait(false)
                    : await _moderation.ModerateAsync(record.Text!, ct).ConfigureAwait(false);
                record.Ai = verdict;
            }

            var riskContribution = CalculateConversationRiskContribution(record.Ai, chatVigilanceState);
            var persistToDatabase = await ShouldPersistToDatabaseAsync(record, m, ct).ConfigureAwait(false);
            var persistToAudit = await ShouldPersistToAuditAsync(record, m, ct).ConfigureAwait(false);
            await PersistRecordReliablyAsync(record, persistToDatabase, persistToAudit, riskContribution, ct).ConfigureAwait(false);

            var conversationRisk = await TryProcessConversationRiskAsync(record, riskContribution, ct).ConfigureAwait(false);
            await TryRefreshChatVigilanceAsync(record, conversationRisk.Snapshot, chatVigilanceState, ct).ConfigureAwait(false);
            if (record.Ai is not null && conversationRisk.Snapshot is not null)
            {
                record.Ai = EnrichVerdictWithConversationRisk(record.Ai, conversationRisk.Snapshot);
            }

            if (record.Ai?.Flagged == true && _botOpt.ModeratorIds.Count > 0)
            {
                await _audit.FlushAsync(ct).ConfigureAwait(false);
                var notification = BuildFlaggedModeratorDeliveryEvent(record, conversationRisk.Snapshot);
                await TryPublishModeratorDeliveryAsync(notification, ct).ConfigureAwait(false);
            }
            else if (conversationRisk.ShouldNotify && _botOpt.ModeratorIds.Count > 0)
            {
                await _audit.FlushAsync(ct).ConfigureAwait(false);
                var notification = BuildConversationRiskModeratorDeliveryEvent(record, conversationRisk.Snapshot);
                await TryPublishModeratorDeliveryAsync(notification, ct).ConfigureAwait(false);
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

    private async Task<bool> ShouldPersistToDatabaseAsync(
        MessageLogRecord record,
        Message message,
        CancellationToken ct)
    {
        if (message.Chat.Type != ChatType.Group && message.Chat.Type != ChatType.Supergroup)
            return true;

        var tracked = await IsTrackedChatAsync(record.ChatId, ct).ConfigureAwait(false);
        if (!tracked)
        {
            _log.LogDebug(
                "SQLite append skipped for untracked chat {ChatId} message {MessageId}",
                record.ChatId,
                record.MessageId);
        }

        return tracked;
    }

    private async Task<bool> ShouldPersistToAuditAsync(
        MessageLogRecord record,
        Message message,
        CancellationToken ct)
    {
        if (message.Chat.Type != ChatType.Group && message.Chat.Type != ChatType.Supergroup)
            return true;

        return await IsTrackedChatAsync(record.ChatId, ct).ConfigureAwait(false);
    }

    private async Task<bool> IsTrackedChatAsync(long chatId, CancellationToken ct)
    {
        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_trackedStateCache.TryGetValue(chatId, out var cached) &&
            nowUnixMs - cached.ResolvedAtUnixMs <= TrackedStateCacheTtl.TotalMilliseconds)
        {
            return cached.IsTracked;
        }

        try
        {
            var entry = await _chatRegistry.GetByChatIdAsync(chatId, ct).ConfigureAwait(false);
            var isTracked = entry?.IsTracked == true;
            UpdateTrackedStateCache(chatId, isTracked);
            return isTracked;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Failed to resolve tracked status for chat {ChatId}. Skipping persistence for privacy safety.",
                chatId);
            return false;
        }
    }

    private void UpdateTrackedStateCache(long chatId, bool isTracked)
    {
        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _trackedStateCache[chatId] = new TrackedStateCacheItem(isTracked, nowUnixMs);
    }

    private async Task PersistRecordReliablyAsync(
        MessageLogRecord record,
        bool persistToDatabase,
        bool persistToAudit,
        double riskContribution,
        CancellationToken ct)
    {
        try
        {
            if (persistToDatabase)
            {
                await ExecuteWithRetryAsync(
                    operationName: "sqlite_append",
                    action: retryCt => _store.AppendAsync(record, retryCt),
                    ct).ConfigureAwait(false);
            }

            if (record.Ai?.Flagged == true)
            {
                await ExecuteWithRetryAsync(
                    operationName: "sqlite_flagged_event",
                    action: retryCt => _store.RecordFlaggedEventAsync(record, retryCt),
                    ct).ConfigureAwait(false);
            }

            if (riskContribution > 0d)
            {
                await ExecuteWithRetryAsync(
                    operationName: "sqlite_risk_event",
                    action: retryCt => _store.RecordRiskEventAsync(record, riskContribution, retryCt),
                    ct).ConfigureAwait(false);
            }

            if (persistToAudit)
            {
                await ExecuteWithRetryAsync(
                    operationName: "audit_enqueue",
                    action: retryCt => _audit.EnqueueAsync(record, retryCt).AsTask(),
                    ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            TryWriteEmergencyBackup(record, ex);
            throw;
        }
    }

    private async Task ExecuteWithRetryAsync(
        string operationName,
        Func<CancellationToken, Task> action,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= PersistenceRetryAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await action(ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < PersistenceRetryAttempts)
            {
                var delayMs = PersistenceRetryBaseDelayMs * (1 << (attempt - 1));
                _log.LogWarning(
                    ex,
                    "Persistence operation {Operation} failed (attempt {Attempt}/{MaxAttempts}); retry in {DelayMs} ms",
                    operationName,
                    attempt,
                    PersistenceRetryAttempts,
                    delayMs);

                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex,
                    "Persistence operation {Operation} failed after {MaxAttempts} attempts",
                    operationName,
                    PersistenceRetryAttempts);
                throw;
            }
        }
    }

    private void TryWriteEmergencyBackup(MessageLogRecord record, Exception ex)
    {
        try
        {
            var backup = new EmergencyBackupRecord
            {
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Error = ex.Message,
                Message = record
            };

            var line = JsonSerializer.Serialize(backup);
            lock (_backupFileSync)
            {
                File.AppendAllText(_emergencyBackupPath, line + Environment.NewLine, Encoding.UTF8);
            }

            _log.LogCritical(
                "Message persisted to emergency backup at {Path}; chat={ChatId} msg={MessageId}",
                _emergencyBackupPath,
                record.ChatId,
                record.MessageId);
        }
        catch (Exception backupEx)
        {
            _log.LogCritical(
                backupEx,
                "Emergency backup write failed; chat={ChatId} msg={MessageId}",
                record.ChatId,
                record.MessageId);
        }
    }

    private sealed class EmergencyBackupRecord
    {
        public required DateTimeOffset CapturedAtUtc { get; init; }
        public required string Error { get; init; }
        public required MessageLogRecord Message { get; init; }
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

        await TrackChatFromMyChatMemberAsync(update, ct).ConfigureAwait(false);

        var oldStatus = update.OldChatMember.Status;
        var newStatus = update.NewChatMember.Status;
        var wasInside = IsInsideStatus(oldStatus);
        var isInside = IsInsideStatus(newStatus);

        if (wasInside && !isInside)
        {
            await NotifyModeratorsAboutBotRemovedAsync(bot, update, ct).ConfigureAwait(false);
            _log.LogInformation(
                "Bot was removed from chat {ChatId}; oldStatus={OldStatus}, newStatus={NewStatus}",
                chat.Id,
                oldStatus,
                newStatus);
            return;
        }

        // Реагируем только на момент, когда бота впервые добавили / вернули в чат
        if (wasInside || !isInside)
            return;

        await NotifyModeratorsAboutBotAddedAsync(bot, update, ct).ConfigureAwait(false);

        var chatTitle = chat.Title ?? $"чат (id={chat.Id})";

        var aboutText =
            "Ас-саля̄му 'аляйкуму ва-рах̣мату -Лля̄хи ва-баракя̄туху\n\n" +
            "Спасибо, что добавили меня в эту группу. Я — бот «Крепость Имана».\n" +
            "По воле Аллаха, могу стать здесь причиной пользы, добрых слов и бараката — через дуа, азкары, хадисы, аяты и спокойные напоминания.\n\n" +
            "Основные команды:\n" +
            "/hadith — получить хадис. Пример по теме: /hadith терпение.\n" +
            "/hadith_topics — посмотреть доступные темы хадисов и полистать их кнопками.\n" +
            "/dua — получить дуа. Пример по теме: /dua знания.\n" +
            "/dua_topics — посмотреть темы дуа и выбрать подходящую.\n" +
            "/azkar — получить азкары. Пример по теме: /azkar утро.\n" +
            "/azkar_topics — посмотреть темы азкаров.\n" +
            "/ayat_day — получить аят дня.\n" +
            "/today — получить короткую пользу на сегодня: аят, дуа и азкар.\n" +
            "/help_me — подобрать дуа и азкары по состоянию. Пример: /help_me тревога.\n" +
            "/about — кратко рассказывает о боте и его возможностях.\n\n" +
            "Стремление к знаниям — путь к укреплению имана.\n" +
            "Пусть эта беседа будет местом пользы, знания и благого напоминания.";

        await bot.SendMessage(
            chatId: chat.Id,
            text: aboutText,
            cancellationToken: ct
        ).ConfigureAwait(false);

        _log.LogInformation("Bot was added to chat {ChatId} ({Title})", chat.Id, chatTitle);
    }

    private async Task NotifyModeratorsAboutBotAddedAsync(
        ITelegramBotClient bot,
        ChatMemberUpdated update,
        CancellationToken ct)
    {
        if (_botOpt.ModeratorIds.Count == 0)
            return;

        var chat = update.Chat;
        var chatDisplay = BuildChatDisplay(chat);
        var chatLink = BuildChatLink(chat.Username);
        var actorDisplay = BuildActorDisplay(update.From);
        var actorLink = BuildActorLink(update.From);

        var eventUtc = update.Date.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(update.Date, DateTimeKind.Utc)
            : update.Date.ToUniversalTime();

        var sb = new StringBuilder();
        sb.Append("🆕 Бота добавили в группу\n");
        sb.Append("Когда: ");
        sb.Append(eventUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"));
        sb.Append('\n');
        sb.Append("Куда: ");
        sb.Append(chatDisplay);
        sb.Append(" (id=");
        sb.Append(chat.Id);
        sb.Append(", type=");
        sb.Append(chat.Type);
        sb.Append(")\n");

        if (!string.IsNullOrWhiteSpace(chatLink))
        {
            sb.Append("Ссылка на чат: ");
            sb.Append(chatLink);
            sb.Append('\n');
        }

        sb.Append("Кто добавил: ");
        sb.Append(actorDisplay);

        if (!string.IsNullOrWhiteSpace(actorLink))
        {
            sb.Append("\nПрофиль: ");
            sb.Append(actorLink);
        }

        var text = TruncateText(sb.ToString(), 3500);
        await TrySendModeratorNotificationAsync(
            bot,
            text,
            "bot-added event",
            ct).ConfigureAwait(false);
    }

    private async Task NotifyModeratorsAboutBotRemovedAsync(
        ITelegramBotClient bot,
        ChatMemberUpdated update,
        CancellationToken ct)
    {
        if (_botOpt.ModeratorIds.Count == 0)
            return;

        var chat = update.Chat;
        var chatDisplay = BuildChatDisplay(chat);
        var chatLink = BuildChatLink(chat.Username);
        var actorDisplay = BuildActorDisplay(update.From);
        var actorLink = BuildActorLink(update.From);

        var eventUtc = update.Date.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(update.Date, DateTimeKind.Utc)
            : update.Date.ToUniversalTime();

        var sb = new StringBuilder();
        sb.Append("🚫 Бота исключили из группы\n");
        sb.Append("Когда: ");
        sb.Append(eventUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"));
        sb.Append('\n');
        sb.Append("Откуда: ");
        sb.Append(chatDisplay);
        sb.Append(" (id=");
        sb.Append(chat.Id);
        sb.Append(", type=");
        sb.Append(chat.Type);
        sb.Append(")\n");

        if (!string.IsNullOrWhiteSpace(chatLink))
        {
            sb.Append("Ссылка на чат: ");
            sb.Append(chatLink);
            sb.Append('\n');
        }

        sb.Append("Кто исключил: ");
        sb.Append(actorDisplay);

        if (!string.IsNullOrWhiteSpace(actorLink))
        {
            sb.Append("\nПрофиль: ");
            sb.Append(actorLink);
        }

        sb.Append("\nСтатус: ");
        sb.Append(update.OldChatMember.Status);
        sb.Append(" -> ");
        sb.Append(update.NewChatMember.Status);

        var text = TruncateText(sb.ToString(), 3500);
        await TrySendModeratorNotificationAsync(
            bot,
            text,
            "bot-removed event",
            ct).ConfigureAwait(false);
    }

    private async Task TrySendModeratorNotificationAsync(
        ITelegramBotClient bot,
        string text,
        string eventName,
        CancellationToken ct)
    {
        foreach (var modId in _botOpt.ModeratorIds)
        {
            try
            {
                await bot.SendMessage(
                    chatId: modId,
                    text: text,
                    cancellationToken: ct
                ).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to notify moderator {ModId} about {EventName}", modId, eventName);
            }
        }
    }

    private static bool TryParseTime(string input, out int hour, out int minute)
    {
        hour = 0;
        minute = 0;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        input = input.Trim();

        var parts = input.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 1)
        {
            // Только часы 9 или 09
            if (!int.TryParse(parts[0], out hour))
                return false;
            minute = 0;
            return true;
        }

        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[0], out hour))
                return false;
            if (!int.TryParse(parts[1], out minute))
                return false;
            return true;
        }

        return false;
    }

    private Task<ModerationRequest> BuildModerationRequestAsync(
        MessageLogRecord record,
        Message message,
        ChatVigilanceState? chatVigilanceState,
        CancellationToken ct)
    {
        var recentChatMessages = _cache.GetRecent(
                record.ChatId,
                userId: null,
                window: TimeSpan.FromMinutes(Math.Max(1, _moderationOpt.ChatContextWindowMinutes)),
                maxCount: Math.Max(1, _moderationOpt.ChatContextMaxMessages + 1))
            .Where(static item => !string.IsNullOrWhiteSpace(item.Text))
            .Where(item => item.MessageId != record.MessageId)
            .Where(item => !IsSameAuthor(item, record))
            .TakeLast(Math.Max(1, _moderationOpt.ChatContextMaxMessages))
            .Select(static item => new ModerationContextMessage
            {
                Timestamp = item.Timestamp,
                MessageId = item.MessageId,
                UserId = item.UserId,
                Username = item.Username,
                Text = item.Text
            })
            .ToArray();

        var recentUserMessages = _cache.GetRecent(
                record.ChatId,
                record.FromId,
                window: TimeSpan.FromMinutes(Math.Max(1, _moderationOpt.UserContextWindowMinutes)),
                maxCount: Math.Max(1, _moderationOpt.UserContextMaxMessages + 1))
            .Where(static item => !string.IsNullOrWhiteSpace(item.Text))
            .Where(item => item.MessageId != record.MessageId)
            .TakeLast(Math.Max(1, _moderationOpt.UserContextMaxMessages))
            .Select(static item => new ModerationContextMessage
            {
                Timestamp = item.Timestamp,
                MessageId = item.MessageId,
                UserId = item.UserId,
                Username = item.Username,
                Text = item.Text
            })
            .ToArray();

        var nowUtc = DateTimeOffset.UtcNow;
        var vigilanceActive = chatVigilanceState?.IsActiveAt(nowUtc) == true;
        var activeVigilanceState = vigilanceActive ? chatVigilanceState : null;

        return Task.FromResult(new ModerationRequest
        {
            Text = record.Text ?? string.Empty,
            ChatId = record.ChatId,
            ChatTitle = record.ChatTitle,
            UserId = record.FromId,
            Username = record.FromUsername,
            MessageId = record.MessageId,
            ReplyToText = message.ReplyToMessage?.Text ?? message.ReplyToMessage?.Caption,
            ChatUnderHeightenedVigilance = vigilanceActive,
            ChatVigilanceUntilUtc = activeVigilanceState?.HeightenedUntilUtc,
            ChatVigilanceTriggerCount = activeVigilanceState?.TriggerCount,
            ChatVigilanceLabel = activeVigilanceState?.LastLabel,
            ChatVigilanceTopic = activeVigilanceState?.LastTopic,
            ChatVigilanceRiskLevel = activeVigilanceState?.LastRiskLevel,
            ChatVigilanceReason = activeVigilanceState?.LastReason,
            HighSensitivityReview = vigilanceActive,
            RecentChatMessages = recentChatMessages,
            RecentUserMessages = recentUserMessages
        });
    }

    private static bool IsSameAuthor(DialogMessage item, MessageLogRecord record)
    {
        if (record.FromId.HasValue && item.UserId.HasValue)
            return record.FromId.Value == item.UserId.Value;

        if (!string.IsNullOrWhiteSpace(record.FromUsername) && !string.IsNullOrWhiteSpace(item.Username))
            return string.Equals(record.FromUsername, item.Username, StringComparison.OrdinalIgnoreCase);

        return false;
    }

    private double CalculateConversationRiskContribution(AiVerdict? verdict, ChatVigilanceState? chatVigilanceState)
    {
        if (verdict is null)
            return 0d;

        var label = verdict.Label?.Trim().ToLowerInvariant();
        var confidence = Math.Clamp(verdict.Confidence ?? 0d, 0d, 1d);
        var heightenedVigilance = chatVigilanceState?.IsActiveAt(DateTimeOffset.UtcNow) == true;

        if (verdict.IsModelFailureFallback)
        {
            var fallbackScore = verdict.RiskLevel switch
            {
                "critical" => 4.5d,
                "high" => 2.75d,
                "medium" => 1.25d,
                _ => 0.15d
            };

            if (label is not ("neutral" or "unsure" or null))
                fallbackScore += 0.35d;

            if (verdict.NeedsContextReview == true && verdict.RiskLevel is "medium" or "high" or "critical")
                fallbackScore += 0.25d;

            if (heightenedVigilance)
            {
                fallbackScore += 0.5d;
                if (verdict.Topic is "privacy_tools" or "military_info" or "war_conflict" or "weapons" or "narcotics" or "extremism" or "terrorism" or "other")
                    fallbackScore += 0.5d;
            }

            return Math.Round(Math.Max(0d, fallbackScore), 2, MidpointRounding.AwayFromZero);
        }

        if (verdict.Flagged is null)
            return 0d;

        if (label is "neutral" && verdict.NeedsContextReview != true)
        {
            if (!heightenedVigilance)
                return 0d;

            var watchfulScore = verdict.Topic switch
            {
                "privacy_tools" or "military_info" or "war_conflict" or "weapons" or "narcotics" or "extremism" or "terrorism" or "other" => 0.55d,
                _ => 0d
            };

            watchfulScore += verdict.Intent switch
            {
                "information_request" or "instruction_request" => 0.45d,
                "operational_planning" => 0.8d,
                _ => 0d
            };

            if (verdict.UkArticleCandidates is { Count: > 0 } ukSignals)
            {
                watchfulScore += Math.Min(1.1d, 0.35d + (ukSignals.Count * 0.2d));
                if (verdict.Intent is "instruction_request" or "operational_planning")
                    watchfulScore += 0.35d;
            }

            return Math.Round(Math.Max(0d, watchfulScore), 2, MidpointRounding.AwayFromZero);
        }

        double score;
        if (verdict.Flagged == true)
        {
            score = 5d + (confidence * 3d);
        }
        else if (label is "unsure")
        {
            score = verdict.NeedsContextReview == true ? 1.75d : 0.5d;
        }
        else
        {
            score = 1.5d + (confidence * 2.5d);
        }

        score += label switch
        {
            "terrorism" or "extremism" or "other_illegal" or "pro_ukraine_advocacy" => 1.5d,
            "weapons_illegal" or "narcotics" or "violence" or "distribution_illegal_materials" => 1.0d,
            "hate_speech" or "suicide_selfharm" => 0.75d,
            _ => 0d
        };

        score += verdict.Intent switch
        {
            "advocacy" or "recruitment" or "operational_planning" or "threat" => 1.5d,
            "instruction_request" => 1.0d,
            "information_request" when label is not ("neutral" or "unsure") => 0.5d,
            _ => 0d
        };

        score += verdict.RiskLevel switch
        {
            "critical" => 2.5d,
            "high" => 1.75d,
            "medium" => 0.75d,
            "low" => 0.25d,
            _ => 0d
        };

        score += verdict.Stance switch
        {
            "support" => 1.25d,
            "condemnation" or "educational_quote" or "neutral_report" when verdict.Flagged != true => -1.0d,
            _ => 0d
        };

        if (verdict.NeedsContextReview == true)
            score += 1d;

        if (verdict.UkArticleCandidates is { Count: > 0 } ukCandidates)
        {
            score += Math.Min(2.2d, 0.55d + (ukCandidates.Count * 0.35d));

            if (verdict.Intent is "instruction_request" or "operational_planning" or "recruitment" or "threat")
                score += 0.75d;

            if (verdict.RiskLevel is "high" or "critical")
                score += 0.75d;
        }

        if (heightenedVigilance)
        {
            score += 0.75d;

            score += verdict.Topic switch
            {
                "privacy_tools" or "military_info" or "war_conflict" or "weapons" or "narcotics" or "extremism" or "terrorism" or "other" => 0.75d,
                _ => 0d
            };

            score += verdict.Intent switch
            {
                "information_request" or "instruction_request" => 0.5d,
                "advocacy" or "recruitment" or "operational_planning" or "threat" => 0.85d,
                _ => 0d
            };

            if (verdict.UkArticleCandidates is { Count: > 0 })
                score += 0.6d;
        }

        return Math.Round(Math.Max(0d, score), 2, MidpointRounding.AwayFromZero);
    }

    private async Task<ConversationRiskProcessingResult> TryProcessConversationRiskAsync(
        MessageLogRecord record,
        double riskContribution,
        CancellationToken ct)
    {
        if (riskContribution <= 0d)
            return ConversationRiskProcessingResult.Empty;

        try
        {
            var window = TimeSpan.FromMinutes(Math.Max(1, _moderationOpt.IncidentWindowMinutes));
            var snapshot = await _store
                .GetConversationRiskSnapshotAsync(
                    chatId: record.ChatId,
                    userId: null,
                    window: window,
                    ct: ct)
                .ConfigureAwait(false);

            if (snapshot is null || snapshot.TotalScore < _moderationOpt.IncidentThreshold)
                return new ConversationRiskProcessingResult(snapshot, ShouldNotify: false);

            var shouldNotify = await _store
                .TryMarkConversationRiskAlertAsync(
                    chatId: record.ChatId,
                    userId: null,
                    nowUtc: DateTimeOffset.UtcNow,
                    totalScore: snapshot.TotalScore,
                    cooldown: TimeSpan.FromMinutes(Math.Max(1, _moderationOpt.IncidentCooldownMinutes)),
                    ct: ct)
                .ConfigureAwait(false);

            return new ConversationRiskProcessingResult(snapshot, shouldNotify);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Failed to process conversation risk for chat {ChatId}",
                record.ChatId);
            return ConversationRiskProcessingResult.Empty;
        }
    }

    private async Task<ChatVigilanceState?> TryGetChatVigilanceStateAsync(MessageLogRecord record, CancellationToken ct)
    {
        if (!ShouldUseChatVigilance(record))
            return null;

        try
        {
            return await _store.GetChatVigilanceStateAsync(record.ChatId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load chat vigilance state for chat {ChatId}", record.ChatId);
            return null;
        }
    }

    private async Task TryRefreshChatVigilanceAsync(
        MessageLogRecord record,
        ConversationRiskSnapshot? conversationRiskSnapshot,
        ChatVigilanceState? currentState,
        CancellationToken ct)
    {
        if (!ShouldUseChatVigilance(record) || record.Ai is null)
            return;

        var triggerByFlag = record.Ai.Flagged == true;
        var triggerByConversation = conversationRiskSnapshot is not null
            && conversationRiskSnapshot.TotalScore >= _moderationOpt.IncidentThreshold;

        if (!triggerByFlag && !triggerByConversation)
            return;

        var nowUtc = DateTimeOffset.UtcNow;
        var heightenedUntilUtc = nowUtc.AddHours(Math.Max(1, _moderationOpt.ChatVigilanceHours));
        if (currentState?.HeightenedUntilUtc > heightenedUntilUtc)
            heightenedUntilUtc = currentState.HeightenedUntilUtc;

        var label = triggerByFlag ? record.Ai.Label : "conversation_incident";
        var topic = record.Ai.Topic;
        var riskLevel = triggerByFlag ? record.Ai.RiskLevel : "high";
        var score = conversationRiskSnapshot?.TotalScore;
        var reason = triggerByFlag
            ? record.Ai.Reason
            : $"накопительный риск по чату достиг {conversationRiskSnapshot!.TotalScore:F2}";

        try
        {
            await _store.UpsertChatVigilanceAsync(
                    chatId: record.ChatId,
                    nowUtc: nowUtc,
                    heightenedUntilUtc: heightenedUntilUtc,
                    label: label,
                    topic: topic,
                    riskLevel: riskLevel,
                    reason: reason,
                    score: score,
                    ct: ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to update chat vigilance state for chat {ChatId}", record.ChatId);
        }
    }

    private static bool ShouldUseChatVigilance(MessageLogRecord record)
    {
        return string.Equals(record.ChatType, ChatType.Group.ToString(), StringComparison.OrdinalIgnoreCase) ||
               string.Equals(record.ChatType, ChatType.Supergroup.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static AiVerdict EnrichVerdictWithConversationRisk(AiVerdict verdict, ConversationRiskSnapshot snapshot)
    {
        return new AiVerdict
        {
            Label = verdict.Label,
            Confidence = verdict.Confidence,
            Flagged = verdict.Flagged,
            Reason = verdict.Reason,
            Topic = verdict.Topic,
            Intent = verdict.Intent,
            Stance = verdict.Stance,
            RiskLevel = verdict.RiskLevel,
            NeedsContextReview = verdict.NeedsContextReview,
            UkArticleCandidates = verdict.UkArticleCandidates,
            IncidentScore = snapshot.TotalScore,
            IncidentEventCount = snapshot.EventCount,
            IsModelFailureFallback = verdict.IsModelFailureFallback
        };
    }

    private async Task TryTrackParticipantFromMessageAsync(
        Message message,
        MessageLogRecord record,
        CancellationToken ct)
    {
        if ((message.Chat.Type != ChatType.Group && message.Chat.Type != ChatType.Supergroup) ||
            message.From is null)
            return;

        var snapshot = BuildChatMemberRegistryRecord(
            chatId: message.Chat.Id,
            chatTitle: message.Chat.Title,
            chatUsername: message.Chat.Username,
            user: message.From,
            observedAtUtc: record.Timestamp,
            status: null,
            isAdmin: false,
            seenInMessages: true,
            seenInNewChatMembers: false,
            seenInChatMemberUpdates: false,
            seenInAdminSnapshot: false);

        await TryUpsertChatMemberAsync(snapshot, ct).ConfigureAwait(false);
    }

    private async Task TryTrackNewChatMembersAsync(
        Message message,
        IReadOnlyCollection<User> newMembers,
        CancellationToken ct)
    {
        if (newMembers.Count == 0)
            return;

        var observedAtUtc = ToUtcDateTimeOffset(message.Date);
        foreach (var user in newMembers)
        {
            var snapshot = BuildChatMemberRegistryRecord(
                chatId: message.Chat.Id,
                chatTitle: message.Chat.Title,
                chatUsername: message.Chat.Username,
                user: user,
                observedAtUtc: observedAtUtc,
                status: ChatMemberStatus.Member.ToString(),
                isAdmin: false,
                seenInMessages: false,
                seenInNewChatMembers: true,
                seenInChatMemberUpdates: false,
                seenInAdminSnapshot: false);

            await TryUpsertChatMemberAsync(snapshot, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleChatMemberAsync(
        ChatMemberUpdated update,
        CancellationToken ct)
    {
        try
        {
            var newMember = update.NewChatMember;
            var snapshot = BuildChatMemberRegistryRecord(
                chatId: update.Chat.Id,
                chatTitle: update.Chat.Title,
                chatUsername: update.Chat.Username,
                user: newMember.User,
                observedAtUtc: ToUtcDateTimeOffset(update.Date),
                status: newMember.Status.ToString(),
                isAdmin: newMember.Status is ChatMemberStatus.Administrator or ChatMemberStatus.Creator,
                seenInMessages: false,
                seenInNewChatMembers: false,
                seenInChatMemberUpdates: true,
                seenInAdminSnapshot: false);

            await TryUpsertChatMemberAsync(snapshot, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to process chat_member update for chat {ChatId}", update.Chat.Id);
        }
    }

    private async Task TryUpsertChatMemberAsync(
        ChatMemberRegistryRecord snapshot,
        CancellationToken ct)
    {
        try
        {
            await ExecuteWithRetryAsync(
                operationName: "sqlite_chat_member_upsert",
                action: retryCt => _store.UpsertChatMemberAsync(snapshot, retryCt),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Failed to upsert chat member snapshot (chat={ChatId}, user={UserId})",
                snapshot.ChatId,
                snapshot.UserId);
        }
    }

    private static ChatMemberRegistryRecord BuildChatMemberRegistryRecord(
        long chatId,
        string? chatTitle,
        string? chatUsername,
        User user,
        DateTimeOffset observedAtUtc,
        string? status,
        bool isAdmin,
        bool seenInMessages,
        bool seenInNewChatMembers,
        bool seenInChatMemberUpdates,
        bool seenInAdminSnapshot)
    {
        return new ChatMemberRegistryRecord
        {
            ChatId = chatId,
            ChatTitle = chatTitle,
            ChatUsername = chatUsername,
            UserId = user.Id,
            Username = user.Username,
            FirstName = user.FirstName,
            LastName = user.LastName,
            IsBot = user.IsBot,
            Status = status,
            IsAdmin = isAdmin,
            ObservedAtUtc = observedAtUtc,
            SeenInMessages = seenInMessages,
            SeenInNewChatMembers = seenInNewChatMembers,
            SeenInChatMemberUpdates = seenInChatMemberUpdates,
            SeenInAdminSnapshot = seenInAdminSnapshot
        };
    }

    private async Task HandleNewChatMembersAsync(
        ITelegramBotClient bot,
        Message message,
        CancellationToken ct)
    {
        // Работаем только в группах и супергруппах
        if (message.Chat.Type != ChatType.Group &&
            message.Chat.Type != ChatType.Supergroup)
            return;

        var newMembers = message.NewChatMembers;
        if (newMembers is null || newMembers.Length == 0)
            return;

        await TryTrackNewChatMembersAsync(message, newMembers, ct).ConfigureAwait(false);

        // Берем только живых людей, без ботов
        var humans = newMembers
            .Where(u => u is { IsBot: false })
            .ToArray();

        if (humans.Length == 0)
            return;

        if (ShouldSuppressWelcomeMessage(message.Chat.Id, DateTimeOffset.UtcNow))
        {
            _log.LogInformation(
                "Welcome suppressed by cooldown for {Count} new members in chat {ChatId}",
                humans.Length,
                message.Chat.Id);
            return;
        }

        var welcomeDua = await _islamicProvider.GetRandomWelcomeDuaAsync(ct).ConfigureAwait(false);
        if (welcomeDua is null)
        {
            _log.LogWarning("Welcome: failed to get welcome dua for new members in chat {ChatId}", message.Chat.Id);
            return;
        }

        // Собираем список имён новых участников, но без перегрузки при массовом входе
        var visibleHumans = humans.Take(4).ToArray();
        var hiddenCount = humans.Length - visibleHumans.Length;
        var namesText = string.Join(", ", visibleHumans.Select(static u =>
        {
            if (!string.IsNullOrWhiteSpace(u.Username))
                return "@" + u.Username;

            var full = $"{u.FirstName} {u.LastName}".Trim();
            return string.IsNullOrWhiteSpace(full) ? "новый участник" : full;
        }));

        if (hiddenCount > 0)
        {
            namesText += $", и ещё {hiddenCount}";
        }

        var chatTitle = message.Chat.Title ?? "этот чат";

        var text =
            $"Ас-саляму алейкум ва рахматуллахи ва баракятух, {namesText}.\n\n" +
            $"Добро пожаловать в чат «{chatTitle}».\n\n" +
            "Пусть Аллах откроет для нас двери знания, укрепит иман и сделает это собрание местом пользы, уважения и благого напоминания.\n\n" +
            $"{welcomeDua.Title}\n" +
            $"{welcomeDua.Arabic}\n" +
            $"{welcomeDua.Transliteration}\n" +
            $"«{welcomeDua.TranslationRu}»\n\n" +
            $"Дуа: {welcomeDua.Source}";

        await bot.SendMessage(
            chatId: message.Chat.Id,
            text: text,
            cancellationToken: ct
        ).ConfigureAwait(false);

        _log.LogInformation(
            "Welcome dua sent for {Count} new members in chat {ChatId}",
            humans.Length,
            message.Chat.Id);
    }

    private async Task<bool> IsChatAdminAsync(
    ITelegramBotClient bot,
    long chatId,
    long? userId,
    CancellationToken ct)
    {
        if (!userId.HasValue)
            return false;

        try
        {
            var member = await bot.GetChatMember(chatId, userId.Value, ct).ConfigureAwait(false);

            if (member is Telegram.Bot.Types.ChatMemberOwner)
                return true;

            if (member is Telegram.Bot.Types.ChatMemberAdministrator)
                return true;

            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Failed to check admin rights for user {UserId} in chat {ChatId}",
                userId, chatId);
            return false;
        }
    }

    private async Task<ChatMemberStatus?> TryGetCurrentBotStatusAsync(
        ITelegramBotClient bot,
        long chatId,
        CancellationToken ct)
    {
        try
        {
            var me = await bot.GetMe(cancellationToken: ct).ConfigureAwait(false);
            var member = await bot.GetChatMember(chatId, me.Id, ct).ConfigureAwait(false);
            return member.Status;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Failed to fetch current bot membership status for chat {ChatId}",
                chatId);
            return null;
        }
    }

    private async Task<ParticipantRosterContext> TryRefreshParticipantRosterAsync(
        ITelegramBotClient bot,
        ChatRegistryEntry entry,
        CancellationToken ct)
    {
        var botStatus = await TryGetCurrentBotStatusAsync(bot, entry.ChatId, ct).ConfigureAwait(false);
        var botIsAdmin = botStatus is ChatMemberStatus.Administrator or ChatMemberStatus.Creator;
        var telegramMemberCount = await TryGetChatMemberCountAsync(bot, entry.ChatId, ct).ConfigureAwait(false);

        if (!botIsAdmin)
            return new ParticipantRosterContext(botIsAdmin, telegramMemberCount);

        try
        {
            var observedAtUtc = DateTimeOffset.UtcNow;
            var admins = await bot.GetChatAdministrators(entry.ChatId, cancellationToken: ct).ConfigureAwait(false);
            foreach (var admin in admins)
            {
                var snapshot = BuildChatMemberRegistryRecord(
                    chatId: entry.ChatId,
                    chatTitle: entry.ChatTitle,
                    chatUsername: entry.ChatUsername,
                    user: admin.User,
                    observedAtUtc: observedAtUtc,
                    status: admin.Status.ToString(),
                    isAdmin: true,
                    seenInMessages: false,
                    seenInNewChatMembers: false,
                    seenInChatMemberUpdates: false,
                    seenInAdminSnapshot: true);

                await TryUpsertChatMemberAsync(snapshot, ct).ConfigureAwait(false);
            }

            var me = await bot.GetMe(cancellationToken: ct).ConfigureAwait(false);
            var currentBotMember = await bot.GetChatMember(entry.ChatId, me.Id, ct).ConfigureAwait(false);
            var botSnapshot = BuildChatMemberRegistryRecord(
                chatId: entry.ChatId,
                chatTitle: entry.ChatTitle,
                chatUsername: entry.ChatUsername,
                user: currentBotMember.User,
                observedAtUtc: observedAtUtc,
                status: currentBotMember.Status.ToString(),
                isAdmin: currentBotMember.Status is ChatMemberStatus.Administrator or ChatMemberStatus.Creator,
                seenInMessages: false,
                seenInNewChatMembers: false,
                seenInChatMemberUpdates: false,
                seenInAdminSnapshot: true);

            await TryUpsertChatMemberAsync(botSnapshot, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to refresh participant roster from Telegram for chat {ChatId}", entry.ChatId);
        }

        return new ParticipantRosterContext(botIsAdmin, telegramMemberCount);
    }

    private async Task<int?> TryGetChatMemberCountAsync(
        ITelegramBotClient bot,
        long chatId,
        CancellationToken ct)
    {
        try
        {
            return await bot.GetChatMemberCount(chatId, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to fetch chat member count for chat {ChatId}", chatId);
            return null;
        }
    }

    private static string BuildParticipantSources(TrackedParticipantEntry participant)
    {
        var sources = new List<string>(4);
        if (participant.SeenInMessages)
            sources.Add("msg");
        if (participant.SeenInNewChatMembers)
            sources.Add("join");
        if (participant.SeenInChatMemberUpdates)
            sources.Add("member");
        if (participant.SeenInAdminSnapshot)
            sources.Add("admin");

        return string.Join(",", sources);
    }

    private static string BuildParticipantsCaption(
        ChatRegistryEntry entry,
        long participantCount,
        ParticipantRosterContext rosterContext)
    {
        var sb = new StringBuilder();
        sb.Append("Состав чата ");
        sb.Append(BuildRegistryChatDisplay(entry));
        sb.Append(" [id=");
        sb.Append(entry.ChatId);
        sb.Append("]\nДоступно записей: ");
        sb.Append(participantCount);

        if (rosterContext.TelegramMemberCount.HasValue)
        {
            sb.Append("\nТекущий размер по Telegram: ");
            sb.Append(rosterContext.TelegramMemberCount.Value);
        }

        if (!rosterContext.BotIsAdmin)
            sb.Append("\nБот не администратор: список ограничен доступным составом.");

        return sb.ToString();
    }

    private async Task TrySyncChatRegistryStatusAsync(
        ChatRegistryEntry entry,
        ChatMemberStatus status,
        CancellationToken ct)
    {
        try
        {
            var update = new ChatRegistryUpdate
            {
                ChatId = entry.ChatId,
                ChatTitle = entry.ChatTitle,
                ChatUsername = entry.ChatUsername,
                ChatType = string.IsNullOrWhiteSpace(entry.ChatType)
                    ? ChatType.Supergroup.ToString()
                    : entry.ChatType!,
                Status = status.ToString(),
                IsActive = IsInsideStatus(status),
                SeenAtUtc = DateTimeOffset.UtcNow
            };

            await _chatRegistry.UpsertAsync(update, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Failed to sync chat registry status for chat {ChatId}",
                entry.ChatId);
        }
    }

    private static bool IsInsideStatus(ChatMemberStatus status) =>
        status == ChatMemberStatus.Member ||
        status == ChatMemberStatus.Administrator ||
        status == ChatMemberStatus.Creator ||
        status == ChatMemberStatus.Restricted;

    private bool ShouldSuppressWelcomeMessage(long chatId, DateTimeOffset nowUtc)
    {
        while (true)
        {
            if (_recentWelcomeByChat.TryGetValue(chatId, out var lastSentUtc))
            {
                if (nowUtc - lastSentUtc < WelcomeSendCooldown)
                    return true;

                if (_recentWelcomeByChat.TryUpdate(chatId, nowUtc, lastSentUtc))
                    return false;

                continue;
            }

            if (_recentWelcomeByChat.TryAdd(chatId, nowUtc))
                return false;
        }
    }

    private static string BuildChatDisplay(MessageLogRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.ChatTitle))
            return record.ChatTitle;

        if (!string.IsNullOrWhiteSpace(record.ChatUsername))
            return "@" + TrimAt(record.ChatUsername);

        return record.ChatId.ToString();
    }

    private static string BuildChatDisplay(Chat chat)
    {
        if (!string.IsNullOrWhiteSpace(chat.Title))
            return chat.Title;

        if (!string.IsNullOrWhiteSpace(chat.Username))
            return "@" + TrimAt(chat.Username);

        return $"чат {chat.Id}";
    }

    private static string? BuildChatLink(string? chatUsername)
    {
        if (string.IsNullOrWhiteSpace(chatUsername))
            return null;

        return $"https://t.me/{TrimAt(chatUsername)}";
    }

    private static string BuildUserDisplay(MessageLogRecord record)
    {
        var sb = new StringBuilder();

        var fullName = $"{record.FromFirstName} {record.FromLastName}".Trim();
        if (!string.IsNullOrWhiteSpace(fullName))
            sb.Append(fullName);

        if (!string.IsNullOrWhiteSpace(record.FromUsername))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append('@');
            sb.Append(TrimAt(record.FromUsername));
        }

        if (record.FromId.HasValue)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append("id=");
            sb.Append(record.FromId.Value);
        }

        return sb.Length == 0 ? "id=unknown" : sb.ToString();
    }

    private static string BuildActorDisplay(User? user)
    {
        if (user is null)
            return "не удалось определить";

        var sb = new StringBuilder();

        var fullName = $"{user.FirstName} {user.LastName}".Trim();
        if (!string.IsNullOrWhiteSpace(fullName))
            sb.Append(fullName);

        if (!string.IsNullOrWhiteSpace(user.Username))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append('@');
            sb.Append(TrimAt(user.Username));
        }

        if (user.Id > 0)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append("id=");
            sb.Append(user.Id);
        }

        return sb.Length == 0 ? "не удалось определить" : sb.ToString();
    }

    private static string? BuildActorLink(User? user)
    {
        if (user is null)
            return null;

        if (!string.IsNullOrWhiteSpace(user.Username))
            return $"https://t.me/{TrimAt(user.Username)}";

        return user.Id > 0 ? $"tg://user?id={user.Id}" : null;
    }

    private static string? BuildUserLink(MessageLogRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.FromUsername))
            return $"https://t.me/{TrimAt(record.FromUsername)}";

        if (record.FromId.HasValue)
            return $"tg://user?id={record.FromId.Value}";

        return null;
    }

    private static string BuildModerationCaption(
        MessageLogRecord record,
        string chatDisplay,
        string userDisplay,
        string? messageLink,
        ConversationRiskSnapshot? conversationRisk)
    {
        var sb = new StringBuilder();
        sb.Append("🚨 Обнаружено возможное нарушение законодательства РФ\n");
        sb.Append("Чат: ");
        sb.Append(chatDisplay);
        sb.Append(" (id=");
        sb.Append(record.ChatId);
        sb.Append(")\n");
        sb.Append("Пользователь: ");
        sb.Append(userDisplay);
        sb.Append('\n');
        sb.Append("Вердикт: ");
        sb.Append(record.Ai?.Label ?? "n/a");
        sb.Append(" уверенность: ");
        sb.Append($"{(record.Ai?.Confidence ?? 0):P0}");

        if (conversationRisk is not null)
        {
            sb.Append("\nНакопительный риск: ");
            sb.Append(conversationRisk.TotalScore.ToString("0.0"));
            sb.Append(" (событий: ");
            sb.Append(conversationRisk.EventCount);
            sb.Append(')');
        }

        if (!string.IsNullOrWhiteSpace(messageLink))
        {
            sb.Append("\nСсылка на сообщение: ");
            sb.Append(messageLink);
        }

        return TruncateText(sb.ToString(), 900);
    }

    private static string BuildModerationDetails(
        MessageLogRecord record,
        string chatDisplay,
        string userDisplay,
        string? userLink,
        string? messageLink,
        ConversationRiskSnapshot? conversationRisk)
    {
        var sb = new StringBuilder();
        sb.Append("🚨 Обнаружено возможное нарушение законодательства РФ\n");
        sb.Append("Чат: ");
        sb.Append(chatDisplay);
        sb.Append(" (id=");
        sb.Append(record.ChatId);
        sb.Append(")\n");
        sb.Append("Пользователь: ");
        sb.Append(userDisplay);
        sb.Append('\n');

        if (!string.IsNullOrWhiteSpace(userLink))
        {
            sb.Append("Профиль: ");
            sb.Append(userLink);
            sb.Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(messageLink))
        {
            sb.Append("Ссылка на сообщение: ");
            sb.Append(messageLink);
            sb.Append('\n');
        }
        else
        {
            sb.Append("Ссылка на сообщение: недоступна (нет публичной ссылки)\n");
        }

        sb.Append("Вердикт: ");
        sb.Append(record.Ai?.Label ?? "n/a");
        sb.Append(" уверенность: ");
        sb.Append($"{(record.Ai?.Confidence ?? 0):P0}");
        sb.Append('\n');

        sb.Append("Причина: ");
        sb.Append(string.IsNullOrWhiteSpace(record.Ai?.Reason) ? "n/a" : record.Ai?.Reason);

        AppendAiSummary(sb, record, conversationRisk);

        return TruncateText(sb.ToString(), 3500);
    }

    private static string BuildConversationRiskCaption(
        MessageLogRecord record,
        string chatDisplay,
        string userDisplay,
        string? messageLink,
        ConversationRiskSnapshot? conversationRisk)
    {
        var sb = new StringBuilder();
        sb.Append("⚠️ Обнаружен накопительный риск по диалогу\n");
        sb.Append("Чат: ");
        sb.Append(chatDisplay);
        sb.Append(" (id=");
        sb.Append(record.ChatId);
        sb.Append(")\n");
        sb.Append("Участник-триггер: ");
        sb.Append(userDisplay);

        if (conversationRisk is not null)
        {
            sb.Append("\nРиск: ");
            sb.Append(conversationRisk.TotalScore.ToString("0.0"));
            sb.Append(" за ");
            sb.Append(conversationRisk.EventCount);
            sb.Append(" сообщ.");
            sb.Append("\nУчастников в окне: ");
            sb.Append(conversationRisk.ParticipantCount);
        }

        if (!string.IsNullOrWhiteSpace(messageLink))
        {
            sb.Append("\nСсылка на сообщение: ");
            sb.Append(messageLink);
        }

        return TruncateText(sb.ToString(), 900);
    }

    private static string BuildConversationRiskDetails(
        MessageLogRecord record,
        string chatDisplay,
        string userDisplay,
        string? userLink,
        string? messageLink,
        ConversationRiskSnapshot? conversationRisk)
    {
        var sb = new StringBuilder();
        sb.Append("⚠️ Обнаружен накопительный риск по диалогу\n");
        sb.Append("Чат: ");
        sb.Append(chatDisplay);
        sb.Append(" (id=");
        sb.Append(record.ChatId);
        sb.Append(")\n");
        sb.Append("Последняя реплика: ");
        sb.Append(userDisplay);
        sb.Append('\n');

        if (!string.IsNullOrWhiteSpace(userLink))
        {
            sb.Append("Профиль: ");
            sb.Append(userLink);
            sb.Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(messageLink))
        {
            sb.Append("Ссылка на сообщение: ");
            sb.Append(messageLink);
            sb.Append('\n');
        }
        else
        {
            sb.Append("Ссылка на сообщение: недоступна (нет публичной ссылки)\n");
        }

        if (conversationRisk is not null)
        {
            sb.Append("Накопительный риск: ");
            sb.Append(conversationRisk.TotalScore.ToString("0.0"));
            sb.Append(" за ");
            sb.Append(conversationRisk.EventCount);
            sb.Append(" сообщ.\n");
            sb.Append("Участников в окне: ");
            sb.Append(conversationRisk.ParticipantCount);
            sb.Append('\n');
        }

        sb.Append("Последний вердикт: ");
        sb.Append(record.Ai?.Label ?? "n/a");
        sb.Append(" уверенность: ");
        sb.Append($"{(record.Ai?.Confidence ?? 0):P0}");
        sb.Append('\n');

        sb.Append("Причина: ");
        sb.Append(string.IsNullOrWhiteSpace(record.Ai?.Reason) ? "n/a" : record.Ai?.Reason);

        AppendAiSummary(sb, record, conversationRisk);

        return TruncateText(sb.ToString(), 3500);
    }

    private ModeratorDeliveryEvent BuildFlaggedModeratorDeliveryEvent(
        MessageLogRecord record,
        ConversationRiskSnapshot? conversationRisk)
    {
        var chatDisplay = BuildChatDisplay(record);
        var userDisplay = BuildUserDisplay(record);
        var messageLink = BuildMessageLink(record);
        var userLink = BuildUserLink(record);

        return new ModeratorDeliveryEvent
        {
            Kind = ModeratorDeliveryKind.FlaggedMessage,
            Record = record,
            ConversationRisk = conversationRisk,
            ChatDisplay = chatDisplay,
            UserDisplay = userDisplay,
            UserLink = userLink,
            MessageLink = messageLink,
            Caption = BuildModerationCaption(record, chatDisplay, userDisplay, messageLink, conversationRisk),
            Details = BuildModerationDetails(record, chatDisplay, userDisplay, userLink, messageLink, conversationRisk)
        };
    }

    private ModeratorDeliveryEvent BuildConversationRiskModeratorDeliveryEvent(
        MessageLogRecord record,
        ConversationRiskSnapshot? conversationRisk)
    {
        var chatDisplay = BuildChatDisplay(record);
        var userDisplay = BuildUserDisplay(record);
        var messageLink = BuildMessageLink(record);
        var userLink = BuildUserLink(record);

        return new ModeratorDeliveryEvent
        {
            Kind = ModeratorDeliveryKind.ConversationRisk,
            Record = record,
            ConversationRisk = conversationRisk,
            ChatDisplay = chatDisplay,
            UserDisplay = userDisplay,
            UserLink = userLink,
            MessageLink = messageLink,
            Caption = BuildConversationRiskCaption(record, chatDisplay, userDisplay, messageLink, conversationRisk),
            Details = BuildConversationRiskDetails(record, chatDisplay, userDisplay, userLink, messageLink, conversationRisk)
        };
    }

    private async Task TryPublishModeratorDeliveryAsync(
        ModeratorDeliveryEvent notification,
        CancellationToken ct)
    {
        try
        {
            await _moderatorDelivery.PublishAsync(notification, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Moderator delivery sink failed for {Kind}; chat={ChatId} msg={MessageId}",
                notification.Kind,
                notification.Record.ChatId,
                notification.Record.MessageId);
        }
    }

    private static void AppendAiSummary(
        StringBuilder sb,
        MessageLogRecord record,
        ConversationRiskSnapshot? conversationRisk)
    {
        if (!string.IsNullOrWhiteSpace(record.Ai?.Topic))
        {
            sb.Append("\nТема: ");
            sb.Append(HumanizeAiValue(record.Ai!.Topic));
        }

        if (!string.IsNullOrWhiteSpace(record.Ai?.Intent))
        {
            sb.Append("\nНамерение: ");
            sb.Append(HumanizeAiValue(record.Ai!.Intent));
        }

        if (!string.IsNullOrWhiteSpace(record.Ai?.Stance))
        {
            sb.Append("\nПозиция: ");
            sb.Append(HumanizeAiValue(record.Ai!.Stance));
        }

        if (!string.IsNullOrWhiteSpace(record.Ai?.RiskLevel))
        {
            sb.Append("\nУровень риска: ");
            sb.Append(HumanizeAiValue(record.Ai!.RiskLevel));
        }

        if (record.Ai?.NeedsContextReview == true)
        {
            sb.Append("\nКонтекст: повышает риск");
        }

        if (record.Ai?.IsModelFailureFallback == true)
        {
            sb.Append("\nРежим анализа: локальный fallback из-за сбоя LLM");
        }

        if (record.Ai?.UkArticleCandidates is { Count: > 0 } articles)
        {
            sb.Append("\nВозможные статьи УК РФ: ");
            sb.Append(string.Join(", ", articles));
        }

        if (conversationRisk is not null)
        {
            sb.Append("\nОкно риска: ");
            sb.Append(conversationRisk.TotalScore.ToString("0.0"));
            sb.Append(" / ");
            sb.Append(conversationRisk.EventCount);
            sb.Append(" сообщ.");
        }
    }

    private static string HumanizeAiValue(string value) =>
        value switch
        {
            "religion" => "религия",
            "war_conflict" => "военный конфликт",
            "extremism" => "экстремизм",
            "terrorism" => "терроризм",
            "military_info" => "военные сведения",
            "narcotics" => "наркотики",
            "weapons" => "оружие",
            "hate" => "ненависть",
            "self_harm" => "самоповреждение",
            "privacy_tools" => "средства приватности",
            "other" => "прочее",
            "unknown" => "не определено",
            "neutral_discussion" => "нейтральное обсуждение",
            "information_request" => "запрос информации",
            "instruction_request" => "запрос инструкции",
            "advocacy" => "агитация",
            "recruitment" => "вербовка",
            "operational_planning" => "координация/планирование",
            "threat" => "угроза",
            "unclear" => "неясно",
            "support" => "поддержка",
            "neutral_report" => "нейтральный пересказ",
            "condemnation" => "осуждение",
            "educational_quote" => "учебная цитата",
            "none" => "нет",
            "low" => "низкий",
            "medium" => "средний",
            "high" => "высокий",
            "critical" => "критический",
            _ => value
        };

    private static string? BuildMessageLink(MessageLogRecord record)
    {
        if (record.MessageId <= 0)
            return null;

        var username = record.ChatUsername;
        if (!string.IsNullOrWhiteSpace(username))
        {
            if (username.StartsWith("@", StringComparison.Ordinal))
                username = username[1..];

            return $"https://t.me/{username}/{record.MessageId}";
        }

        var isSupergroup = string.Equals(record.ChatType, ChatType.Supergroup.ToString(), StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(record.ChatType, ChatType.Channel.ToString(), StringComparison.OrdinalIgnoreCase);
        if (isSupergroup)
        {
            var abs = Math.Abs(record.ChatId);
            var id = abs.ToString();
            if (id.StartsWith("100", StringComparison.Ordinal))
                id = id[3..];
            else
                id = string.Empty;

            if (!string.IsNullOrWhiteSpace(id))
                return $"https://t.me/c/{id}/{record.MessageId}";
        }

        return $"tg://openmessage?chat_id={record.ChatId}&message_id={record.MessageId}";
    }

    private static string TrimAt(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return value.StartsWith("@", StringComparison.Ordinal) ? value[1..] : value;
    }

    private static string TruncateText(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLen)
            return text;

        if (maxLen <= 3)
            return text[..maxLen];

        return text[..(maxLen - 3)] + "...";
    }

    private async Task TrackChatFromMessageAsync(Message message, DateTimeOffset seenAtUtc, CancellationToken ct)
    {
        if (message.Chat.Type != ChatType.Group && message.Chat.Type != ChatType.Supergroup)
            return;

        try
        {
            var update = new ChatRegistryUpdate
            {
                ChatId = message.Chat.Id,
                ChatTitle = message.Chat.Title,
                ChatUsername = message.Chat.Username,
                ChatType = message.Chat.Type.ToString(),
                Status = null,
                IsActive = true,
                SeenAtUtc = seenAtUtc
            };

            await _chatRegistry.UpsertAsync(update, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to update chat registry from message for chat {ChatId}", message.Chat.Id);
        }
    }

    private async Task TrackChatFromMyChatMemberAsync(ChatMemberUpdated update, CancellationToken ct)
    {
        try
        {
            var chat = update.Chat;
            var status = update.NewChatMember.Status;
            var isActive =
                status == ChatMemberStatus.Member ||
                status == ChatMemberStatus.Administrator ||
                status == ChatMemberStatus.Creator ||
                status == ChatMemberStatus.Restricted;

            var regUpdate = new ChatRegistryUpdate
            {
                ChatId = chat.Id,
                ChatTitle = chat.Title,
                ChatUsername = chat.Username,
                ChatType = chat.Type.ToString(),
                Status = status.ToString(),
                IsActive = isActive,
                SeenAtUtc = ToUtcDateTimeOffset(update.Date)
            };

            await _chatRegistry.UpsertAsync(regUpdate, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to update chat registry from MyChatMember");
        }
    }

    private static DateTimeOffset ToUtcDateTimeOffset(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return new DateTimeOffset(utc);
    }

    private async Task HandleAdminGroupsAsync(
        ITelegramBotClient bot,
        Message message,
        string text,
        CancellationToken ct)
    {
        var filter = ChatRegistryFilter.Active;
        var page = 1;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 1)
        {
            for (var i = 1; i < parts.Length; i++)
            {
                var token = parts[i].ToLowerInvariant();
                if (int.TryParse(token, out var parsedPage))
                {
                    page = Math.Max(1, parsedPage);
                    continue;
                }

                filter = token switch
                {
                    "all" or "все" => ChatRegistryFilter.All,
                    "removed" or "deleted" or "left" or "kicked" or "удаленные" or "удалённые" => ChatRegistryFilter.Removed,
                    "tracked" or "watch" or "watched" or "отслеживаемые" or "отслеживаемый" => ChatRegistryFilter.Tracked,
                    "active" or "активные" => ChatRegistryFilter.Active,
                    _ => filter
                };
            }
        }

        await SendAdminGroupsPageAsync(bot, message.Chat.Id, filter, page, ct).ConfigureAwait(false);
    }

    private async Task HandleAdminTrackedAsync(
        ITelegramBotClient bot,
        Message message,
        string text,
        CancellationToken ct)
    {
        var page = 1;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 1 && int.TryParse(parts[1], out var parsedPage))
        {
            page = Math.Max(1, parsedPage);
        }

        await SendAdminGroupsPageAsync(bot, message.Chat.Id, ChatRegistryFilter.Tracked, page, ct).ConfigureAwait(false);
    }

    private async Task SendAdminGroupsPageAsync(
        ITelegramBotClient bot,
        long targetChatId,
        ChatRegistryFilter filter,
        int page,
        CancellationToken ct)
    {
        const int pageSize = 10;
        var total = await _chatRegistry.CountAsync(filter, ct).ConfigureAwait(false);
        if (total <= 0)
        {
            await SendPrivateAdminMessageAsync(bot, targetChatId, "Список чатов пока пуст.", ct).ConfigureAwait(false);
            return;
        }

        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        var list = await _chatRegistry.GetPageAsync(filter, page, pageSize, ct).ConfigureAwait(false);
        var label = filter switch
        {
            ChatRegistryFilter.All => "все",
            ChatRegistryFilter.Removed => "удаленные",
            ChatRegistryFilter.Tracked => "отслеживаемые",
            _ => "активные"
        };

        var sb = new StringBuilder();
        sb.Append("Чаты бота (");
        sb.Append(label);
        sb.Append("): страница ");
        sb.Append(page);
        sb.Append(" из ");
        sb.Append(pageCount);
        sb.Append(", всего ");
        sb.Append(total);
        sb.AppendLine(".");
        sb.AppendLine();

        var index = (page - 1) * pageSize;
        foreach (var entry in list)
        {
            index++;
            var title = entry.ChatTitle;
            var username = string.IsNullOrWhiteSpace(entry.ChatUsername) ? null : "@" + entry.ChatUsername;
            var display = !string.IsNullOrWhiteSpace(title) ? title : (username ?? $"chat {entry.ChatId}");

            sb.Append(index);
            sb.Append(") ");
            sb.Append(display);

            if (username is not null && (title is null || !title.Contains(username, StringComparison.OrdinalIgnoreCase)))
            {
                sb.Append(" (");
                sb.Append(username);
                sb.Append(')');
            }

            sb.Append(" [id=");
            sb.Append(entry.ChatId);
            sb.Append("] status=");
            sb.Append(entry.IsActive ? "active" : "removed");
            sb.Append(", tracked=");
            sb.Append(entry.IsTracked ? "yes" : "no");

            if (!string.IsNullOrWhiteSpace(entry.Status))
            {
                sb.Append(", member=");
                sb.Append(entry.Status);
            }

            sb.Append(", last=");
            sb.Append(entry.LastSeenUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'"));

            if (!entry.IsActive && entry.RemovedUtc.HasValue)
            {
                sb.Append(", removed=");
                sb.Append(entry.RemovedUtc.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'"));
            }

            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("Команды:");
        AppendModeratorCommandsList(sb);

        await SendPrivateAdminMessageAsync(bot, targetChatId, sb.ToString(), ct).ConfigureAwait(false);
    }

    private async Task HandleAdminTrackToggleAsync(
        ITelegramBotClient bot,
        Message message,
        string text,
        bool isTracked,
        CancellationToken ct)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (TryParseBulkTrackFilter(parts, isTracked, out var bulkFilter) && bulkFilter is { } filter)
        {
            await HandleAdminTrackToggleBulkAsync(bot, message, isTracked, filter, ct).ConfigureAwait(false);
            return;
        }

        if (!TryParseChatIdFromCommand(text, out var chatId))
        {
            var usage = isTracked
                ? "Формат: /admin_track <chat_id|all>\nПример: /admin_track -1001234567890\nИли: /admin_track all"
                : "Формат: /admin_untrack <chat_id|all>\nПример: /admin_untrack -1001234567890\nИли: /admin_untrack all";

            await SendPrivateAdminMessageAsync(bot, message.Chat.Id, usage, ct).ConfigureAwait(false);
            return;
        }

        var entry = await _chatRegistry.GetByChatIdAsync(chatId, ct).ConfigureAwait(false);
        if (entry is null)
        {
            await SendPrivateAdminMessageAsync(bot, message.Chat.Id, "Чат не найден в реестре. Добавьте бота в группу и дождитесь хотя бы одного сообщения.", ct).ConfigureAwait(false);
            return;
        }

        var isGroup =
            string.Equals(entry.ChatType, ChatType.Group.ToString(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.ChatType, ChatType.Supergroup.ToString(), StringComparison.OrdinalIgnoreCase);

        if (!isGroup)
        {
            await SendPrivateAdminMessageAsync(bot, message.Chat.Id, "Отслеживание поддерживается только для групп и супергрупп.", ct).ConfigureAwait(false);
            return;
        }

        if (isTracked)
        {
            var liveStatus = await TryGetCurrentBotStatusAsync(bot, chatId, ct).ConfigureAwait(false);
            if (!liveStatus.HasValue)
            {
                await SendPrivateAdminMessageAsync(
                    bot,
                    message.Chat.Id,
                    $"Не удалось проверить текущий статус бота в чате {BuildRegistryChatDisplay(entry)} [id={chatId}].\n" +
                    "Трекинг не изменён. Повторите команду позже.",
                    ct).ConfigureAwait(false);
                return;
            }

            await TrySyncChatRegistryStatusAsync(entry, liveStatus.Value, ct).ConfigureAwait(false);
            entry = await _chatRegistry.GetByChatIdAsync(chatId, ct).ConfigureAwait(false) ?? entry;

            var isInsideChat = IsInsideStatus(liveStatus.Value);

            if (!isInsideChat)
            {
                if (entry.IsTracked)
                {
                    await _chatRegistry
                        .SetTrackedAsync(chatId, isTracked: false, ct)
                        .ConfigureAwait(false);
                    UpdateTrackedStateCache(chatId, false);
                }

                var memberStateText = liveStatus.Value.ToString();
                await SendPrivateAdminMessageAsync(
                    bot,
                    message.Chat.Id,
                    $"Нельзя включить тихое отслеживание: бот сейчас не состоит в чате {BuildRegistryChatDisplay(entry)} [id={chatId}] " +
                    $"(member={memberStateText}).\n" +
                    "Добавьте бота обратно в чат и повторите команду.",
                    ct).ConfigureAwait(false);
                return;
            }
        }

        if (entry.IsTracked == isTracked)
        {
            UpdateTrackedStateCache(chatId, isTracked);

            await SendPrivateAdminMessageAsync(
                bot,
                message.Chat.Id,
                isTracked
                    ? "Этот чат уже отмечен для тихой выгрузки."
                    : "Этот чат уже не отмечен для тихой выгрузки.",
                ct).ConfigureAwait(false);
            return;
        }

        var updated = await _chatRegistry
            .SetTrackedAsync(chatId, isTracked, ct)
            .ConfigureAwait(false);

        if (!updated)
        {
            await SendPrivateAdminMessageAsync(bot, message.Chat.Id, "Не удалось обновить статус чата. Попробуйте ещё раз.", ct).ConfigureAwait(false);
            return;
        }

        UpdateTrackedStateCache(chatId, isTracked);

        var display = BuildRegistryChatDisplay(entry);
        var stateText = isTracked ? "включено" : "отключено";

        await SendPrivateAdminMessageAsync(
            bot,
            message.Chat.Id,
            $"Тихое отслеживание {stateText} для чата: {display} [id={chatId}].",
            ct).ConfigureAwait(false);
    }

    private async Task HandleAdminTrackToggleBulkAsync(
        ITelegramBotClient bot,
        Message message,
        bool isTracked,
        ChatRegistryFilter filter,
        CancellationToken ct)
    {
        var affected = await _chatRegistry.SetTrackedByFilterAsync(filter, isTracked, ct).ConfigureAwait(false);
        _trackedStateCache.Clear();

        var filterText = filter switch
        {
            ChatRegistryFilter.Active => "активных чатов",
            ChatRegistryFilter.Tracked => "отмеченных чатов",
            ChatRegistryFilter.Removed => "удалённых чатов",
            _ => "чатов"
        };

        var stateText = isTracked ? "включено" : "отключено";
        var tail = isTracked
            ? "\nДля просмотра используйте /admin_groups tracked"
            : "\nДля проверки используйте /admin_groups all";

        await SendPrivateAdminMessageAsync(
            bot,
            message.Chat.Id,
            $"Тихое отслеживание {stateText} для {affected} {filterText}.{tail}",
            ct).ConfigureAwait(false);
    }

    private async Task HandleAdminDumpAsync(
        ITelegramBotClient bot,
        Message message,
        string text,
        CancellationToken ct)
    {
        await SendDisabledExportSurfaceMessageAsync(bot, message.Chat.Id, ct).ConfigureAwait(false);
    }

    private async Task HandleAdminParticipantsAsync(
        ITelegramBotClient bot,
        Message message,
        string text,
        CancellationToken ct)
    {
        if (message.Chat.Type != ChatType.Private || !IsModerator(message.From?.Id))
            return;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !long.TryParse(parts[1], out var chatId))
        {
            await SendPrivateAdminMessageAsync(bot, message.Chat.Id, "Формат: /admin_participants <chat_id> [page]\nПример: /admin_participants -1001234567890 1", ct).ConfigureAwait(false);
            return;
        }

        var page = 1;
        if (parts.Length >= 3 && int.TryParse(parts[2], out var parsedPage))
            page = Math.Max(1, parsedPage);

        var entry = await _chatRegistry.GetByChatIdAsync(chatId, ct).ConfigureAwait(false);
        if (entry is null)
        {
            await SendPrivateAdminMessageAsync(bot, message.Chat.Id, "Чат не найден в реестре.", ct).ConfigureAwait(false);
            return;
        }

        var rosterContext = await TryRefreshParticipantRosterAsync(bot, entry, ct).ConfigureAwait(false);
        const int pageSize = 20;
        var total = await _trackedChatExport.CountParticipantsAsync(chatId, ct).ConfigureAwait(false);
        if (total <= 0)
        {
            var noDataText = rosterContext.BotIsAdmin
                ? "По этому чату пока нет доступных боту записей о составе."
                : "По этому чату пока нет доступных боту записей о составе. Если бот станет администратором, он сможет собирать состав точнее.";
            await SendPrivateAdminMessageAsync(bot, message.Chat.Id, noDataText, ct).ConfigureAwait(false);
            return;
        }

        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        var list = await _trackedChatExport
            .GetParticipantsPageAsync(chatId, page, pageSize, ct)
            .ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.Append("Доступный боту состав чата ");
        sb.Append(BuildRegistryChatDisplay(entry));
        sb.Append(" [id=");
        sb.Append(chatId);
        sb.Append("]\n");
        sb.Append("Страница ");
        sb.Append(page);
        sb.Append(" из ");
        sb.Append(pageCount);
        sb.Append(", доступно записей: ");
        sb.Append(total);
        sb.AppendLine(".");
        if (rosterContext.TelegramMemberCount.HasValue)
        {
            sb.Append("Текущий размер чата по Telegram: ");
            sb.Append(rosterContext.TelegramMemberCount.Value);
            sb.AppendLine(".");
        }
        if (!rosterContext.BotIsAdmin)
            sb.AppendLine("Бот не является администратором в этом чате, поэтому список ограничен доступным ему составом.");
        sb.AppendLine();

        var index = (page - 1) * pageSize;
        foreach (var participant in list)
        {
            index++;
            var fullName = $"{participant.FirstName} {participant.LastName}".Trim();
            if (string.IsNullOrWhiteSpace(fullName))
                fullName = "без имени";

            sb.Append(index);
            sb.Append(") id=");
            sb.Append(participant.UserId);

            if (!string.IsNullOrWhiteSpace(participant.Username))
            {
                sb.Append(" @");
                sb.Append(TrimAt(participant.Username));
            }

            sb.Append(" | ");
            sb.Append(fullName);
            if (participant.IsAdmin)
                sb.Append(" | admin");
            if (participant.IsBot)
                sb.Append(" | bot");
            if (!string.IsNullOrWhiteSpace(participant.Status))
            {
                sb.Append(" | status=");
                sb.Append(participant.Status);
            }
            var sources = BuildParticipantSources(participant);
            if (!string.IsNullOrWhiteSpace(sources))
            {
                sb.Append(" | src=");
                sb.Append(sources);
            }
            sb.Append(" | msg=");
            sb.Append(participant.MessageCount);
            sb.Append(" | last=");
            sb.Append(participant.LastSeenUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'"));
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.Append("Для продолжения используйте /admin_chat_summary ");
        sb.Append(chatId);

        await SendPrivateAdminMessageAsync(bot, message.Chat.Id, TruncateText(sb.ToString(), 3500), ct).ConfigureAwait(false);
    }

    private async Task HandleAdminParticipantsDumpAsync(
        ITelegramBotClient bot,
        Message message,
        string text,
        CancellationToken ct)
    {
        await SendDisabledExportSurfaceMessageAsync(bot, message.Chat.Id, ct).ConfigureAwait(false);
    }

    private async Task HandleAdminFlaggedUsersAsync(
        ITelegramBotClient bot,
        Message message,
        string text,
        CancellationToken ct)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        long? chatId = null;
        var page = 1;

        if (parts.Length >= 2)
        {
            if (long.TryParse(parts[1], out var parsedChatId))
            {
                chatId = parsedChatId;
            }
            else if (!int.TryParse(parts[1], out page))
            {
                await SendPrivateAdminMessageAsync(bot, message.Chat.Id, "Формат: /admin_flagged_users [chat_id] [page]\nПример: /admin_flagged_users -1001234567890 1", ct).ConfigureAwait(false);
                return;
            }
        }

        if (parts.Length >= 3 && int.TryParse(parts[2], out var parsedPage))
            page = Math.Max(1, parsedPage);

        const int pageSize = 20;
        var total = await _trackedChatExport.CountFlaggedUsersAsync(chatId, ct).ConfigureAwait(false);
        if (total <= 0)
        {
            await SendPrivateAdminMessageAsync(bot, message.Chat.Id, "Пока нет пользователей с метками нарушения.", ct).ConfigureAwait(false);
            return;
        }

        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        var list = await _trackedChatExport
            .GetFlaggedUsersPageAsync(chatId, page, pageSize, ct)
            .ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.Append("Пользователи, помеченные ИИ");
        if (chatId.HasValue)
        {
            sb.Append(" в чате [id=");
            sb.Append(chatId.Value);
            sb.Append(']');
        }
        sb.Append('\n');
        sb.Append("Страница ");
        sb.Append(page);
        sb.Append(" из ");
        sb.Append(pageCount);
        sb.Append(", всего пользователей: ");
        sb.Append(total);
        sb.AppendLine(".");
        sb.AppendLine();

        var index = (page - 1) * pageSize;
        foreach (var item in list)
        {
            index++;
            var fullName = $"{item.FirstName} {item.LastName}".Trim();
            if (string.IsNullOrWhiteSpace(fullName))
                fullName = "без имени";

            sb.Append(index);
            sb.Append(") id=");
            sb.Append(item.UserId);

            if (!string.IsNullOrWhiteSpace(item.Username))
            {
                sb.Append(" @");
                sb.Append(TrimAt(item.Username));
            }

            sb.Append(" | ");
            sb.Append(fullName);
            sb.Append(" | flags=");
            sb.Append(item.FlagCount);
            sb.Append(" | last=");
            sb.Append(item.LastFlaggedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'"));

            if (item.LastChatId.HasValue)
            {
                sb.Append(" | chat=");
                sb.Append(item.LastChatId.Value);
            }

            if (!string.IsNullOrWhiteSpace(item.LastLabel))
            {
                sb.Append(" | label=");
                sb.Append(item.LastLabel);
            }

            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("Команды:");
        sb.AppendLine("/admin_user_chats");
        sb.Append("/admin_chat_summary");

        await SendPrivateAdminMessageAsync(bot, message.Chat.Id, TruncateText(sb.ToString(), 3500), ct).ConfigureAwait(false);
    }

    private async Task HandleAdminUserChatsAsync(
        ITelegramBotClient bot,
        Message message,
        string text,
        CancellationToken ct)
    {
        if (!TryParseUserIdCommand(text, out var userId))
        {
            await SendPrivateAdminMessageAsync(bot, message.Chat.Id, "Формат: /admin_user_chats <user_id>\nПример: /admin_user_chats 123456789", ct).ConfigureAwait(false);
            return;
        }

        var chats = await _trackedChatExport.GetUserChatsAsync(userId, ct).ConfigureAwait(false);
        if (chats.Count == 0)
        {
            await SendPrivateAdminMessageAsync(bot, message.Chat.Id, "По этому user_id в базе нет сообщений.", ct).ConfigureAwait(false);
            return;
        }

        var sb = new StringBuilder();
        sb.Append("Чаты пользователя id=");
        sb.Append(userId);
        sb.Append(" (всего ");
        sb.Append(chats.Count);
        sb.AppendLine("):");
        sb.AppendLine();

        var index = 0;
        foreach (var chat in chats)
        {
            index++;
            var display = !string.IsNullOrWhiteSpace(chat.ChatTitle)
                ? chat.ChatTitle
                : (!string.IsNullOrWhiteSpace(chat.ChatUsername)
                    ? "@" + TrimAt(chat.ChatUsername)
                    : $"chat {chat.ChatId}");

            sb.Append(index);
            sb.Append(") ");
            sb.Append(display);
            sb.Append(" [id=");
            sb.Append(chat.ChatId);
            sb.Append("] | msg=");
            sb.Append(chat.MessageCount);
            sb.Append(" | flags=");
            sb.Append(chat.FlaggedCount);
            sb.Append(" | last=");
            sb.Append(chat.LastSeenUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'"));
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.Append("Для детализации используйте /admin_chat_summary <chat_id>");

        await SendPrivateAdminMessageAsync(bot, message.Chat.Id, TruncateText(sb.ToString(), 3500), ct).ConfigureAwait(false);
    }

    private async Task HandleAdminUserDumpAsync(
        ITelegramBotClient bot,
        Message message,
        string text,
        CancellationToken ct)
    {
        await SendDisabledExportSurfaceMessageAsync(bot, message.Chat.Id, ct).ConfigureAwait(false);
    }

    private async Task HandleAdminStartedUsersAsync(
        ITelegramBotClient bot,
        Message message,
        string text,
        CancellationToken ct)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var page = 1;
        if (parts.Length >= 2 && !int.TryParse(parts[1], out page))
        {
            await SendPrivateAdminMessageAsync(bot, message.Chat.Id, "Формат: /admin_started_users [page]\nПример: /admin_started_users 1", ct).ConfigureAwait(false);
            return;
        }

        page = Math.Max(1, page);

        const int pageSize = 20;
        var total = await _store.CountPrivateStartUsersAsync(ct).ConfigureAwait(false);
        if (total <= 0)
        {
            await SendPrivateAdminMessageAsync(bot, message.Chat.Id, "Пока нет пользователей, которые запускали бота в личной переписке.", ct).ConfigureAwait(false);
            return;
        }

        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        var list = await _store.GetPrivateStartUsersPageAsync(page, pageSize, ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine("Пользователи, запустившие бота в личной переписке");
        sb.Append("Страница ");
        sb.Append(page);
        sb.Append(" из ");
        sb.Append(pageCount);
        sb.Append(", всего пользователей: ");
        sb.Append(total);
        sb.AppendLine(".");
        sb.AppendLine();

        var index = (page - 1) * pageSize;
        foreach (var item in list)
        {
            index++;
            sb.Append(index);
            sb.Append(") id=");
            sb.Append(item.UserId);

            if (!string.IsNullOrWhiteSpace(item.Username))
            {
                sb.Append(" @");
                sb.Append(TrimAt(item.Username));
            }

            sb.Append(" | ");
            sb.Append(BuildUserDisplayName(item.FirstName, item.LastName));
            sb.Append(" | first=/start ");
            sb.Append(item.StartedAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'"));
            sb.Append(" | last=/start ");
            sb.Append(item.LastStartedAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'"));
            sb.Append(" | starts=");
            sb.Append(item.StartCount);

            if (!string.IsNullOrWhiteSpace(item.Phone))
            {
                sb.Append(" | phone=");
                sb.Append(item.Phone);
            }

            sb.AppendLine();
        }

        sb.AppendLine();
        sb.Append("/admin_health");

        await SendPrivateAdminMessageAsync(bot, message.Chat.Id, TruncateText(sb.ToString(), 3500), ct).ConfigureAwait(false);
    }

    private async Task HandleAdminStartedUsersDumpAsync(
        ITelegramBotClient bot,
        Message message,
        CancellationToken ct)
    {
        await SendDisabledExportSurfaceMessageAsync(bot, message.Chat.Id, ct).ConfigureAwait(false);
    }

    private async Task HandleAdminHealthAsync(
        ITelegramBotClient bot,
        Message message,
        CancellationToken ct)
    {
        if (message.Chat.Type != ChatType.Private || !IsModerator(message.From?.Id))
            return;

        var recentWindow = TimeSpan.FromHours(24);
        var snapshot = await _store.GetAdminHealthSnapshotAsync(recentWindow, ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine("Служебное состояние системы");
        sb.AppendLine();
        sb.Append("Версия: ");
        sb.AppendLine(BotBuildInfo.DisplayVersion);
        sb.Append("ИИ: ");
        sb.Append(_moderationOpt.Enabled ? "включён" : "выключен");
        sb.Append(" | model=");
        sb.Append(_moderationOpt.Model);
        sb.Append(" | reasoner=");
        sb.Append(_moderationOpt.ReasonerEnabled ? _moderationOpt.ReasonerModel : "off");
        sb.AppendLine();
        sb.Append("Контекст: ");
        sb.Append(_moderationOpt.ContextEnabled ? "on" : "off");
        sb.Append(" | chat=");
        sb.Append(_moderationOpt.ChatContextMaxMessages);
        sb.Append('/');
        sb.Append(_moderationOpt.ChatContextWindowMinutes);
        sb.Append("m | user=");
        sb.Append(_moderationOpt.UserContextMaxMessages);
        sb.Append('/');
        sb.Append(_moderationOpt.UserContextWindowMinutes);
        sb.AppendLine("m");
        sb.Append("Инциденты: window=");
        sb.Append(_moderationOpt.IncidentWindowMinutes);
        sb.Append("m | threshold=");
        sb.Append(_moderationOpt.IncidentThreshold.ToString("0.##"));
        sb.Append(" | vigilance=");
        sb.Append(_moderationOpt.ChatVigilanceHours);
        sb.AppendLine("h");
        sb.AppendLine();
        sb.Append("Чаты: all=");
        sb.Append(snapshot.TotalChats);
        sb.Append(" | active=");
        sb.Append(snapshot.ActiveChats);
        sb.Append(" | tracked=");
        sb.Append(snapshot.TrackedChats);
        sb.Append(" | removed=");
        sb.Append(snapshot.RemovedChats);
        sb.AppendLine();
        sb.Append("Личка: запустивших бота=");
        sb.Append(snapshot.PrivateStartUsers);
        sb.AppendLine();
        sb.Append("Сообщения: total=");
        sb.Append(snapshot.TotalMessages);
        sb.Append(" | last=");
        sb.Append(FormatUtc(snapshot.LastMessageUtc));
        sb.AppendLine();
        sb.Append("Flags: total=");
        sb.Append(snapshot.TotalFlaggedEvents);
        sb.Append(" | 24h=");
        sb.Append(snapshot.RecentFlaggedEvents);
        sb.Append(" | last=");
        sb.Append(FormatUtc(snapshot.LastFlaggedUtc));
        sb.AppendLine();
        sb.Append("Risk: total=");
        sb.Append(snapshot.TotalRiskEvents);
        sb.Append(" | 24h=");
        sb.Append(snapshot.RecentRiskEvents);
        sb.Append(" | last=");
        sb.Append(FormatUtc(snapshot.LastRiskUtc));
        sb.AppendLine();
        sb.Append("Vigilance чатов: active=");
        sb.Append(snapshot.ActiveVigilanceChats);
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Команды:");
        sb.AppendLine("/admin_chat_summary <chat_id>");
        sb.Append("/admin_incidents [chat_id] [page]");

        await SendPrivateAdminMessageAsync(bot, message.Chat.Id, TruncateText(sb.ToString(), 3500), ct).ConfigureAwait(false);
    }

    private async Task HandleAdminChatSummaryAsync(
        ITelegramBotClient bot,
        Message message,
        string text,
        CancellationToken ct)
    {
        if (message.Chat.Type != ChatType.Private || !IsModerator(message.From?.Id))
            return;

        if (!TryParseChatIdFromCommand(text, out var chatId))
        {
            await SendPrivateAdminMessageAsync(bot, message.Chat.Id, "Формат: /admin_chat_summary <chat_id>\nПример: /admin_chat_summary -1001234567890", ct).ConfigureAwait(false);
            return;
        }

        var entry = await _chatRegistry.GetByChatIdAsync(chatId, ct).ConfigureAwait(false);
        if (entry is null)
        {
            await SendPrivateAdminMessageAsync(bot, message.Chat.Id, "Чат не найден в реестре.", ct).ConfigureAwait(false);
            return;
        }

        var recentWindow = TimeSpan.FromHours(24);
        var summary = await _store.GetChatModerationSummaryAsync(chatId, recentWindow, ct).ConfigureAwait(false);
        var vigilance = await _store.GetChatVigilanceStateAsync(chatId, ct).ConfigureAwait(false);
        var riskSnapshot = await _store
            .GetConversationRiskSnapshotAsync(chatId, userId: null, TimeSpan.FromMinutes(_moderationOpt.IncidentWindowMinutes), ct)
            .ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.Append("Сводка чата: ");
        sb.Append(BuildRegistryChatDisplay(entry));
        sb.Append(" [id=");
        sb.Append(chatId);
        sb.AppendLine("]");
        sb.Append("Тип=");
        sb.Append(entry.ChatType ?? "unknown");
        sb.Append(" | status=");
        sb.Append(entry.IsActive ? "active" : "removed");
        sb.Append(" | tracked=");
        sb.Append(entry.IsTracked ? "yes" : "no");
        if (!string.IsNullOrWhiteSpace(entry.Status))
        {
            sb.Append(" | member=");
            sb.Append(entry.Status);
        }

        if (!string.IsNullOrWhiteSpace(entry.ChatUsername))
        {
            sb.Append(" | @");
            sb.Append(TrimAt(entry.ChatUsername));
        }

        sb.AppendLine();
        sb.Append("First seen=");
        sb.Append(FormatUtc(entry.FirstSeenUtc));
        sb.Append(" | last seen=");
        sb.Append(FormatUtc(entry.LastSeenUtc));
        if (entry.RemovedUtc.HasValue)
        {
            sb.Append(" | removed=");
            sb.Append(FormatUtc(entry.RemovedUtc));
        }

        sb.AppendLine();
        sb.AppendLine();
        sb.Append("Сообщения: ");
        sb.Append(summary.MessageCount);
        sb.Append(" | last=");
        sb.Append(FormatUtc(summary.LastMessageUtc));
        sb.AppendLine();
        sb.Append("Участники: ");
        sb.Append(summary.ParticipantCount);
        sb.Append(" | flagged users=");
        sb.Append(summary.FlaggedUserCount);
        sb.AppendLine();
        sb.Append("Flags: total=");
        sb.Append(summary.FlaggedEventCount);
        sb.Append(" | 24h=");
        sb.Append(summary.RecentFlaggedEventCount);
        sb.Append(" | last=");
        sb.Append(FormatUtc(summary.LastFlaggedUtc));
        sb.AppendLine();
        sb.Append("Risk: total=");
        sb.Append(summary.RiskEventCount);
        sb.Append(" | 24h=");
        sb.Append(summary.RecentRiskEventCount);
        sb.Append(" | last=");
        sb.Append(FormatUtc(summary.LastRiskUtc));
        if (!string.IsNullOrWhiteSpace(summary.LastRiskLabel))
        {
            sb.Append(" | label=");
            sb.Append(summary.LastRiskLabel);
        }

        if (!string.IsNullOrWhiteSpace(summary.LastRiskTopic))
        {
            sb.Append(" | topic=");
            sb.Append(summary.LastRiskTopic);
        }

        if (!string.IsNullOrWhiteSpace(summary.LastRiskIntent))
        {
            sb.Append(" | intent=");
            sb.Append(summary.LastRiskIntent);
        }

        if (summary.LastRiskScore.HasValue)
        {
            sb.Append(" | score=");
            sb.Append(summary.LastRiskScore.Value.ToString("0.##"));
        }

        sb.AppendLine();

        if (riskSnapshot is not null)
        {
            sb.Append("Risk window ");
            sb.Append(_moderationOpt.IncidentWindowMinutes);
            sb.Append("m: score=");
            sb.Append(riskSnapshot.TotalScore.ToString("0.##"));
            sb.Append(" | events=");
            sb.Append(riskSnapshot.EventCount);
            sb.Append(" | participants=");
            sb.Append(riskSnapshot.ParticipantCount);
            if (riskSnapshot.LastAlertedAtUtc.HasValue)
            {
                sb.Append(" | last alert=");
                sb.Append(FormatUtc(riskSnapshot.LastAlertedAtUtc));
            }

            sb.AppendLine();
        }

        if (vigilance is not null)
        {
            sb.Append("Vigilance: ");
            sb.Append(vigilance.IsActiveAt(DateTimeOffset.UtcNow) ? "active" : "inactive");
            sb.Append(" | until=");
            sb.Append(FormatUtc(vigilance.HeightenedUntilUtc));
            sb.Append(" | triggers=");
            sb.Append(vigilance.TriggerCount);
            if (!string.IsNullOrWhiteSpace(vigilance.LastLabel))
            {
                sb.Append(" | label=");
                sb.Append(vigilance.LastLabel);
            }

            if (!string.IsNullOrWhiteSpace(vigilance.LastTopic))
            {
                sb.Append(" | topic=");
                sb.Append(vigilance.LastTopic);
            }

            if (!string.IsNullOrWhiteSpace(vigilance.LastRiskLevel))
            {
                sb.Append(" | risk=");
                sb.Append(vigilance.LastRiskLevel);
            }

            if (vigilance.LastScore.HasValue)
            {
                sb.Append(" | score=");
                sb.Append(vigilance.LastScore.Value.ToString("0.##"));
            }

            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(summary.LastRiskReason))
        {
            sb.AppendLine();
            sb.Append("Последняя причина: ");
            sb.AppendLine(TruncateText(summary.LastRiskReason, 300));
        }

        sb.AppendLine();
        sb.AppendLine("Далее:");
        sb.Append("/admin_participants ");
        sb.Append(chatId);
        sb.AppendLine();
        sb.Append("/admin_flagged_users ");
        sb.Append(chatId);

        await SendPrivateAdminMessageAsync(bot, message.Chat.Id, TruncateText(sb.ToString(), 3500), ct).ConfigureAwait(false);
    }

    private async Task HandleAdminIncidentsAsync(
        ITelegramBotClient bot,
        Message message,
        string text,
        CancellationToken ct)
    {
        if (message.Chat.Type != ChatType.Private || !IsModerator(message.From?.Id))
            return;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        long? chatId = null;
        var page = 1;

        if (parts.Length >= 2)
        {
            var token = parts[1];
            if (TryParseChatIdToken(token, out var parsedChatId))
                chatId = parsedChatId;
            else if (!int.TryParse(token, out page))
            {
                await SendPrivateAdminMessageAsync(bot, message.Chat.Id, "Формат: /admin_incidents [chat_id] [page]\nПример: /admin_incidents -1001234567890 1", ct).ConfigureAwait(false);
                return;
            }
        }

        if (parts.Length >= 3)
        {
            if (!int.TryParse(parts[2], out page))
            {
                await SendPrivateAdminMessageAsync(bot, message.Chat.Id, "page должен быть целым числом.", ct).ConfigureAwait(false);
                return;
            }
        }

        page = Math.Max(1, page);
        var recentWindow = TimeSpan.FromDays(7);
        const int pageSize = 10;

        var total = await _store.CountIncidentFeedAsync(chatId, recentWindow, ct).ConfigureAwait(false);
        if (total <= 0)
        {
            var emptyText = chatId.HasValue
                ? "За последние 7 дней по этому чату нет инцидентных записей."
                : "За последние 7 дней инцидентных записей нет.";

            await SendPrivateAdminMessageAsync(bot, message.Chat.Id, emptyText, ct).ConfigureAwait(false);
            return;
        }

        var pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Clamp(page, 1, pageCount);

        var incidents = await _store
            .GetIncidentFeedPageAsync(chatId, page, pageSize, recentWindow, ct)
            .ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.Append("Инцидентные записи");
        if (chatId.HasValue)
        {
            sb.Append(" по чату ");
            sb.Append(chatId.Value);
        }

        sb.Append(": страница ");
        sb.Append(page);
        sb.Append(" из ");
        sb.Append(pageCount);
        sb.Append(", всего ");
        sb.Append(total);
        sb.AppendLine(".");
        sb.AppendLine();

        var index = (page - 1) * pageSize;
        foreach (var incident in incidents)
        {
            index++;
            var chatDisplay = string.IsNullOrWhiteSpace(incident.ChatTitle)
                ? $"chat {incident.ChatId}"
                : incident.ChatTitle!;
            sb.Append(index);
            sb.Append(") ");
            sb.Append(incident.EventAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'"));
            sb.Append(" | chat=");
            sb.Append(chatDisplay);
            sb.Append(" [id=");
            sb.Append(incident.ChatId);
            sb.Append("] | user=");
            sb.Append(incident.UserId);

            if (!string.IsNullOrWhiteSpace(incident.Username))
            {
                sb.Append(" @");
                sb.Append(TrimAt(incident.Username));
            }

            sb.Append(" | label=");
            sb.Append(incident.Label ?? "unsure");
            sb.Append(" | score=");
            sb.Append(incident.RiskScore.ToString("0.##"));

            if (!string.IsNullOrWhiteSpace(incident.Topic))
            {
                sb.Append(" | topic=");
                sb.Append(incident.Topic);
            }

            if (!string.IsNullOrWhiteSpace(incident.Intent))
            {
                sb.Append(" | intent=");
                sb.Append(incident.Intent);
            }

            if (incident.Confidence.HasValue)
            {
                sb.Append(" | conf=");
                sb.Append(incident.Confidence.Value.ToString("0.##"));
            }

            if (incident.NeedsContextReview)
                sb.Append(" | ctx");

            if (incident.GroupedCount > 1)
            {
                sb.Append(" | grouped=");
                sb.Append(incident.GroupedCount);
            }

            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(incident.MessageText))
            {
                sb.Append("   msg: ");
                sb.AppendLine(TruncateTextForSingleLine(incident.MessageText, 140));
            }

            if (!string.IsNullOrWhiteSpace(incident.Reason))
            {
                sb.Append("   why: ");
                sb.AppendLine(TruncateTextForSingleLine(incident.Reason, 180));
            }
        }

        sb.AppendLine();
        sb.AppendLine("Далее:");
        sb.AppendLine("/admin_health");
        if (chatId.HasValue)
        {
            sb.Append("/admin_chat_summary ");
            sb.Append(chatId.Value);
        }
        else
        {
            sb.Append("/admin_groups tracked");
        }

        await SendPrivateAdminMessageAsync(bot, message.Chat.Id, TruncateText(sb.ToString(), 3500), ct).ConfigureAwait(false);
    }

    private async Task TryRegisterPrivateStartAsync(
        MessageLogRecord record,
        Message message,
        CancellationToken ct)
    {
        try
        {
            var phone = ExtractOwnPhoneNumber(message, record.FromId);
            await ExecuteWithRetryAsync(
                operationName: "sqlite_private_start_register",
                action: retryCt => _store.RegisterPrivateStartAsync(record, phone, retryCt),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Failed to register private /start for user {UserId}",
                record.FromId);
        }
    }

    private async Task TryUpdatePrivateStartProfileFromContactAsync(
        MessageLogRecord record,
        Message message,
        CancellationToken ct)
    {
        var phone = ExtractOwnPhoneNumber(message, record.FromId);
        if (phone is null)
            return;

        try
        {
            await ExecuteWithRetryAsync(
                operationName: "sqlite_private_start_profile_update",
                action: retryCt => _store.UpdatePrivateStartProfileAsync(record, phone, retryCt),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Failed to update private-start profile for user {UserId}",
                record.FromId);
        }
    }

    private async Task SendPrivateStartAsync(
        ITelegramBotClient bot,
        Message message,
        MessageLogRecord record,
        CancellationToken ct)
    {
        var intro = string.IsNullOrWhiteSpace(_botOpt.NonModeratorStartText)
            ? "Ас-саляму алейкум ва рахматуллахи ва баракятух.\n\n" +
              "Я — «Крепость Имана». Помогу делиться дуа, азкарами, хадисами и аятами и, по воле Аллаха, делать вашу группу местом пользы, добрых слов и бараката."
            : _botOpt.NonModeratorStartText!.Trim();

        var sb = new StringBuilder();
        sb.AppendLine(intro);
        sb.AppendLine();
        sb.AppendLine("В группе я могу быть полезен так:");
        sb.AppendLine("— давать хадисы, дуа, азкары и аят дня;");
        sb.AppendLine("— помогать с напоминаниями по теме;");
        sb.AppendLine("— мягко приветствовать новых участников;");
        sb.AppendLine("— по желанию отправлять ежедневное напоминание.");
        sb.AppendLine();
        sb.AppendLine("Примеры команд в группе:");
        sb.AppendLine("/dua знания");
        sb.AppendLine("/azkar утро");
        sb.AppendLine("/hadith терпение");
        sb.AppendLine("/today");
        sb.AppendLine("/help_me тревога");
        sb.AppendLine("/ayat_day");
        sb.AppendLine("/iman_daily_on 09:00");
        sb.AppendLine();
        sb.Append("Добавьте меня в свою группу, и, по воле Аллаха, я помогу наполнить её полезными напоминаниями без лишнего шума.");

        if (IsModerator(record.FromId))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append("Служебные команды модератора: /admin_status");
        }

        var text = sb.ToString();
        var replyMarkup = await TryBuildPrivateStartKeyboardAsync(bot, ct).ConfigureAwait(false);

        try
        {
            await bot.SendMessage(
                chatId: message.Chat.Id,
                text: text,
                replyMarkup: replyMarkup,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to send private /start with keyboard to user {UserId}; retrying without keyboard", record.FromId);
            await bot.SendMessage(
                chatId: message.Chat.Id,
                text: text,
                cancellationToken: ct).ConfigureAwait(false);
        }
    }

    private async Task<InlineKeyboardMarkup?> TryBuildPrivateStartKeyboardAsync(
        ITelegramBotClient bot,
        CancellationToken ct)
    {
        var botUsername = await TryGetBotUsernameAsync(bot, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(botUsername))
            return null;

        return new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithUrl(
                    text: "Добавить в группу",
                    url: $"https://t.me/{TrimAt(botUsername)}?startgroup=true")
            ]
        ]);
    }

    private async Task<string?> TryGetBotUsernameAsync(ITelegramBotClient bot, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_cachedBotUsername))
            return _cachedBotUsername;

        try
        {
            var me = await bot.GetMe(ct).ConfigureAwait(false);
            _cachedBotUsername = me.Username;
            return _cachedBotUsername;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to resolve bot username for private /start");
            return null;
        }
    }

    private static bool TryParseUserIdCommand(string text, out long userId)
    {
        userId = 0;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return false;

        return long.TryParse(parts[1], out userId);
    }

    private static bool TryParseChatIdFromCommand(string text, out long chatId)
    {
        chatId = 0;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return false;

        var token = parts[1].Trim().Trim(',', ';');
        if (token.StartsWith("id=", StringComparison.OrdinalIgnoreCase))
            token = token[3..];

        return long.TryParse(token, out chatId);
    }

    private static bool TryParseBulkTrackFilter(
        string[] parts,
        bool isTracked,
        out ChatRegistryFilter? filter)
    {
        filter = null;
        if (parts.Length < 2)
            return false;

        var token = parts[1].Trim().ToLowerInvariant();
        if (long.TryParse(token, out _))
            return false;

        if (isTracked)
        {
            filter = token switch
            {
                "all" or "active" => ChatRegistryFilter.Active,
                _ => null
            };
        }
        else
        {
            filter = token switch
            {
                "all" => ChatRegistryFilter.All,
                "tracked" => ChatRegistryFilter.Tracked,
                "active" => ChatRegistryFilter.Active,
                _ => null
            };
        }

        return filter.HasValue;
    }

    private static string FormatPrivateAdminText(string text)
    {
        var encoded = EscapeHtmlText(text ?? string.Empty);
        return PrivateAdminCopyableTokenRegex.Replace(encoded, static match => $"<code>{match.Value}</code>");
    }

    private static string FormatPrivateAdminCaption(string text)
        => FormatPrivateAdminText(text);

    private static string EscapeHtmlText(string text)
        => text
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static Task SendPrivateAdminMessageAsync(
        ITelegramBotClient bot,
        long chatId,
        string text,
        CancellationToken ct)
        => bot.SendMessage(
            chatId: chatId,
            text: FormatPrivateAdminText(text),
            parseMode: ParseMode.Html,
            cancellationToken: ct);

    private static void AppendModeratorCommandsList(StringBuilder sb)
    {
        sb.AppendLine("/admin_health");
        sb.AppendLine("/admin_groups");
        sb.AppendLine("/admin_chat_summary");
        sb.AppendLine("/admin_incidents");
        sb.AppendLine("/admin_track");
        sb.AppendLine("/admin_untrack");
        sb.AppendLine("/admin_participants");
        sb.AppendLine("/admin_flagged_users");
        sb.AppendLine("/admin_user_chats");
        sb.AppendLine("/admin_started_users");
        sb.AppendLine();
        sb.AppendLine("Шаблоны:");
        sb.AppendLine("/admin_chat_summary -1001234567890");
        sb.AppendLine("/admin_incidents -1001234567890 1");
        sb.AppendLine("/admin_track all");
        sb.Append("/admin_untrack all");
    }

    private static bool IsDisabledExportCommand(string? command)
    {
        return string.Equals(command, "/admin_dump", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(command, "/admin_participants_dump", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(command, "/admin_user_dump", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(command, "/admin_started_users_dump", StringComparison.OrdinalIgnoreCase);
    }

    private static Task SendDisabledExportSurfaceMessageAsync(
        ITelegramBotClient bot,
        long chatId,
        CancellationToken ct)
    {
        return SendPrivateAdminMessageAsync(
            bot,
            chatId,
            "Выгрузки через бота временно недоступны.",
            ct);
    }

    private static bool TryParseChatIdToken(string token, out long chatId)
    {
        chatId = 0;
        var trimmed = token.Trim().Trim(',', ';');
        if (trimmed.StartsWith("id=", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[3..];

        if (!long.TryParse(trimmed, out var parsed))
            return false;

        if (trimmed.StartsWith("-", StringComparison.Ordinal))
        {
            chatId = parsed;
            return true;
        }

        if (Math.Abs(parsed) < 100000)
            return false;

        chatId = parsed;
        return true;
    }

    private static string BuildUserDisplayName(string? firstName, string? lastName)
    {
        var fullName = $"{firstName} {lastName}".Trim();
        return string.IsNullOrWhiteSpace(fullName) ? "без имени" : fullName;
    }

    private static string? ExtractOwnPhoneNumber(Message message, long? fromId)
    {
        if (!fromId.HasValue || message.Contact is not { } contact)
            return null;

        if (!contact.UserId.HasValue || contact.UserId.Value != fromId.Value)
            return null;

        return NormalizePhone(contact.PhoneNumber);
    }

    private static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return null;

        var trimmed = phone.Trim();
        var sb = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (char.IsDigit(ch))
            {
                sb.Append(ch);
                continue;
            }

            if (ch == '+' && sb.Length == 0)
                sb.Append(ch);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string BuildRegistryChatDisplay(ChatRegistryEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.ChatTitle))
            return entry.ChatTitle;

        if (!string.IsNullOrWhiteSpace(entry.ChatUsername))
            return "@" + TrimAt(entry.ChatUsername);

        return $"chat {entry.ChatId}";
    }

    private static string FormatUtc(DateTimeOffset? value)
        => value.HasValue
            ? value.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'")
            : "—";

    private static string TruncateTextForSingleLine(string text, int maxLength)
    {
        var normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (normalized.Contains("  ", StringComparison.Ordinal))
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);

        return TruncateText(normalized, maxLength);
    }

    private static string? ExtractCommandName(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith("/", StringComparison.Ordinal))
            return null;

        var firstSpace = trimmed.IndexOf(' ');
        var command = firstSpace < 0 ? trimmed : trimmed[..firstSpace];

        var mentionIdx = command.IndexOf('@');
        if (mentionIdx >= 0)
            command = command[..mentionIdx];

        return command;
    }

    private readonly record struct ConversationRiskProcessingResult(
        ConversationRiskSnapshot? Snapshot,
        bool ShouldNotify)
    {
        public static ConversationRiskProcessingResult Empty => new(null, false);
    }

    private readonly record struct ParticipantRosterContext(
        bool BotIsAdmin,
        int? TelegramMemberCount);

    private readonly record struct TrackedStateCacheItem(bool IsTracked, long ResolvedAtUnixMs);
}






