namespace HarborSentinel.Bot.Models;

public sealed class FlaggedDialogPayload
{
    public FlaggedUser? User { get; init; }
    public FlaggedChat? Chat { get; init; }
    public FlaggedVerdict? Verdict { get; init; }
    public List<DialogMessage>? Dialog { get; init; }
}
