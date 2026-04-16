using HarborSentinel.AdminContracts.Chats;
using HarborSentinel.AdminContracts.Diagnostics;
using HarborSentinel.AdminContracts.Dashboard;
using HarborSentinel.AdminContracts.Exports;
using HarborSentinel.AdminContracts.Incidents;
using HarborSentinel.AdminContracts.Users;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace HarborSentinel.AdminConsole.Services;

public sealed class AdminHostClient : IAdminHostClient
{
    private static readonly JsonSerializerOptions ErrorJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;

    public AdminHostClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<AdminDashboardSnapshotDto> GetDashboardSnapshotAsync(
        string baseUrl,
        string? apiToken,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var requestUri = new Uri(new Uri(EnsureTrailingSlash(baseUrl), UriKind.Absolute), "api/v1/health");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        if (!string.IsNullOrWhiteSpace(apiToken))
            request.Headers.TryAddWithoutValidation("X-Admin-Token", apiToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        await EnsureSuccessWithDetailsAsync(response, timeoutCts.Token).ConfigureAwait(false);

        var payload = await response.Content
            .ReadFromJsonAsync<AdminDashboardSnapshotDto>(cancellationToken: timeoutCts.Token)
            .ConfigureAwait(false);

        return payload ?? throw new InvalidOperationException("AdminHost вернул пустой ответ для обзора.");
    }

    public async Task<AdminNodeStatusDto> GetNodeStatusAsync(
        string baseUrl,
        string? apiToken,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var requestUri = new Uri(new Uri(EnsureTrailingSlash(baseUrl), UriKind.Absolute), "api/v1/node-status");
        using var request = BuildRequest(HttpMethod.Get, requestUri, apiToken);
        using var timeoutCts = CreateTimeout(ct, timeout);
        using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        await EnsureSuccessWithDetailsAsync(response, timeoutCts.Token).ConfigureAwait(false);

        var payload = await response.Content
            .ReadFromJsonAsync<AdminNodeStatusDto>(cancellationToken: timeoutCts.Token)
            .ConfigureAwait(false);

        return payload ?? throw new InvalidOperationException("AdminHost вернул пустой ответ для состояния узла.");
    }

    public async Task<AdminChatListResponseDto> GetChatsAsync(
        string baseUrl,
        string? apiToken,
        TimeSpan timeout,
        int page,
        int pageSize,
        AdminChatActivityFilterDto activityFilter,
        AdminChatTrackingFilterDto trackingFilter,
        CancellationToken ct)
    {
        var requestUri = new Uri(
            new Uri(EnsureTrailingSlash(baseUrl), UriKind.Absolute),
            $"api/v1/chats?page={page}&pageSize={pageSize}&activity={activityFilter}&tracking={trackingFilter}");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        if (!string.IsNullOrWhiteSpace(apiToken))
            request.Headers.TryAddWithoutValidation("X-Admin-Token", apiToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        await EnsureSuccessWithDetailsAsync(response, timeoutCts.Token).ConfigureAwait(false);

        var payload = await response.Content
            .ReadFromJsonAsync<AdminChatListResponseDto>(cancellationToken: timeoutCts.Token)
            .ConfigureAwait(false);

        return payload ?? throw new InvalidOperationException("AdminHost вернул пустой ответ для списка чатов.");
    }

    public async Task<AdminChatDetailDto> GetChatDetailAsync(
        string baseUrl,
        string? apiToken,
        TimeSpan timeout,
        long chatId,
        CancellationToken ct)
    {
        var requestUri = new Uri(
            new Uri(EnsureTrailingSlash(baseUrl), UriKind.Absolute),
            $"api/v1/chats/{chatId}");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        if (!string.IsNullOrWhiteSpace(apiToken))
            request.Headers.TryAddWithoutValidation("X-Admin-Token", apiToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        await EnsureSuccessWithDetailsAsync(response, timeoutCts.Token).ConfigureAwait(false);

        var payload = await response.Content
            .ReadFromJsonAsync<AdminChatDetailDto>(cancellationToken: timeoutCts.Token)
            .ConfigureAwait(false);

        return payload ?? throw new InvalidOperationException("AdminHost вернул пустой ответ для карточки чата.");
    }

    public async Task<AdminIncidentFeedResponseDto> GetIncidentsAsync(
        string baseUrl,
        string? apiToken,
        string? operatorId,
        TimeSpan timeout,
        int page,
        int pageSize,
        long? chatId,
        int recentWindowDays,
        CancellationToken ct)
    {
        var chatPart = chatId.HasValue ? $"&chatId={chatId.Value}" : string.Empty;
        var requestUri = new Uri(
            new Uri(EnsureTrailingSlash(baseUrl), UriKind.Absolute),
            $"api/v1/incidents?page={page}&pageSize={pageSize}&recentDays={recentWindowDays}{chatPart}");

        using var request = BuildRequest(HttpMethod.Get, requestUri, apiToken, operatorId);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        await EnsureSuccessWithDetailsAsync(response, timeoutCts.Token).ConfigureAwait(false);

        var payload = await response.Content
            .ReadFromJsonAsync<AdminIncidentFeedResponseDto>(cancellationToken: timeoutCts.Token)
            .ConfigureAwait(false);

        return payload ?? throw new InvalidOperationException("AdminHost вернул пустой ответ для ленты инцидентов.");
    }

    public async Task<AdminIncidentStartupSyncResponseDto> GetIncidentStartupSyncAsync(
        string baseUrl,
        string? apiToken,
        string? operatorId,
        TimeSpan timeout,
        int recentWindowDays,
        int maxItems,
        CancellationToken ct)
    {
        var requestUri = new Uri(
            new Uri(EnsureTrailingSlash(baseUrl), UriKind.Absolute),
            $"api/v1/incidents/startup-sync?recentDays={recentWindowDays}&maxItems={maxItems}");

        using var request = BuildRequest(HttpMethod.Get, requestUri, apiToken, operatorId);
        using var timeoutCts = CreateTimeout(ct, timeout);
        using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        await EnsureSuccessWithDetailsAsync(response, timeoutCts.Token).ConfigureAwait(false);

        var payload = await response.Content
            .ReadFromJsonAsync<AdminIncidentStartupSyncResponseDto>(cancellationToken: timeoutCts.Token)
            .ConfigureAwait(false);

        return payload ?? throw new InvalidOperationException("AdminHost вернул пустой ответ для startup sync инцидентов.");
    }

    public async Task<AdminIncidentMarkSeenResponseDto> MarkIncidentsSeenAsync(
        string baseUrl,
        string? apiToken,
        string? operatorId,
        TimeSpan timeout,
        AdminIncidentMarkSeenRequestDto requestBody,
        CancellationToken ct)
    {
        var requestUri = new Uri(new Uri(EnsureTrailingSlash(baseUrl), UriKind.Absolute), "api/v1/incidents/mark-seen");
        using var request = BuildRequest(HttpMethod.Post, requestUri, apiToken, operatorId);
        request.Content = JsonContent.Create(requestBody);
        using var timeoutCts = CreateTimeout(ct, timeout);
        using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        await EnsureSuccessWithDetailsAsync(response, timeoutCts.Token).ConfigureAwait(false);

        var payload = await response.Content
            .ReadFromJsonAsync<AdminIncidentMarkSeenResponseDto>(cancellationToken: timeoutCts.Token)
            .ConfigureAwait(false);

        return payload ?? throw new InvalidOperationException("AdminHost вернул пустой ответ для отметки инцидентов как просмотренных.");
    }

    public async Task<AdminIncidentAcknowledgeResponseDto> AcknowledgeIncidentAsync(
        string baseUrl,
        string? apiToken,
        string? operatorId,
        TimeSpan timeout,
        string incidentId,
        AdminIncidentAcknowledgeRequestDto requestBody,
        CancellationToken ct)
    {
        var requestUri = new Uri(new Uri(EnsureTrailingSlash(baseUrl), UriKind.Absolute), $"api/v1/incidents/{Uri.EscapeDataString(incidentId)}/acknowledge");
        using var request = BuildRequest(HttpMethod.Post, requestUri, apiToken, operatorId);
        request.Content = JsonContent.Create(requestBody);
        using var timeoutCts = CreateTimeout(ct, timeout);
        using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        await EnsureSuccessWithDetailsAsync(response, timeoutCts.Token).ConfigureAwait(false);

        var payload = await response.Content
            .ReadFromJsonAsync<AdminIncidentAcknowledgeResponseDto>(cancellationToken: timeoutCts.Token)
            .ConfigureAwait(false);

        return payload ?? throw new InvalidOperationException("AdminHost вернул пустой ответ для подтверждения инцидента.");
    }

    public async Task<AdminUserListResponseDto> GetUsersAsync(
        string baseUrl,
        string? apiToken,
        TimeSpan timeout,
        int page,
        int pageSize,
        AdminUserListSourceFilterDto sourceFilter,
        string? searchQuery,
        CancellationToken ct)
    {
        var searchPart = string.IsNullOrWhiteSpace(searchQuery)
            ? string.Empty
            : $"&search={Uri.EscapeDataString(searchQuery.Trim())}";
        var requestUri = new Uri(
            new Uri(EnsureTrailingSlash(baseUrl), UriKind.Absolute),
            $"api/v1/users?page={page}&pageSize={pageSize}&source={sourceFilter}{searchPart}");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        if (!string.IsNullOrWhiteSpace(apiToken))
            request.Headers.TryAddWithoutValidation("X-Admin-Token", apiToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        await EnsureSuccessWithDetailsAsync(response, timeoutCts.Token).ConfigureAwait(false);

        var payload = await response.Content
            .ReadFromJsonAsync<AdminUserListResponseDto>(cancellationToken: timeoutCts.Token)
            .ConfigureAwait(false);

        return payload ?? throw new InvalidOperationException("AdminHost вернул пустой ответ для списка пользователей.");
    }

    public async Task<AdminUserDetailDto> GetUserDetailAsync(
        string baseUrl,
        string? apiToken,
        TimeSpan timeout,
        long userId,
        CancellationToken ct)
    {
        var requestUri = new Uri(
            new Uri(EnsureTrailingSlash(baseUrl), UriKind.Absolute),
            $"api/v1/users/{userId}");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        if (!string.IsNullOrWhiteSpace(apiToken))
            request.Headers.TryAddWithoutValidation("X-Admin-Token", apiToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        await EnsureSuccessWithDetailsAsync(response, timeoutCts.Token).ConfigureAwait(false);

        var payload = await response.Content
            .ReadFromJsonAsync<AdminUserDetailDto>(cancellationToken: timeoutCts.Token)
            .ConfigureAwait(false);

        return payload ?? throw new InvalidOperationException("AdminHost вернул пустой ответ для карточки пользователя.");
    }

    public async Task<AdminExportCatalogResponseDto> GetExportCatalogAsync(
        string baseUrl,
        string? apiToken,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var requestUri = new Uri(new Uri(EnsureTrailingSlash(baseUrl), UriKind.Absolute), "api/v1/exports/catalog");
        using var request = BuildRequest(HttpMethod.Get, requestUri, apiToken);
        using var timeoutCts = CreateTimeout(ct, timeout);
        using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        await EnsureSuccessWithDetailsAsync(response, timeoutCts.Token).ConfigureAwait(false);

        var payload = await response.Content
            .ReadFromJsonAsync<AdminExportCatalogResponseDto>(cancellationToken: timeoutCts.Token)
            .ConfigureAwait(false);

        return payload ?? throw new InvalidOperationException("AdminHost вернул пустой ответ для каталога выгрузок.");
    }

    public async Task<AdminExportJobsResponseDto> GetExportJobsAsync(
        string baseUrl,
        string? apiToken,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var requestUri = new Uri(new Uri(EnsureTrailingSlash(baseUrl), UriKind.Absolute), "api/v1/exports/jobs");
        using var request = BuildRequest(HttpMethod.Get, requestUri, apiToken);
        using var timeoutCts = CreateTimeout(ct, timeout);
        using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        await EnsureSuccessWithDetailsAsync(response, timeoutCts.Token).ConfigureAwait(false);

        var payload = await response.Content
            .ReadFromJsonAsync<AdminExportJobsResponseDto>(cancellationToken: timeoutCts.Token)
            .ConfigureAwait(false);

        return payload ?? throw new InvalidOperationException("AdminHost вернул пустой ответ для списка выгрузок.");
    }

    public async Task<AdminExportJobDetailDto> CreateExportJobAsync(
        string baseUrl,
        string? apiToken,
        TimeSpan timeout,
        AdminExportJobCreateRequestDto requestBody,
        CancellationToken ct)
    {
        var requestUri = new Uri(new Uri(EnsureTrailingSlash(baseUrl), UriKind.Absolute), "api/v1/exports/jobs");
        using var request = BuildRequest(HttpMethod.Post, requestUri, apiToken);
        request.Content = JsonContent.Create(requestBody);
        using var timeoutCts = CreateTimeout(ct, timeout);
        using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        await EnsureSuccessWithDetailsAsync(response, timeoutCts.Token).ConfigureAwait(false);

        var payload = await response.Content
            .ReadFromJsonAsync<AdminExportJobDetailDto>(cancellationToken: timeoutCts.Token)
            .ConfigureAwait(false);

        return payload ?? throw new InvalidOperationException("AdminHost вернул пустой ответ для созданной выгрузки.");
    }

    public async Task<AdminExportJobDetailDto> GetExportJobAsync(
        string baseUrl,
        string? apiToken,
        TimeSpan timeout,
        string jobId,
        CancellationToken ct)
    {
        var requestUri = new Uri(new Uri(EnsureTrailingSlash(baseUrl), UriKind.Absolute), $"api/v1/exports/jobs/{jobId}");
        using var request = BuildRequest(HttpMethod.Get, requestUri, apiToken);
        using var timeoutCts = CreateTimeout(ct, timeout);
        using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        await EnsureSuccessWithDetailsAsync(response, timeoutCts.Token).ConfigureAwait(false);

        var payload = await response.Content
            .ReadFromJsonAsync<AdminExportJobDetailDto>(cancellationToken: timeoutCts.Token)
            .ConfigureAwait(false);

        return payload ?? throw new InvalidOperationException("AdminHost вернул пустой ответ для карточки выгрузки.");
    }

    public async Task<DownloadedAdminExportFile> DownloadExportJobFileAsync(
        string baseUrl,
        string? apiToken,
        TimeSpan timeout,
        string jobId,
        CancellationToken ct)
    {
        var requestUri = new Uri(new Uri(EnsureTrailingSlash(baseUrl), UriKind.Absolute), $"api/v1/exports/jobs/{jobId}/download");
        using var request = BuildRequest(HttpMethod.Get, requestUri, apiToken);
        using var timeoutCts = CreateTimeout(ct, timeout);
        using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        await EnsureSuccessWithDetailsAsync(response, timeoutCts.Token).ConfigureAwait(false);

        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? $"export_{jobId}.bin";
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var content = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);

        return new DownloadedAdminExportFile
        {
            FileName = fileName,
            ContentType = contentType,
            Content = content
        };
    }

    private static string EnsureTrailingSlash(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("Адрес AdminHost не задан.");

        return baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, Uri uri, string? apiToken, string? operatorId = null)
    {
        var request = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrWhiteSpace(apiToken))
            request.Headers.TryAddWithoutValidation("X-Admin-Token", apiToken);
        if (!string.IsNullOrWhiteSpace(operatorId))
            request.Headers.TryAddWithoutValidation("X-Admin-Operator", operatorId);

        return request;
    }

    private static CancellationTokenSource CreateTimeout(CancellationToken ct, TimeSpan timeout)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        return timeoutCts;
    }

    private static async Task EnsureSuccessWithDetailsAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        string? detail = null;

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<AdminHostErrorPayload>(stream, ErrorJsonOptions, ct).ConfigureAwait(false);
            detail = string.IsNullOrWhiteSpace(payload?.Detail) ? null : payload.Detail;
        }
        catch
        {
            // Ignore payload parsing issues and fall back to HTTP status text.
        }

        detail ??= !string.IsNullOrWhiteSpace(response.ReasonPhrase)
            ? response.ReasonPhrase
            : $"HTTP {(int)response.StatusCode}";

        throw new HttpRequestException(detail, null, response.StatusCode);
    }

    private sealed class AdminHostErrorPayload
    {
        public string? Detail { get; init; }
    }
}
