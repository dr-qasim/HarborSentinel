using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HarborSentinel.Bot.Hadith;

public sealed class DemoHadithProvider : IHadithProvider
{
    private readonly HadithEntry[] _hadiths;

    public DemoHadithProvider()
    {
        _hadiths = LoadFromJsonOrFallback();
    }

    private static HadithEntry[] LoadFromJsonOrFallback()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var dataDir = Path.Combine(baseDir, "data");
            var filePath = Path.Combine(dataDir, "hadiths-ru.json");

            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);

                var items = JsonSerializer.Deserialize<HadithEntry[]>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (items is { Length: > 0 })
                    return items;
            }
        }
        catch
        {

        }

        return new[]
        {
            new HadithEntry
            {
                Id = "demo_niyyah",
                TextRu = "ДЕМО: Хадис о намерениях (сюда позже вставишь настоящий текст перевода).",
                Source = "Источник (например: аль-Бухари и Муслим, хадис о намерениях).",
                ExplanationRu = "Краткое объяснение о том, что деяния оцениваются по намерениям.",
                TopicTags = new[] { "намерение", "искренность" }
            },
            new HadithEntry
            {
                Id = "demo_sabr",
                TextRu = "ДЕМО: Хадис о терпении при испытаниях (сюда позже вставишь настоящий текст).",
                Source = "Источник хадиса о терпении.",
                ExplanationRu = "Краткое пояснение о важности сабра при испытаниях.",
                TopicTags = new[] { "терпение", "испытания" }
            },
            new HadithEntry
            {
                Id = "demo_akhlaq",
                TextRu = "ДЕМО: Хадис о добром нраве и мягкости к людям.",
                Source = "Источник хадиса о благонравии.",
                ExplanationRu = "Краткое объяснение о важности хорошего отношения к людям.",
                TopicTags = new[] { "нрав", "добро", "отношения", "мягкость" }
            }
        };
    }

    public Task<HadithEntry?> GetRandomAsync(CancellationToken ct = default)
    {
        if (_hadiths.Length == 0)
            return Task.FromResult<HadithEntry?>(null);

        var index = Random.Shared.Next(0, _hadiths.Length);
        return Task.FromResult<HadithEntry?>(_hadiths[index]);
    }

    public Task<HadithEntry?> GetRandomByTopicAsync(string topicQuery, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(topicQuery))
            return GetRandomAsync(ct);

        topicQuery = topicQuery.Trim();

        var byTopic = _hadiths
            .Where(h => h.TopicTags.Any(
                tag => tag.Contains(topicQuery, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (byTopic.Length == 0)
            return Task.FromResult<HadithEntry?>(null);

        var index = Random.Shared.Next(0, byTopic.Length);
        return Task.FromResult<HadithEntry?>(byTopic[index]);
    }

    public Task<string[]> GetAllTopicsAsync(CancellationToken ct = default)
    {
        var topics = _hadiths
            .SelectMany(h => h.TopicTags ?? Array.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t)
            .ToArray();

        return Task.FromResult(topics);
    }
}
