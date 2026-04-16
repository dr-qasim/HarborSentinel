using HarborSentinel.AdminHost.Options;
using HarborSentinel.AdminHost.Services;
using HarborSentinel.AdminContracts.Incidents;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var diagnosticBuffer = new RecentDiagnosticEventBuffer();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddProvider(new RecentDiagnosticEventLoggerProvider(diagnosticBuffer));

builder.Services.Configure<AdminHostOptions>(builder.Configuration.GetSection("AdminHost"));
builder.Services.Configure<HarborRuntimeOptions>(builder.Configuration.GetSection("HarborRuntime"));
builder.Services.AddSingleton<IRecentDiagnosticEventBuffer>(diagnosticBuffer);
builder.Services.AddSingleton<IRuntimeHealthService, SqliteRuntimeHealthService>();
builder.Services.AddSingleton<INodeDiagnosticsService, NodeDiagnosticsService>();
builder.Services.AddSingleton<IChatQueryService, SqliteChatQueryService>();
builder.Services.AddSingleton<SqliteIncidentQueryService>();
builder.Services.AddSingleton<IIncidentQueryService>(static sp => sp.GetRequiredService<SqliteIncidentQueryService>());
builder.Services.AddSingleton<IIncidentInboxService, SqliteIncidentInboxService>();
builder.Services.AddSingleton<IUserQueryService, SqliteUserQueryService>();
builder.Services.AddSingleton<IExportJobService, FileBackedExportJobService>();
builder.Services.AddProblemDetails();

var hostOptions = builder.Configuration.GetSection("AdminHost").Get<AdminHostOptions>() ?? new AdminHostOptions();
ValidateStartupConfiguration(builder.Environment, hostOptions);
builder.WebHost.UseUrls(hostOptions.ListenUrl);

var app = builder.Build();

app.UseExceptionHandler();

app.Use(async (context, next) =>
{
    var options = context.RequestServices.GetRequiredService<IOptions<AdminHostOptions>>().Value;
    if (string.IsNullOrWhiteSpace(options.ApiToken))
    {
        await next().ConfigureAwait(false);
        return;
    }

    var providedToken = context.Request.Headers["X-Admin-Token"].ToString();
    if (TokensMatch(providedToken, options.ApiToken))
    {
        await next().ConfigureAwait(false);
        return;
    }

    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    await context.Response.WriteAsJsonAsync(new
    {
        error = "unauthorized",
        detail = "Missing or invalid X-Admin-Token header."
    }).ConfigureAwait(false);
});

app.MapGet("/", (IOptions<AdminHostOptions> options) => Results.Ok(new
{
    service = "HarborSentinel.AdminHost",
    listen_url = options.Value.ListenUrl,
    api = "/api/v1/health"
}));

app.MapGet("/api/v1/health", async (IRuntimeHealthService healthService, CancellationToken ct) =>
{
    var snapshot = await healthService.GetSnapshotAsync(ct).ConfigureAwait(false);
    return Results.Ok(snapshot);
});

app.MapGet("/api/v1/node-status", async (INodeDiagnosticsService nodeDiagnosticsService, CancellationToken ct) =>
{
    var snapshot = await nodeDiagnosticsService.GetNodeStatusAsync(ct).ConfigureAwait(false);
    return Results.Ok(snapshot);
});

app.MapGet(
    "/api/v1/chats",
    async (
        IChatQueryService chatQueryService,
        int? page,
        int? pageSize,
        HarborSentinel.AdminContracts.Chats.AdminChatActivityFilterDto? activity,
        HarborSentinel.AdminContracts.Chats.AdminChatTrackingFilterDto? tracking,
        CancellationToken ct) =>
    {
        var snapshot = await chatQueryService.GetChatListAsync(
            page ?? 1,
            pageSize ?? 100,
            activity ?? HarborSentinel.AdminContracts.Chats.AdminChatActivityFilterDto.All,
            tracking ?? HarborSentinel.AdminContracts.Chats.AdminChatTrackingFilterDto.All,
            ct).ConfigureAwait(false);
        return Results.Ok(snapshot);
    });

app.MapGet(
    "/api/v1/chats/{chatId:long}",
    async (
        long chatId,
        IChatQueryService chatQueryService,
        CancellationToken ct) =>
    {
        var detail = await chatQueryService.GetChatDetailAsync(chatId, ct).ConfigureAwait(false);
        if (detail is null)
        {
            return Results.NotFound(new
            {
                error = "chat_not_found",
                detail = "Chat is not visible in the current AdminHost scope."
            });
        }

        return Results.Ok(detail);
    });

app.MapGet(
    "/api/v1/incidents",
    async (
        HttpContext httpContext,
        IIncidentInboxService incidentInboxService,
        int? page,
        int? pageSize,
        long? chatId,
        int? recentDays,
        CancellationToken ct) =>
    {
        var response = await incidentInboxService.GetIncidentFeedAsync(
            GetOperatorId(httpContext),
            page ?? 1,
            pageSize ?? 100,
            chatId,
            recentDays ?? 7,
            ct).ConfigureAwait(false);

        return Results.Ok(response);
    });

