using Telegram.Bot;
using Telegram.Bot.Polling;   // ДОБАВЬ using
using Telegram.Bot.Types.Enums;

public sealed class BotLongPollingService : BackgroundService
{
    private readonly ITelegramBotClient _bot;
    private readonly IUpdateHandler _handler; // теперь библиотечный интерфейс
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

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = Array.Empty<UpdateType>(),
            DropPendingUpdates = true
        };

        _bot.StartReceiving(updateHandler: _handler,
                            receiverOptions: receiverOptions,
                            cancellationToken: stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // штатная остановка — молча выходим
            _log.LogInformation("Stopping long polling (cancellation requested).");
        }
    }
}
