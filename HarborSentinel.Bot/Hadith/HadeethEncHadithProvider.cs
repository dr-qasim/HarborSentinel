using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HarborSentinel.Bot.Hadith;

/// <summary>
/// Провайдер хадисов, который сначала пытается получить хадис из HadeethEnc,
/// а при любой ошибке или отсутствии данных падает обратно на локальный DemoHadithProvider.
/// </summary>
public sealed class HadeethEncHadithProvider : IHadithProvider
{
    private readonly HadeethEncClient _client;
    private readonly DemoHadithProvider _fallback;
    private readonly ILogger<HadeethEncHadithProvider> _log;
    private readonly string _language = "ru";


    private CategoryDto[]? _cachedCategories;
    private readonly object _categoriesLock = new();

    public HadeethEncHadithProvider(
        HadeethEncClient client,
        DemoHadithProvider fallback,
        ILogger<HadeethEncHadithProvider> log)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    private async Task<CategoryDto[]?> GetCategoriesCachedAsync(CancellationToken ct)
    {
        if (_cachedCategories is not null && _cachedCategories.Length > 0)
            return _cachedCategories;

        lock (_categoriesLock)
        {
            if (_cachedCategories is not null && _cachedCategories.Length > 0)
                return _cachedCategories;
        }

        var cats = await _client.GetCategoriesAsync(_language, ct).ConfigureAwait(false);
        if (cats is null || cats.Length == 0)
        {
            _log.LogWarning("HadeethEnc: no categories for language {Lang}", _language);
            return null;
        }

        lock (_categoriesLock)
        {
            if (_cachedCategories is null || _cachedCategories.Length == 0)
                _cachedCategories = cats;
        }

        return _cachedCategories;
    }

