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

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = Array.Empty<UpdateType>(),
            DropPendingUpdates = true
        };

        _bot.StartReceiving(
            updateHandler: _handler,              // ⬅️ объект, не делегат
            receiverOptions: receiverOptions,
            cancellationToken: stoppingToken);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
