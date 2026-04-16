namespace HarborSentinel.AdminConsole.ViewModels;

// ─── Base ─────────────────────────────────────────────────────────────────────

public abstract class ExportContentBase
{
    public string FilePath { get; init; } = string.Empty;
    public string FileName => System.IO.Path.GetFileName(FilePath);
    public string TypeLabel { get; init; } = "Неизвестный тип";
}

// ─── Flagged Dialog ───────────────────────────────────────────────────────────

public sealed class FlaggedDialogContent : ExportContentBase
{
    public string UserId    { get; init; } = "нет данных";
    public string Username  { get; init; } = "нет";
    public string Person    { get; init; } = "нет";
    public string ChatId    { get; init; } = "нет данных";
    public string ChatTitle { get; init; } = "(без названия)";
    public string Label      { get; init; } = "нет";
    public string Confidence { get; init; } = "нет данных";
    public string Reason     { get; init; } = "нет данных";
    public IReadOnlyList<FlaggedDialogMessageViewModel> Messages { get; init; } = [];

    public string SubjectDisplay
        => string.IsNullOrWhiteSpace(Person) || Person == "нет"
            ? Username
            : $"{Person} ({Username})";

    public string ChatDisplay
        => string.IsNullOrWhiteSpace(ChatTitle) ? ChatId : $"{ChatTitle} ({ChatId})";

    public string LabelSummary => $"{Label} • conf {Confidence}";
}

public sealed class FlaggedDialogMessageViewModel
{
    public string Timestamp     { get; init; } = "";
    public string AuthorDisplay { get; init; } = "";
    public string Text          { get; init; } = "";
    public bool   IsSubjectUser { get; init; }
}

// ─── Chat Messages ─────────────────────────────────────────────────────────────

public sealed class ChatMessagesContent : ExportContentBase
{
    public string ChatId       { get; init; } = "нет данных";
    public string ChatTitle    { get; init; } = "";
    public string ExportedAt   { get; init; } = "нет данных";
    public IReadOnlyList<ExportMessageViewModel> Messages { get; init; } = [];
    public string MessageCount     { get; init; } = "нет данных";
    public string ParticipantCount { get; init; } = "нет данных";
    public string CompletedAt      { get; init; } = "нет данных";

    public string ChatDisplay => string.IsNullOrWhiteSpace(ChatTitle) ? ChatId : $"{ChatTitle}  ({ChatId})";
    public string CountsSummary => $"{MessageCount} сообщений • {ParticipantCount} участников";
}

public sealed class ExportMessageViewModel
{
    public string TimestampUtc { get; init; } = "";
    public string UserDisplay  { get; init; } = "";
    public string Text         { get; init; } = "";
    public string MessageId    { get; init; } = "";
}

// ─── Participants ──────────────────────────────────────────────────────────────

public sealed class ParticipantsContent : ExportContentBase
{
    public string ChatId     { get; init; } = "нет данных";
    public string ExportedAt { get; init; } = "нет данных";
    public IReadOnlyList<ExportParticipantViewModel> Participants { get; init; } = [];
    public string ParticipantCount { get; init; } = "нет данных";
    public string AdminCount       { get; init; } = "нет данных";
    public string BotCount         { get; init; } = "нет данных";
    public string CompletedAt      { get; init; } = "нет данных";

    public string ChatDisplay => ChatId;
    public string CountsSummary => $"{ParticipantCount} участников • {AdminCount} админов • {BotCount} ботов";
}

public sealed class ExportParticipantViewModel
{
    public string UserId       { get; init; } = "";
    public string Username     { get; init; } = "";
    public string FirstName    { get; init; } = "";
    public string LastName     { get; init; } = "";
    public string MessageCount { get; init; } = "";
    public string IsAdmin      { get; init; } = "";
    public string IsBot        { get; init; } = "";
    public string Status       { get; init; } = "";
    public string FirstSeenUtc { get; init; } = "";
    public string LastSeenUtc  { get; init; } = "";
}

// ─── User Messages ─────────────────────────────────────────────────────────────

public sealed class UserMessagesContent : ExportContentBase
{
    public string UserId     { get; init; } = "нет данных";
    public string ChatScope  { get; init; } = "Все чаты";
    public string ExportedAt { get; init; } = "нет данных";
    public IReadOnlyList<ExportUserMessageViewModel> Messages { get; init; } = [];
    public string MessageCount { get; init; } = "нет данных";
    public string ChatCount    { get; init; } = "нет данных";
    public string CompletedAt  { get; init; } = "нет данных";
    public string CountsSummary => $"{MessageCount} сообщений • {ChatCount} чатов";
}

public sealed class ExportUserMessageViewModel
{
    public string TimestampUtc { get; init; } = "";
    public string ChatDisplay  { get; init; } = "";
    public string Text         { get; init; } = "";
    public string IsFlagged    { get; init; } = "";
    public string FlagLabel    { get; init; } = "";
}

// ─── Started Users ─────────────────────────────────────────────────────────────

public sealed class StartedUsersContent : ExportContentBase
{
    public string ExportedAt { get; init; } = "нет данных";
    public IReadOnlyList<ExportStartedUserViewModel> Users { get; init; } = [];
    public string UserCount   { get; init; } = "нет данных";
    public string CompletedAt { get; init; } = "нет данных";
}

public sealed class ExportStartedUserViewModel
{
    public string UserId          { get; init; } = "";
    public string Username        { get; init; } = "";
    public string FirstName       { get; init; } = "";
    public string LastName        { get; init; } = "";
    public string HasPhone        { get; init; } = "";
    public string PhoneMasked     { get; init; } = "";
    public string StartCount      { get; init; } = "";
    public string StartedAt       { get; init; } = "";
    public string LastStartedAt   { get; init; } = "";
}

// ─── Raw JSON Fallback ─────────────────────────────────────────────────────────

public sealed class RawJsonContent : ExportContentBase
{
    public string Json         { get; init; } = string.Empty;
    public string ErrorMessage { get; init; } = string.Empty;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
}
