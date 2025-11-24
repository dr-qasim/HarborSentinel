using HarborSentinel.Bot;
using HarborSentinel.Bot.AI;
using HarborSentinel.Bot.Logging;
using HarborSentinel.Bot.Options;
using HarborSentinel.Bot.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using HarborSentinel.Bot.Hadith;


namespace HarborSentinel.Bot;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((ctx, cfg) =>
            {
                cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                   .AddEnvironmentVariables();
            })
            .ConfigureLogging(log =>
            {
                // консольный логер по умолчанию
                log.ClearProviders();
                log.AddConsole();
                log.SetMinimumLevel(LogLevel.Information);
            })
          .ConfigureServices((ctx, services) =>
          {
              // Options
              services.Configure<BotOptions>(ctx.Configuration.GetSection("Bot"));
              services.Configure<ModerationOptions>(ctx.Configuration.GetSection("Moderation"));
              services.Configure<StorageOptions>(ctx.Configuration.GetSection("Storage"));
              services.Configure<DatabaseOptions>(ctx.Configuration.GetSection("Database"));

              // Telegram
              var token = ctx.Configuration["Bot:Token"] ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
              if (string.IsNullOrWhiteSpace(token))
                  throw new InvalidOperationException("Bot token is not configured. Set Bot:Token or TELEGRAM_BOT_TOKEN.");
              services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(token));

              // HTTP client для DeepSeek
              services.AddHttpClient("moderation", http =>
              {
                  var baseUrl = ctx.Configuration["Moderation:ApiBaseUrl"] ?? "https://api.deepseek.com";
                  http.BaseAddress = new Uri(baseUrl);
                  var timeoutSec = int.TryParse(ctx.Configuration["Moderation:TimeoutSeconds"], out var t) ? t : 15;
                  http.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 5, 60));
              });

              // Наши сервисы
              services.AddSingleton<IRecentMessagesCache, RecentMessagesCache>();
              services.AddSingleton<IMessageStore, SqliteMessageStore>();
              services.AddHostedService<DbInitializer>();

              services.AddSingleton<IFlaggedDialogExporter, FlaggedDialogExporter>();
              services.AddSingleton<IModerationService, DeepSeekModerationService>();

              services.AddSingleton<IAuditSink, JsonlAuditSink>();
              services.AddHostedService(sp => (JsonlAuditSink)sp.GetRequiredService<IAuditSink>());

              services.AddSingleton<Telegram.Bot.Polling.IUpdateHandler, UpdateHandler>();
              services.AddHostedService<BotLongPollingService>();

              services.AddSingleton<DemoHadithProvider>();
              services.AddSingleton<HadeethEncClient>();
              services.AddSingleton<IHadithProvider, HadeethEncHadithProvider>();
              services.AddSingleton<HadithCommandHandler>();
          });


        using var host = builder.Build();
        await host.RunAsync();
    }
}