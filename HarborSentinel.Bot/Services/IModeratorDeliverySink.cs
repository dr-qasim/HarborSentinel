using HarborSentinel.Bot.Models;

namespace HarborSentinel.Bot.Services;

public interface IModeratorDeliverySink
{
    Task PublishAsync(ModeratorDeliveryEvent notification, CancellationToken ct);
}
