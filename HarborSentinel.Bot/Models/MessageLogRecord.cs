using Telegram.Bot.Types;

namespace HarborSentinel.Bot.Models;

public sealed class MessageLogRecord
{
    // Время логирования (UTC)
    public required DateTimeOffset Timestamp { get; init; }

    // Telegram-метаданные (чат)
    public required long ChatId { get; init; }
    public string? ChatTitle { get; init; }
    public string? ChatUsername { get; init; }
    public required string ChatType { get; init; }

    // Telegram-метаданные (пользователь)
    public required long? FromId { get; init; }
    public string? FromUsername { get; init; }
    public string? FromFirstName { get; init; }
    public string? FromLastName { get; init; }
    public string? FromLanguageCode { get; init; }

    // Сообщение
    public required int MessageId { get; init; }
    public required string MessageType { get; init; }

    // Контент
    public string? Text { get; init; }
    public bool HasEntities { get; init; }
    public int? PhotoCount { get; init; }
    public bool HasDocument { get; init; }
    public bool HasAudio { get; init; }
    public bool HasVideo { get; init; }
    public bool HasVoice { get; init; }

    // Вердикт ИИ (заполним на следующих шагах)
    public AiVerdict? Ai { get; set; }

    /// <summary>Создаёт запись лога из Update/Message.</summary>
    public static MessageLogRecord FromTelegram(Update update, Message m)
    {
        return new MessageLogRecord
        {
            Timestamp = NormalizeTelegramTimestamp(m.Date),

            ChatId = m.Chat.Id,
            ChatTitle = m.Chat.Title,
            ChatUsername = m.Chat.Username,
            ChatType = m.Chat.Type.ToString(),

            FromId = m.From?.Id,
            FromUsername = m.From?.Username,
            FromFirstName = m.From?.FirstName,
            FromLastName = m.From?.LastName,
            FromLanguageCode = m.From?.LanguageCode,

            MessageId = m.MessageId,
            MessageType = m.Type.ToString(),

            Text = m.Text ?? m.Caption,
            HasEntities = ((m.Entities?.Count() ?? 0) + (m.CaptionEntities?.Count() ?? 0)) > 0,
            PhotoCount = m.Photo?.Length,
            HasDocument = m.Document is not null,
            HasAudio = m.Audio is not null,
            HasVideo = m.Video is not null,
            HasVoice = m.Voice is not null,

            Ai = null
        };
    }

    private static DateTimeOffset NormalizeTelegramTimestamp(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return new DateTimeOffset(utc);
    }
}

/// <summary>Короткая форма вердикта ИИ.</summary>
public sealed class AiVerdict
{
    /// <summary>Например: "extremism" | "terrorism" | "narcotics" | "neutral" | "unsure".</summary>
    public string? Label { get; init; }

    /// <summary>Доля уверенности модели (0..1).</summary>
    public double? Confidence { get; init; }

    /// <summary>Итоговый флаг (true — нарушение, false — нет, null — не решали/ошибка).</summary>
    public bool? Flagged { get; init; }

    /// <summary>Краткое объяснение/причина.</summary>
    public string? Reason { get; init; }

    /// <summary>Семантическая тема сообщения/диалога.</summary>
    public string? Topic { get; init; }

    /// <summary>Предполагаемое намерение автора.</summary>
    public string? Intent { get; init; }

    /// <summary>Позиция автора по отношению к опасному содержанию.</summary>
    public string? Stance { get; init; }

    /// <summary>Общий уровень риска по оценке модели.</summary>
    public string? RiskLevel { get; init; }

    /// <summary>Нужен ли дополнительный анализ контекста.</summary>
    public bool? NeedsContextReview { get; init; }

    /// <summary>Предположительно релевантные статьи УК РФ.</summary>
    public IReadOnlyList<string>? UkArticleCandidates { get; init; }

    /// <summary>Накопительный риск по окну сообщений.</summary>
    public double? IncidentScore { get; init; }

    /// <summary>Количество событий, вошедших в накопительный риск.</summary>
    public int? IncidentEventCount { get; init; }

    /// <summary>Вердикт получен из деградационного локального fallback-контра, а не из LLM.</summary>
    public bool IsModelFailureFallback { get; init; }
}
