namespace HarborSentinel.Bot.Islamic;

public sealed class DuaEntry
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Arabic { get; init; }
    public required string Transliteration { get; init; }
    public required string TranslationRu { get; init; }
    public required string WhenToReadRu { get; init; }
    public required string HowToReadRu { get; init; }
    public required string Source { get; init; }
    public string[] TopicTags { get; init; } = [];
}

public sealed class AzkarEntry
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Arabic { get; init; }
    public required string Transliteration { get; init; }
    public required string TranslationRu { get; init; }
    public required string TimeHintRu { get; init; }
    public string? RecommendedCount { get; init; }
    public required string Source { get; init; }
    public string[] TopicTags { get; init; } = [];
}

public sealed class AyahEntry
{
    public required string Id { get; init; }
    public required string SurahNameRu { get; init; }
    public required string SurahNameAr { get; init; }
    public required int SurahNumber { get; init; }
    public required string AyahNumber { get; init; }
    public required string Arabic { get; init; }
    public required string Transliteration { get; init; }
    public required string TranslationRu { get; init; }
    public required string ReflectionRu { get; init; }
}
