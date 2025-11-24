using HarborSentinel.Bot.Models;
using HarborSentinel.Bot.Options;
using HarborSentinel.Bot.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Globalization;


namespace HarborSentinel.Bot.AI
{
    public sealed class DeepSeekModerationService : IModerationService
    {
        private readonly ILogger<DeepSeekModerationService> _log;
        private readonly IHttpClientFactory _httpFactory;
        private readonly ModerationOptions _opt;
        private readonly SemaphoreSlim _gate;

        private static readonly JsonSerializerOptions JsonOpt = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // Короткий системный промпт — строгая классификация и только JSON
        private const string SystemPrompt =
         "Ты — многоязычный модератор контента. Оцени соответствие сообщения законодательству РФ. " +
         "Классифицируй ровно в один ярлык из списка: " +
         "['extremism','terrorism','narcotics','violence','hate_speech','weapons_illegal'," +
         "'child_exploitation','distribution_illegal_materials','suicide_selfharm','other_illegal'," +
         "'neutral','unsure']. " +
         "Верни ТОЛЬКО JSON в ОДНУ СТРОКУ: {\"label\":string, \"confidence\":number, \"reasons\":string}. " +
         "Не добавляй переносов строк, не добавляй ничего, кроме JSON.";


        public DeepSeekModerationService(
            ILogger<DeepSeekModerationService> log,
            IHttpClientFactory httpFactory,
            IOptions<ModerationOptions> opt)
        {
            _log = log;
            _httpFactory = httpFactory;
            _opt = opt.Value;
            if (string.IsNullOrWhiteSpace(_opt.ApiKey))
                _opt.ApiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY"); // запасной вариант
            _gate = new SemaphoreSlim(Math.Max(1, _opt.MaxConcurrency));
        }
        private string? ResolveApiKey()
        {
            if (!string.IsNullOrWhiteSpace(_opt.ApiKey))
                return _opt.ApiKey;

            var fromOptionsEnv = Environment.GetEnvironmentVariable("Moderation__ApiKey");
            if (!string.IsNullOrWhiteSpace(fromOptionsEnv))
                return fromOptionsEnv;

            var fromLegacyEnv = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
            return string.IsNullOrWhiteSpace(fromLegacyEnv) ? null : fromLegacyEnv;
        }

        public async Task<AiVerdict> ModerateAsync(string text, CancellationToken ct)
        {
            if (!_opt.Enabled)
                return new AiVerdict { Label = "disabled", Confidence = 0, Flagged = null, Reason = "moderation disabled" };
            
            var apiKey = ResolveApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _log.LogWarning("DeepSeek API key not configured (checked Moderation:ApiKey, env Moderation__ApiKey, env DEEPSEEK_API_KEY); skipping moderation");
                return new AiVerdict { Label = "unsure", Confidence = 0, Flagged = null, Reason = "no_api_key" };
            }

            var sanitized = Sanitize(text);