    public async Task<HadithEntry?> GetRandomAsync(CancellationToken ct = default)
    {
        try
        {
            var cats = await GetCategoriesCachedAsync(ct).ConfigureAwait(false);
            if (cats is null || cats.Length == 0)
            {
                _log.LogWarning("HadeethEnc: categories empty, falling back to local provider");
                return await _fallback.GetRandomAsync(ct).ConfigureAwait(false);
            }

            var cat = cats[Random.Shared.Next(0, cats.Length)];

            var list = await _client.GetHadeethsListAsync(
                _language,
                cat.Id,
                page: 1,
                perPage: 20,
                ct).ConfigureAwait(false);

            var items = list?.Data;
            if (items is null || items.Length == 0)
            {
                _log.LogWarning("HadeethEnc: no hadeeths in category {CatId}, falling back", cat.Id);
                return await _fallback.GetRandomAsync(ct).ConfigureAwait(false);
            }

            var item = items[Random.Shared.Next(0, items.Length)];

            var one = await _client.GetHadeethOneAsync(_language, item.Id, ct).ConfigureAwait(false);
            if (one is null || string.IsNullOrWhiteSpace(one.Hadeeth))
            {
                _log.LogWarning("HadeethEnc: failed to load hadeeth {Id}, falling back", item.Id);
                return await _fallback.GetRandomAsync(ct).ConfigureAwait(false);
            }
            _log.LogInformation("HadeethEnc: random hadeeth id={Id} from category {CatId} ({CatTitle})",
                one.Id, cat.Id, cat.Title);
            return MapToEntry(one, catTitle: cat.Title);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "HadeethEnc: error in GetRandomAsync, falling back");
            return await _fallback.GetRandomAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<HadithEntry?> GetRandomByTopicAsync(string topicQuery, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(topicQuery))
            return await GetRandomAsync(ct).ConfigureAwait(false);

        topicQuery = topicQuery.Trim();

        try
        {
            var cats = await GetCategoriesCachedAsync(ct).ConfigureAwait(false);
            if (cats is null || cats.Length == 0)
            {
                _log.LogWarning("HadeethEnc: categories empty in GetRandomByTopic, falling back");
                return await _fallback.GetRandomByTopicAsync(topicQuery, ct).ConfigureAwait(false);
            }

            // Нормализуем запрос и делаем "корень" слова
            var queryNorm = NormalizeText(topicQuery);
            var queryStem = MakeStem(queryNorm);

            var matchedCats = cats
                .Where(c => IsCategoryMatch(c.Title, queryNorm, queryStem))
                .ToArray();

            if (matchedCats.Length == 0)
            {
                _log.LogInformation(
                    "HadeethEnc: no categories matched topic '{Topic}' (normalized='{Norm}', stem='{Stem}')",
                    topicQuery, queryNorm, queryStem);
                return await _fallback.GetRandomByTopicAsync(topicQuery, ct).ConfigureAwait(false);
            }

            var cat = matchedCats[Random.Shared.Next(0, matchedCats.Length)];

            var list = await _client.GetHadeethsListAsync(
                _language,
                cat.Id,
                page: 1,
                perPage: 20,
                ct).ConfigureAwait(false);

            var items = list?.Data;
            if (items is null || items.Length == 0)
            {
                _log.LogWarning(
                    "HadeethEnc: no hadeeths in category {CatId} ({CatTitle}) for topic '{Topic}'",
                    cat.Id, cat.Title, topicQuery);
                return await _fallback.GetRandomByTopicAsync(topicQuery, ct).ConfigureAwait(false);
            }

            var item = items[Random.Shared.Next(0, items.Length)];

            var one = await _client.GetHadeethOneAsync(_language, item.Id, ct).ConfigureAwait(false);
            if (one is null || string.IsNullOrWhiteSpace(one.Hadeeth))
            {
                _log.LogWarning(
                    "HadeethEnc: failed to load hadeeth {Id} for topic '{Topic}'",
                    item.Id, topicQuery);
                return await _fallback.GetRandomByTopicAsync(topicQuery, ct).ConfigureAwait(false);
            }

            _log.LogInformation(
                "HadeethEnc: topic='{Topic}', hadeeth id={Id} from category {CatId} ({CatTitle})",
                topicQuery, one.Id, cat.Id, cat.Title);

            return MapToEntry(one, catTitle: cat.Title, topicQuery: topicQuery);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "HadeethEnc: error in GetRandomByTopicAsync(topic='{Topic}')", topicQuery);
            return await _fallback.GetRandomByTopicAsync(topicQuery, ct).ConfigureAwait(false);
        }
    }

    public async Task<string[]> GetAllTopicsAsync(CancellationToken ct = default)
    {
        try
        {
            var cats = await GetCategoriesCachedAsync(ct).ConfigureAwait(false);
            if (cats is null || cats.Length == 0)
            {
                _log.LogWarning("HadeethEnc: categories empty in GetAllTopicsAsync, falling back");
                return await _fallback.GetAllTopicsAsync(ct).ConfigureAwait(false);
            }

            var topics = cats
                .Select(c => c.Title)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t)
                .ToArray();

            if (topics.Length == 0)
                return await _fallback.GetAllTopicsAsync(ct).ConfigureAwait(false);

            return topics;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "HadeethEnc: error in GetAllTopicsAsync, falling back");
            return await _fallback.GetAllTopicsAsync(ct).ConfigureAwait(false);
        }
    }

    private static HadithEntry MapToEntry(HadeethOneResponseDto dto, string? catTitle, string? topicQuery = null)
    {
        // Формируем теги: категория +, возможно, исходный текст запроса
        var tags = Array.Empty<string>();
        if (!string.IsNullOrWhiteSpace(catTitle) || !string.IsNullOrWhiteSpace(topicQuery))
        {
            tags = new[]
            {
                catTitle?.Trim(),
                topicQuery?.Trim()
            }.Where(s => !string.IsNullOrWhiteSpace(s))
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .ToArray()!;
        }

        return new HadithEntry
        {
            Id = dto.Id,
            TextRu = dto.Hadeeth,
            TextAr = null,
            Source = $"HadeethEnc.com, id={dto.Id}, язык=ru",
            ExplanationRu = string.IsNullOrWhiteSpace(dto.Explanation) ? null : dto.Explanation,
            TopicTags = tags
        };
    }

    private static string NormalizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            if (ch == 'ё')
            {
                sb.Append('е');
            }
            else if (char.IsLetter(ch))
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append(' ');
            }
        }

        return sb.ToString();
    }

    private static string MakeStem(string normalized)
    {
        var s = normalized.Trim();
        if (s.Length <= 4)
            return s;

        // обрезаем последние 2 символа, чтобы убрать окончания типа -ие, -ый, -ая и т.п.
        return s[..(s.Length - 2)];
    }

    /// <summary>
    /// Проверяем, подходит ли категория под заданную тему:
    /// сравниваем нормализованный заголовок и его слова с нормализованным запросом.
    /// </summary>
    private static bool IsCategoryMatch(string? title, string queryNorm, string queryStem)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var titleNorm = NormalizeText(title);
        if (string.IsNullOrEmpty(titleNorm))
            return false;

        if (!string.IsNullOrEmpty(queryNorm) &&
            titleNorm.Contains(queryNorm, StringComparison.Ordinal))
            return true;

        var words = titleNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var w in words)
        {
            if (w.Length == 0) continue;

            if (w.Contains(queryStem, StringComparison.Ordinal) ||
                queryStem.Contains(w, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
