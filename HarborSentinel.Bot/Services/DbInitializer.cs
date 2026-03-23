using Microsoft.Extensions.Hosting;

namespace HarborSentinel.Bot.Services;
public sealed class DbInitializer : IHostedService
{
    private readonly IMessageStore _store;
    private readonly IChatRegistry _chatRegistry;

    public DbInitializer(IMessageStore store, IChatRegistry chatRegistry)
    {
        _store = store;
        _chatRegistry = chatRegistry;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _store.InitAsync(ct);
        await _chatRegistry.InitAsync(ct);
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
