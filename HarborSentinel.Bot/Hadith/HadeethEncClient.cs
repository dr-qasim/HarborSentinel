using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace HarborSentinel.Bot.Hadith;

/// <summary>
/// Простой клиент для API HadeethEnc (https://hadeethenc.com/api/v1).
/// Пока только GET-запросы, без каких-либо ключей.
/// </summary>
public sealed class HadeethEncClient
{
    // Один общий HttpClient на все экземпляры — так безопаснее для сокетов.
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly ILogger<HadeethEncClient> _log;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri("https://hadeethenc.com/api/v1/"),
            Timeout = TimeSpan.FromSeconds(10)
        };

        return http;
    }

    public HadeethEncClient(ILogger<HadeethEncClient> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    private static async Task<T?> DeserializeAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false);
    }

    private async Task<T?> GetAsync<T>(string relativeUrl, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(relativeUrl, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("HadeethEnc GET {Url} failed with status {Status}",
                    relativeUrl, response.StatusCode);
                return default;
            }

            return await DeserializeAsync<T>(response, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "HadeethEnc GET {Url} failed", relativeUrl);
            return default;
        }
    }

    /// <summary>
    /// Список доступных языков (код + название).
    /// Пример: code = "ru", native = "Русский".
    /// </summary>
    public Task<LanguageDto[]?> GetLanguagesAsync(CancellationToken ct = default)
        => GetAsync<LanguageDto[]>("languages", ct);

    /// <summary>
    /// Список категорий для указанного языка (id, title, количество хадисов и т.д.).
    /// </summary>
    public Task<CategoryDto[]?> GetCategoriesAsync(string language, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(language))
            throw new ArgumentException("Language must be non-empty", nameof(language));

        var url = $"categories/list/?language={Uri.EscapeDataString(language)}";
        return GetAsync<CategoryDto[]>(url, ct);
    }

    /// <summary>
    /// Список хадисов в категории.
    /// </summary>
    public Task<HadeethsListResponseDto?> GetHadeethsListAsync(
        string language,
        string categoryId,
        int page = 1,
        int perPage = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(language))
            throw new ArgumentException("Language must be non-empty", nameof(language));
        if (string.IsNullOrWhiteSpace(categoryId))
            throw new ArgumentException("CategoryId must be non-empty", nameof(categoryId));
        if (page < 1) page = 1;
        if (perPage < 1) perPage = 1;

        var url =
            $"hadeeths/list/?language={Uri.EscapeDataString(language)}" +
            $"&category_id={Uri.EscapeDataString(categoryId)}" +
            $"&page={page}&per_page={perPage}";

        return GetAsync<HadeethsListResponseDto>(url, ct);
    }

    /// <summary>
    /// Детали одного хадиса по id и языку.
    /// Для неарабских языков возвращает перевод текста и пояснения.
    /// </summary>
    public Task<HadeethOneResponseDto?> GetHadeethOneAsync(
        string language,
        string id,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(language))
            throw new ArgumentException("Language must be non-empty", nameof(language));
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Id must be non-empty", nameof(id));

        var url =
            $"hadeeths/one/?language={Uri.EscapeDataString(language)}" +
            $"&id={Uri.EscapeDataString(id)}";

        return GetAsync<HadeethOneResponseDto>(url, ct);
    }
}

/// <summary>
/// Описание языка в HadeethEnc.
/// </summary>
public sealed class LanguageDto
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("native")]
    public string Native { get; set; } = "";
}

/// <summary>
/// Категория хадисов.
/// </summary>
public sealed class CategoryDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("hadeeths_count")]
    public string HadeethsCount { get; set; } = "";

    [JsonPropertyName("parent_id")]
    public string? ParentId { get; set; }
}

/// <summary>
/// Ответ списка хадисов: data + meta.
/// </summary>
public sealed class HadeethsListResponseDto
{
    [JsonPropertyName("data")]
    public HadeethListItemDto[] Data { get; set; } = Array.Empty<HadeethListItemDto>();

    [JsonPropertyName("meta")]
    public HadeethsListMetaDto Meta { get; set; } = new();
}

public sealed class HadeethListItemDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("translations")]
    public string[] Translations { get; set; } = Array.Empty<string>();
}

public sealed class HadeethsListMetaDto
{
    [JsonPropertyName("current_page")]
    public string CurrentPage { get; set; } = "";

    [JsonPropertyName("last_page")]
    public int LastPage { get; set; }

    [JsonPropertyName("total_items")]
    public int TotalItems { get; set; }

    [JsonPropertyName("per_page")]
    public string PerPage { get; set; } = "";
}

/// <summary>
/// Детальный ответ по одному хадису.
/// Для неарабских языков тут будет перевод текста + пояснение.
/// </summary>
public sealed class HadeethOneResponseDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    // Собственно текст хадиса (перевод).
    [JsonPropertyName("hadeeth")]
    public string Hadeeth { get; set; } = "";

    // Пояснение (тоже перевод).
    [JsonPropertyName("explanation")]
    public string Explanation { get; set; } = "";
}
