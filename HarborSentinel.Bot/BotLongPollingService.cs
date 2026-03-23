using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

public sealed class BotLongPollingService : BackgroundService
{
    private static readonly UpdateType[] ReceiverAllowedUpdates = Enum
        .GetValues<UpdateType>()
        .Where(static updateType => !string.Equals(updateType.ToString(), "Unknown", StringComparison.Ordinal))
        .ToArray();

    private static readonly IReadOnlyList<BotCommand> PublicGroupCommands =
    [
        new() { Command = "hadith", Description = "Хадис" },
        new() { Command = "hadith_topics", Description = "Темы хадисов" },
        new() { Command = "dua", Description = "Дуа" },
        new() { Command = "dua_topics", Description = "Темы дуа" },
        new() { Command = "azkar", Description = "Азкары" },
        new() { Command = "azkar_topics", Description = "Темы азкаров" },
        new() { Command = "ayat_day", Description = "Аят дня" },
        new() { Command = "today", Description = "Польза на сегодня" },
        new() { Command = "help_me", Description = "По состоянию" },
        new() { Command = "about", Description = "О боте" },
        new() { Command = "iman_daily_on", Description = "Включить напоминания" },
        new() { Command = "iman_daily_off", Description = "Отключить напоминания" }
    ];

    private static readonly IReadOnlyList<BotCommand> GroupAdminCommands =
    [
        .. PublicGroupCommands
    ];

    private static readonly IReadOnlyList<(IReadOnlyList<BotCommand> Commands, BotCommandScope Scope, string? LanguageCode, string ScopeName)> CommandTargets =
    [
        (PublicGroupCommands, BotCommandScope.AllGroupChats(), null, "all_group_chats"),
        (PublicGroupCommands, BotCommandScope.AllGroupChats(), "ru", "all_group_chats:ru"),
        (GroupAdminCommands, BotCommandScope.AllChatAdministrators(), null, "all_chat_administrators"),
        (GroupAdminCommands, BotCommandScope.AllChatAdministrators(), "ru", "all_chat_administrators:ru")
    ];

    private readonly ITelegramBotClient _bot;
    private readonly IUpdateHandler _handler;
    private readonly ILogger<BotLongPollingService> _log;

    public BotLongPollingService(
        ITelegramBotClient bot,
        IUpdateHandler handler,
        ILogger<BotLongPollingService> log)
    {
        _bot = bot;
        _handler = handler;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var me = await _bot.GetMe(stoppingToken);
        _log.LogInformation("Telegram bot @{User} is up (id={Id})", me.Username, me.Id);

        await SyncGroupCommandsAsync(stoppingToken).ConfigureAwait(false);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = ReceiverAllowedUpdates,
            DropPendingUpdates = false
        };

        _bot.StartReceiving(
            updateHandler: _handler,
            receiverOptions: receiverOptions,
            cancellationToken: stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("Stopping long polling (cancellation requested).");
        }
    }

    private async Task SyncGroupCommandsAsync(CancellationToken ct)
    {
        try
        {
            foreach (var target in CommandTargets)
            {
                await _bot.SetMyCommands(
                    commands: target.Commands,
                    scope: target.Scope,
                    languageCode: target.LanguageCode,
                    cancellationToken: ct).ConfigureAwait(false);
            }

            _log.LogInformation(
                "Telegram command menus synced (public={PublicCount}, admin={AdminCount}, targets={Targets})",
                PublicGroupCommands.Count,
                GroupAdminCommands.Count,
                CommandTargets.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to sync Telegram group commands");
        }
    }
}