            await _gate.WaitAsync(ct);
            try
            {
                var client = _httpFactory.CreateClient("moderation");

                var request = new ChatCompletionsRequest
                {
                    Model = _opt.Model,
                    Messages =
                    [
                        new ChatMessage("system", SystemPrompt),
                    new ChatMessage("user", sanitized)
                    ],
                    ResponseFormat = new ResponseFormat { Type = "json_object" },
                    Temperature = 0,
                    MaxTokens = 120
                };

                using var http = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
                {
                    Content = new StringContent(JsonSerializer.Serialize(request, JsonOpt), Encoding.UTF8, "application/json")
                };

                http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                using var resp = await client.SendAsync(http, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    _log.LogWarning("LLM HTTP {Status}: {Body}", (int)resp.StatusCode, body);
                    return new AiVerdict { Label = "unsure", Confidence = 0, Flagged = null, Reason = $"http_{(int)resp.StatusCode}" };
                }

                var parsed = JsonSerializer.Deserialize<ChatCompletionsResponse>(body, JsonOpt);
                var content = parsed?.Choices?.FirstOrDefault()?.Message?.Content;

                if (string.IsNullOrWhiteSpace(content))
                    return new AiVerdict { Label = "unsure", Confidence = 0, Flagged = null, Reason = "empty_content" };

                var cls = TryParseClassification(content);
                var label = (cls?.Label ?? "unsure").Trim().ToLowerInvariant();
                var confidence = cls?.Confidence ?? 0.0;
                var flagged = label is not ("neutral" or "unsure") && confidence >= _opt.ConfidenceThreshold;

                return new AiVerdict
                {
                    Label = label,
                    Confidence = confidence,
                    Flagged = flagged,
                    Reason = cls?.Reasons ?? $"label={label}, confidence={confidence:F2}"
                };
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "LLM error");
                return new AiVerdict { Label = "unsure", Confidence = 0, Flagged = null, Reason = "llm_error" };
            }
            finally
            {
                _gate.Release();
            }
        }
        private Classification? TryParseClassification(string content)
        {
            // 1) Нормальный JSON
            try
            {
                var parsed = JsonSerializer.Deserialize<Classification>(content, JsonOpt);
                if (parsed is not null) return parsed;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "LLM returned non-JSON content: {Content}", content);
            }

            // 2) Fallback: выдернем поля регэкспами из «полу-JSON»
            // label: строка в кавычках
            var mLabel = Regex.Match(content, "\"label\"\\s*:\\s*\"(?<l>[^\"]+)", RegexOptions.IgnoreCase);
            // confidence: число (1, 0.85, 0.9 и т.п.)
            var mConf = Regex.Match(content, "\"confidence\"\\s*:\\s*(?<c>[0-9]+(?:\\.[0-9]+)?)", RegexOptions.IgnoreCase);
            // reasons: всё, что после "reasons":"..., возможно с незакрытой кавычкой/фигурной скобкой
            var mReas = Regex.Match(content, "\"reasons\"\\s*:\\s*\"(?<r>.+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);

            string? label = mLabel.Success ? mLabel.Groups["l"].Value : null;

            double? conf = null;
            if (mConf.Success &&
                double.TryParse(mConf.Groups["c"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cVal))
            {
                conf = cVal;
            }

            string? reasons = null;
            if (mReas.Success)
            {
                reasons = mReas.Groups["r"].Value;
                // подчистим незакрытый хвост
                reasons = reasons.Replace("\r", "").Replace("\n", " ");
                reasons = reasons.TrimEnd(' ', '"', '}');
                if (reasons.Length > 300) reasons = reasons[..300] + "…";
            }

            if (label is null && conf is null && reasons is null) return null;
            return new Classification { Label = label, Confidence = conf, Reasons = reasons };
        }

        // DTOs для OpenAI-совместимого API
        private sealed record ChatCompletionsRequest
        {
            [JsonPropertyName("model")] public required string Model { get; init; }
            [JsonPropertyName("messages")] public required List<ChatMessage> Messages { get; init; }
            [JsonPropertyName("response_format")] public ResponseFormat? ResponseFormat { get; init; }
            [JsonPropertyName("temperature")] public double? Temperature { get; init; }
            [JsonPropertyName("max_tokens")] public int? MaxTokens { get; init; }
        }

        private sealed record ChatMessage(
            [property: JsonPropertyName("role")] string Role,
            [property: JsonPropertyName("content")] string Content);

        private sealed record ResponseFormat
        {
            [JsonPropertyName("type")] public string Type { get; init; } = "json_object";
        }

        private sealed record ChatCompletionsResponse
        {
            [JsonPropertyName("choices")] public List<Choice>? Choices { get; init; }
        }

        private sealed record Choice
        {
            [JsonPropertyName("message")] public MessageContent? Message { get; init; }
        }

        private sealed record MessageContent
        {
            [JsonPropertyName("content")] public string? Content { get; init; }
        }

        private sealed record Classification
        {
            [JsonPropertyName("label")] public string? Label { get; init; }
            [JsonPropertyName("confidence")] public double? Confidence { get; init; }
            [JsonPropertyName("reasons")] public string? Reasons { get; init; }
        }

        // Мини-санитизация перед отправкой в модель
        private static string Sanitize(string input)
        {
            return TextSanitizer.MaskSensitive(input, maxLen: 4000);
        }
    }
}
