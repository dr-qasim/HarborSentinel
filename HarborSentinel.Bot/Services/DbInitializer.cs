using Microsoft.Extensions.Hosting;

namespace HarborSentinel.Bot.Services;
public sealed class DbInitializer : IHostedService
{
    private readonly IMessageStore _store;
    public DbInitializer(IMessageStore store) => _store = store;
    public Task StartAsync(CancellationToken ct) => _store.InitAsync(ct);
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
