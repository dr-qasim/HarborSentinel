namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class ChatFilterOptionViewModel<T>
{
    public required string Label { get; init; }
    public required T Value { get; init; }

    public override string ToString() => Label;
}
