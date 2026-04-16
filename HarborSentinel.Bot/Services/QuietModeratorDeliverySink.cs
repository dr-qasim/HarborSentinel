using HarborSentinel.Bot.Models;
using Microsoft.Extensions.Logging;

namespace HarborSentinel.Bot.Services;

public sealed class QuietModeratorDeliverySink : IModeratorDeliverySink
{
    private readonly ILogger<QuietModeratorDeliverySink> _log;

    public QuietModeratorDeliverySink(ILogger<QuietModeratorDeliverySink> log)
    {
        _log = log;
    }

    public Task PublishAsync(ModeratorDeliveryEvent notification, CancellationToken ct)
    {
        var riskScore = notification.ConversationRisk?.TotalScore;
        var riskEvents = notification.ConversationRisk?.EventCount;

        _log.LogInformation(
            "Automatic moderator delivery suppressed ({Kind}); chat={ChatId} user={UserId} msg={MessageId} label={Label} risk={RiskScore} events={RiskEvents}",
            notification.Kind,
            notification.Record.ChatId,
            notification.Record.FromId,
            notification.Record.MessageId,
            notification.Record.Ai?.Label,
            riskScore,
            riskEvents);

        return Task.CompletedTask;
    }
}
