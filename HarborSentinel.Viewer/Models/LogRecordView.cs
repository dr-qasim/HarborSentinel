namespace HarborSentinel.Viewer.Models;

public sealed class LogRecordView
{
    public string SourceFile { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;

    public DateTimeOffset? Timestamp { get; set; }

    public long? ChatId { get; set; }
    public string? ChatTitle { get; set; }
    public string? ChatUsername { get; set; }

    public long? UserId { get; set; }
    public string? Username { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }

    public int? MessageId { get; set; }
    public string? Text { get; set; }

    public bool Flagged { get; set; }
    public string? Label { get; set; }
    public double? Confidence { get; set; }
    public string? Reason { get; set; }

    public string RawJson { get; set; } = string.Empty;

    public string DisplayTime =>
        Timestamp?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "n/a";

    public string DisplayChat
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ChatTitle))
                return ChatTitle;

            if (!string.IsNullOrWhiteSpace(ChatUsername))
                return "@" + TrimAt(ChatUsername);

            return ChatId?.ToString() ?? "chat n/a";
        }
    }

    public string DisplayUser
    {
        get
        {
            var fullName = $"{FirstName} {LastName}".Trim();

            if (!string.IsNullOrWhiteSpace(Username))
            {
                if (!string.IsNullOrWhiteSpace(fullName))
                    return $"{fullName} @{TrimAt(Username)}";

                return "@" + TrimAt(Username);
            }

            if (!string.IsNullOrWhiteSpace(fullName))
                return fullName;

            return UserId.HasValue ? $"id={UserId.Value}" : "user n/a";
        }
    }

    public string DisplayHeader => $"{DisplayUser} • {DisplayChat}";

    public string TextPreview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Text))
                return "—";

            const int max = 220;
            if (Text.Length <= max)
                return Text;

            return Text[..max] + "…";
        }
    }

    public string DisplayLabel
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Label))
                return Flagged ? "flagged" : "neutral";

            return Label!;
        }
    }

    public string DisplayMessageId => MessageId?.ToString() ?? "n/a";

    public string DisplayConfidence =>
        Confidence.HasValue ? $"{Confidence.Value:P0}" : "n/a";

    public string DisplayChatWithId =>
        $"{DisplayChat} (chat_id={ChatId?.ToString() ?? "n/a"})";

    public string DisplayUserWithId =>
        $"{DisplayUser} (user_id={UserId?.ToString() ?? "n/a"})";

    public string DisplaySourceInfo =>
        $"msg_id={DisplayMessageId} • source={SourceKind}";

    public string DisplayVerdictQuote
    {
        get
        {
            if (!Flagged)
                return string.Empty;

            if (string.IsNullOrWhiteSpace(Reason))
                return $"⚠ «Вердикт ИИ: {DisplayLabel}, уверенность {DisplayConfidence}»";

            return $"⚠ «Вердикт ИИ: {DisplayLabel}, уверенность {DisplayConfidence}. Причина: {Reason}»";
        }
    }

    public string DisplayListLine
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(TextPreview) ? "—" : TextPreview;
            return $"{DisplayTime}  |  {DisplayUser}\n{DisplayLabel} ({DisplayConfidence})\n{text}";
        }
    }

    private static string TrimAt(string value)
        => value.StartsWith("@", StringComparison.Ordinal) ? value[1..] : value;

    public override string ToString() => DisplayListLine;
}