app.MapGet(
    "/api/v1/incidents/startup-sync",
    async (
        HttpContext httpContext,
        IIncidentInboxService incidentInboxService,
        int? recentDays,
        int? maxItems,
        CancellationToken ct) =>
    {
        var response = await incidentInboxService.GetStartupSyncAsync(
            GetOperatorId(httpContext),
            recentDays ?? 90,
            maxItems ?? 10,
            ct).ConfigureAwait(false);
        return Results.Ok(response);
    });

app.MapPost(
    "/api/v1/incidents/mark-seen",
    async (
        AdminIncidentMarkSeenRequestDto request,
        IIncidentInboxService incidentInboxService,
        CancellationToken ct) =>
    {
        var response = await incidentInboxService.MarkSeenAsync(request, ct).ConfigureAwait(false);
        return Results.Ok(response);
    });

app.MapPost(
    "/api/v1/incidents/{incidentId}/acknowledge",
    async (
        string incidentId,
        AdminIncidentAcknowledgeRequestDto request,
        IIncidentInboxService incidentInboxService,
        CancellationToken ct) =>
    {
        var response = await incidentInboxService.AcknowledgeAsync(incidentId, request, ct).ConfigureAwait(false);
        return Results.Ok(response);
    });

app.MapGet(
    "/api/v1/users",
    async (
        IUserQueryService userQueryService,
        int? page,
        int? pageSize,
        HarborSentinel.AdminContracts.Users.AdminUserListSourceFilterDto? source,
        string? search,
        CancellationToken ct) =>
    {
        var response = await userQueryService.GetUsersAsync(
            page ?? 1,
            pageSize ?? 100,
            source ?? HarborSentinel.AdminContracts.Users.AdminUserListSourceFilterDto.All,
            search,
            ct).ConfigureAwait(false);

        return Results.Ok(response);
    });

app.MapGet(
    "/api/v1/users/{userId:long}",
    async (
        long userId,
        IUserQueryService userQueryService,
        CancellationToken ct) =>
    {
        var detail = await userQueryService.GetUserDetailAsync(userId, ct).ConfigureAwait(false);
        if (detail is null)
        {
            return Results.NotFound(new
            {
                error = "user_not_found",
                detail = "User is not visible in the current AdminHost scope."
            });
        }

        return Results.Ok(detail);
    });

app.MapGet(
    "/api/v1/exports/catalog",
    async (
        IExportJobService exportJobService,
        CancellationToken ct) =>
    {
        var response = await exportJobService.GetCatalogAsync(ct).ConfigureAwait(false);
        return Results.Ok(response);
    });

app.MapGet(
    "/api/v1/exports/jobs",
    async (
        IExportJobService exportJobService,
        CancellationToken ct) =>
    {
        var response = await exportJobService.GetJobsAsync(ct).ConfigureAwait(false);
        return Results.Ok(response);
    });

app.MapGet(
    "/api/v1/exports/jobs/{jobId}",
    async (
        string jobId,
        IExportJobService exportJobService,
        CancellationToken ct) =>
    {
        var response = await exportJobService.GetJobAsync(jobId, ct).ConfigureAwait(false);
        return response is null
            ? Results.NotFound(new { error = "export_job_not_found", detail = "Export job was not found." })
            : Results.Ok(response);
    });

app.MapPost(
    "/api/v1/exports/jobs",
    async (
        HarborSentinel.AdminContracts.Exports.AdminExportJobCreateRequestDto request,
        IExportJobService exportJobService,
        CancellationToken ct) =>
    {
        try
        {
            var response = await exportJobService.CreateJobAsync(request, ct).ConfigureAwait(false);
            return Results.Ok(response);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = "invalid_export_request", detail = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = "export_unavailable", detail = ex.Message });
        }
    });

app.MapGet(
    "/api/v1/exports/jobs/{jobId}/download",
    async (
        string jobId,
        IExportJobService exportJobService,
        CancellationToken ct) =>
    {
        var download = await exportJobService.OpenDownloadAsync(jobId, ct).ConfigureAwait(false);
        return download is null
            ? Results.NotFound(new { error = "export_file_not_found", detail = "Export file is not available for download." })
            : Results.File(download.FilePath, download.ContentType, download.DownloadFileName);
    });

app.Run();

static bool TokensMatch(string providedToken, string expectedToken)
{
    if (string.IsNullOrEmpty(providedToken) || string.IsNullOrEmpty(expectedToken))
        return false;

    var providedBytes = Encoding.UTF8.GetBytes(providedToken);
    var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
    return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
}

static void ValidateStartupConfiguration(IHostEnvironment environment, AdminHostOptions options)
{
    if (!environment.IsProduction())
        return;

    if (string.IsNullOrWhiteSpace(options.ApiToken))
        throw new InvalidOperationException("AdminHost ApiToken must be configured in Production.");

    if (!Uri.TryCreate(options.ListenUrl, UriKind.Absolute, out var listenUri))
        throw new InvalidOperationException("AdminHost ListenUrl is invalid.");

    var host = listenUri.Host;
    var isLocalhost =
        string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);

    if (!isLocalhost)
        throw new InvalidOperationException("AdminHost must stay localhost-only in Production.");
}

static string? GetOperatorId(HttpContext context)
{
    var value = context.Request.Headers["X-Admin-Operator"].ToString();
    return string.IsNullOrWhiteSpace(value) ? null : value;
}
