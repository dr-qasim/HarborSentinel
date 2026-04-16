using HarborSentinel.AdminContracts.Exports;
using HarborSentinel.AdminHost.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace HarborSentinel.AdminHost.Services;

public sealed class FileBackedExportJobService : IExportJobService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly HarborRuntimeOptions _runtimeOptions;
    private readonly ILogger<FileBackedExportJobService> _log;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly string _databasePath;
    private readonly string _jobsRootPath;
    private readonly ConcurrentDictionary<string, JobManifest> _jobs = new(StringComparer.Ordinal);

    public FileBackedExportJobService(
        IOptions<HarborRuntimeOptions> runtimeOptions,
        ILogger<FileBackedExportJobService> log,
        IHostApplicationLifetime lifetime)
    {
        _runtimeOptions = runtimeOptions.Value;
        _log = log;
        _lifetime = lifetime;

        _databasePath = Path.GetFullPath(_runtimeOptions.DatabasePath);
        var storageBasePath = Path.GetFullPath(_runtimeOptions.StorageBasePath);
        var adminExportsDir = string.IsNullOrWhiteSpace(_runtimeOptions.AdminExportsDir)
            ? "admin_exports"
            : _runtimeOptions.AdminExportsDir;
        _jobsRootPath = Path.Combine(storageBasePath, adminExportsDir, "adminhost_jobs");

        Directory.CreateDirectory(_jobsRootPath);
        LoadExistingJobs();
    }

    public async Task<AdminExportCatalogResponseDto> GetCatalogAsync(CancellationToken ct)
    {
        var warnings = new List<string>();
        var runtime = await ReadRuntimeCapabilitiesAsync(warnings, ct).ConfigureAwait(false);

        return new AdminExportCatalogResponseDto
        {
            Items =
            [
                new AdminExportCatalogItemDto
                {
                    Kind = AdminExportKindDto.TrackedChatMessages,
                    Label = "Выгрузка отслеживаемого чата",
                    Description = "Server-side выгрузка сообщений выбранного чата в сжатый JSONL.",
                    RequiresChatId = true,
                    IsAvailable = runtime.MessagesExists,
                    AvailabilityNote = runtime.MessagesExists
                        ? runtime.ChatTrackingValidationAvailable
                            ? "Требуется Chat ID. Будет проверен текущий scope и tracked-статус."
                            : "Требуется Chat ID. Legacy schema: проверка tracked-статуса ограничена."
                        : "Таблица messages отсутствует."
                },
                new AdminExportCatalogItemDto
                {
                    Kind = AdminExportKindDto.StartedUsers,
                    Label = "Выгрузка private-start пользователей",
                    Description = "Server-side выгрузка реестра private-start с безопасным представлением телефона.",
                    RequiresChatId = false,
                    IsAvailable = runtime.PrivateStartUsersExists,
                    AvailabilityNote = runtime.PrivateStartUsersExists
                        ? "Формируется из таблицы private_start_users."
                        : "Таблица private_start_users отсутствует."
                }
            ],
            Warnings = warnings
        };
    }

    public Task<AdminExportJobsResponseDto> GetJobsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var items = _jobs.Values
            .OrderByDescending(static item => item.RequestedAtUtc)
            .Select(ToListItemDto)
            .ToArray();

        return Task.FromResult(new AdminExportJobsResponseDto
        {
            TotalCount = items.Length,
            Items = items
        });
    }

    public Task<AdminExportJobDetailDto?> GetJobAsync(string jobId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_jobs.TryGetValue(jobId, out var job))
            return Task.FromResult<AdminExportJobDetailDto?>(null);

        return Task.FromResult<AdminExportJobDetailDto?>(ToDetailDto(job));
    }

    public async Task<AdminExportJobDetailDto> CreateJobAsync(AdminExportJobCreateRequestDto request, CancellationToken ct)
    {
        var runtimeWarnings = new List<string>();
        var runtime = await ReadRuntimeCapabilitiesAsync(runtimeWarnings, ct).ConfigureAwait(false);

        ValidateRequest(request, runtime);

        var jobId = $"job_{DateTimeOffset.UtcNow.UtcDateTime:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
        var manifest = new JobManifest
        {
            JobId = jobId,
            Kind = request.Kind,
            ChatId = request.ChatId,
            Label = GetJobLabel(request.Kind),
            ScopeSummary = BuildScopeSummary(request),
            Status = AdminExportJobStatusDto.Pending,
            RequestedAtUtc = DateTimeOffset.UtcNow,
            Warnings = []
        };

        _jobs[jobId] = manifest;
        await PersistManifestAsync(manifest, ct).ConfigureAwait(false);

        _ = Task.Run(() => ExecuteJobAsync(jobId, _lifetime.ApplicationStopping));
        return ToDetailDto(manifest);
    }

    public Task<ExportJobDownloadResult?> OpenDownloadAsync(string jobId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_jobs.TryGetValue(jobId, out var job) ||
            job.Status != AdminExportJobStatusDto.Completed ||
            job.File is null)
        {
            return Task.FromResult<ExportJobDownloadResult?>(null);
        }

        var jobDir = GetJobDirectory(job.JobId);
        var fullPath = Path.GetFullPath(Path.Combine(jobDir, job.File.RelativePath));
        if (!fullPath.StartsWith(jobDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            return Task.FromResult<ExportJobDownloadResult?>(null);

        return Task.FromResult<ExportJobDownloadResult?>(new ExportJobDownloadResult
        {
            FilePath = fullPath,
            ContentType = job.File.ContentType,
            DownloadFileName = job.File.FileName
        });
    }

    private async Task ExecuteJobAsync(string jobId, CancellationToken ct)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            return;

        try
        {
            job.Status = AdminExportJobStatusDto.Running;
            job.StartedAtUtc = DateTimeOffset.UtcNow;
            await PersistManifestAsync(job, ct).ConfigureAwait(false);

            var result = job.Kind switch
            {
                AdminExportKindDto.TrackedChatMessages => await ExecuteTrackedChatExportAsync(job, ct).ConfigureAwait(false),
                AdminExportKindDto.StartedUsers => await ExecuteStartedUsersExportAsync(job, ct).ConfigureAwait(false),
                _ => throw new InvalidOperationException("Неподдерживаемый тип выгрузки.")
            };

            job.Status = AdminExportJobStatusDto.Completed;
            job.CompletedAtUtc = DateTimeOffset.UtcNow;
            job.ResultSummary = result.ResultSummary;
            job.File = result.File;
            job.Warnings = job.Warnings.Concat(result.Warnings).Distinct(StringComparer.Ordinal).ToList();
            await PersistManifestAsync(job, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Status = AdminExportJobStatusDto.Failed;
            job.CompletedAtUtc = DateTimeOffset.UtcNow;
            job.ErrorMessage = "Выполнение выгрузки было остановлено вместе с AdminHost.";
            await PersistManifestAsync(job, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Export job {JobId} failed", jobId);
            job.Status = AdminExportJobStatusDto.Failed;
            job.CompletedAtUtc = DateTimeOffset.UtcNow;
            job.ErrorMessage = ex.Message;
            await PersistManifestAsync(job, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<JobExecutionResult> ExecuteTrackedChatExportAsync(JobManifest job, CancellationToken ct)
    {
        if (!job.ChatId.HasValue)
            throw new ArgumentException("Для выгрузки чата требуется Chat ID.");

        var warnings = new List<string>();
        await using var connection = OpenReadOnly();
        var runtime = await ReadRuntimeCapabilitiesAsync(warnings, ct).ConfigureAwait(false);
        await ValidateTrackedChatAsync(connection, runtime, job.ChatId.Value, warnings, ct).ConfigureAwait(false);

        var jobDir = EnsureJobDirectory(job.JobId);
        var fileName = $"tracked_chat_{job.ChatId.Value}_{DateTimeOffset.UtcNow.UtcDateTime:yyyyMMdd_HHmmss}.jsonl.gz";
        var relativePath = fileName;
        var fullPath = Path.Combine(jobDir, fileName);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, timestamp_utc, chat_id, chat_title, chat_username, user_id,
       user_username, user_first_name, user_last_name, message_id, text
FROM messages
WHERE chat_id = $chat
ORDER BY timestamp_utc ASC, id ASC;";
        cmd.Parameters.AddWithValue("$chat", job.ChatId.Value);

        long messageCount = 0;
        var participants = new HashSet<long>();
        await using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var gz = new GZipStream(fs, CompressionLevel.SmallestSize);
        await using var writer = new StreamWriter(gz, new UTF8Encoding(false));

        var header = new
        {
            kind = "meta",
            export_kind = "tracked_chat_messages",
            chat_id = job.ChatId.Value,
            exported_at_utc = DateTimeOffset.UtcNow.UtcDateTime.ToString("O"),
            format = "jsonl",
            version = 1
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(header, JsonOptions)).ConfigureAwait(false);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            messageCount++;
            if (!reader.IsDBNull(5))
                participants.Add(reader.GetInt64(5));

            var line = new
            {
                kind = "message",
                store_id = reader.GetInt64(0),
                timestamp_utc = reader.GetString(1),
                chat_id = reader.GetInt64(2),
                chat_title = reader.IsDBNull(3) ? null : reader.GetString(3),
                chat_username = reader.IsDBNull(4) ? null : reader.GetString(4),
                user_id = reader.IsDBNull(5) ? (long?)null : reader.GetInt64(5),
                user_username = reader.IsDBNull(6) ? null : reader.GetString(6),
                user_first_name = reader.IsDBNull(7) ? null : reader.GetString(7),
                user_last_name = reader.IsDBNull(8) ? null : reader.GetString(8),
                message_id = reader.IsDBNull(9) ? (int?)null : reader.GetInt32(9),
                text = reader.IsDBNull(10) ? null : reader.GetString(10)
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(line, JsonOptions)).ConfigureAwait(false);
        }

        var summary = new
        {
            kind = "summary",
            chat_id = job.ChatId.Value,
            message_count = messageCount,
            participant_count = participants.Count,
            completed_at_utc = DateTimeOffset.UtcNow.UtcDateTime.ToString("O")
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(summary, JsonOptions)).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);

        var fileInfo = new FileInfo(fullPath);
        return new JobExecutionResult
        {
            ResultSummary = $"Чат {job.ChatId.Value}: сообщений {messageCount:N0}, участников {participants.Count:N0}.",
            Warnings = warnings,
            File = new PersistedFileInfo
            {
                RelativePath = relativePath,
                FileName = fileName,
                ContentType = "application/gzip",
                SizeBytes = fileInfo.Length,
                CreatedAtUtc = DateTimeOffset.UtcNow
            }
        };
    }

    private async Task<JobExecutionResult> ExecuteStartedUsersExportAsync(JobManifest job, CancellationToken ct)
    {
        var warnings = new List<string>();
        await using var connection = OpenReadOnly();
        var runtime = await ReadRuntimeCapabilitiesAsync(warnings, ct).ConfigureAwait(false);
        if (!runtime.PrivateStartUsersExists)
            throw new InvalidOperationException("Таблица private_start_users отсутствует.");

        var jobDir = EnsureJobDirectory(job.JobId);
        var fileName = $"started_users_{DateTimeOffset.UtcNow.UtcDateTime:yyyyMMdd_HHmmss}.jsonl.gz";
        var relativePath = fileName;
        var fullPath = Path.Combine(jobDir, fileName);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT user_id, first_name, last_name, username, phone, started_at_utc, last_started_at_utc, start_count
FROM private_start_users
ORDER BY last_started_at_utc DESC, user_id DESC;";

        long count = 0;
        await using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var gz = new GZipStream(fs, CompressionLevel.SmallestSize);
        await using var writer = new StreamWriter(gz, new UTF8Encoding(false));

        var header = new
        {
            kind = "meta",
            export_kind = "started_users",
            exported_at_utc = DateTimeOffset.UtcNow.UtcDateTime.ToString("O"),
            format = "jsonl",
            version = 1
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(header, JsonOptions)).ConfigureAwait(false);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            count++;
            var phone = reader.IsDBNull(4) ? null : reader.GetString(4);
            var line = new
            {
                kind = "started_user",
                user_id = reader.GetInt64(0),
                first_name = reader.IsDBNull(1) ? null : reader.GetString(1),
                last_name = reader.IsDBNull(2) ? null : reader.GetString(2),
                username = reader.IsDBNull(3) ? null : reader.GetString(3),
                has_phone = !string.IsNullOrWhiteSpace(phone),
                phone_masked = MaskPhone(phone),
                started_at_utc = reader.GetString(5),
                last_started_at_utc = reader.GetString(6),
                start_count = reader.GetInt32(7)
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(line, JsonOptions)).ConfigureAwait(false);
        }

        var summary = new
        {
            kind = "summary",
            user_count = count,
            completed_at_utc = DateTimeOffset.UtcNow.UtcDateTime.ToString("O")
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(summary, JsonOptions)).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);

        var fileInfo = new FileInfo(fullPath);
        return new JobExecutionResult
        {
            ResultSummary = $"Private-start пользователей: {count:N0}.",
            Warnings = warnings,
            File = new PersistedFileInfo
            {
                RelativePath = relativePath,
                FileName = fileName,
                ContentType = "application/gzip",
                SizeBytes = fileInfo.Length,
                CreatedAtUtc = DateTimeOffset.UtcNow
            }
        };
    }

    private async Task<RuntimeCapabilities> ReadRuntimeCapabilitiesAsync(List<string> warnings, CancellationToken ct)
    {
        if (!File.Exists(_databasePath))
        {
            warnings.Add("Runtime-база данных не найдена. Экспортный контур недоступен.");
            return new RuntimeCapabilities();
        }

        try
        {
            await using var connection = OpenReadOnly();
            var messagesExists = await TableExistsAsync(connection, "messages", ct).ConfigureAwait(false);
            var privateStartUsersExists = await TableExistsAsync(connection, "private_start_users", ct).ConfigureAwait(false);
            var chatRegistryExists = await TableExistsAsync(connection, "chat_registry", ct).ConfigureAwait(false);
            var chatRegistryHasTracked = chatRegistryExists && await ColumnExistsAsync(connection, "chat_registry", "is_tracked", ct).ConfigureAwait(false);
            var chatRegistryHasListed = chatRegistryExists && await ColumnExistsAsync(connection, "chat_registry", "is_listed", ct).ConfigureAwait(false);

            if (!messagesExists)
                warnings.Add("Таблица messages отсутствует. Выгрузка чатов недоступна.");
            if (!privateStartUsersExists)
                warnings.Add("Таблица private_start_users отсутствует. Выгрузка private-start пользователей недоступна.");
            if (chatRegistryExists && !chatRegistryHasTracked)
                warnings.Add("Обнаружена старая схема chat_registry: проверка tracked-статуса ограничена.");

            return new RuntimeCapabilities
            {
                MessagesExists = messagesExists,
                PrivateStartUsersExists = privateStartUsersExists,
                ChatRegistryExists = chatRegistryExists,
                ChatRegistryHasTracked = chatRegistryHasTracked,
                ChatRegistryHasListed = chatRegistryHasListed
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to read export runtime capabilities");
            warnings.Add("Не удалось определить состояние runtime-слоя для выгрузок.");
            return new RuntimeCapabilities();
        }
    }

    private static void ValidateRequest(AdminExportJobCreateRequestDto request, RuntimeCapabilities runtime)
    {
        switch (request.Kind)
        {
            case AdminExportKindDto.TrackedChatMessages:
                if (!request.ChatId.HasValue)
                    throw new ArgumentException("Для выгрузки отслеживаемого чата требуется Chat ID.");
                if (!runtime.MessagesExists)
                    throw new InvalidOperationException("Выгрузка чата недоступна: таблица messages отсутствует.");
                break;

            case AdminExportKindDto.StartedUsers:
                if (!runtime.PrivateStartUsersExists)
                    throw new InvalidOperationException("Выгрузка private-start пользователей недоступна: таблица private_start_users отсутствует.");
                break;

            default:
                throw new ArgumentException("Указан неподдерживаемый тип выгрузки.");
        }
    }

    private async Task ValidateTrackedChatAsync(
        SqliteConnection connection,
        RuntimeCapabilities runtime,
        long chatId,
        List<string> warnings,
        CancellationToken ct)
    {
        if (!runtime.ChatRegistryExists)
        {
            warnings.Add("Таблица chat_registry отсутствует. Проверка tracked-статуса недоступна, используется экспорт по сообщениям.");
            return;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $@"
SELECT {(runtime.ChatRegistryHasTracked ? "is_tracked" : "NULL")} AS is_tracked,
       {(runtime.ChatRegistryHasListed ? "is_listed" : "NULL")} AS is_listed
FROM chat_registry
WHERE chat_id = $chat
LIMIT 1;";
        cmd.Parameters.AddWithValue("$chat", chatId);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (runtime.ChatRegistryHasListed)
                throw new InvalidOperationException("Чат не найден в текущей области AdminHost.");

            warnings.Add("Чат отсутствует в chat_registry. Проверка tracked-статуса пропущена.");
            return;
        }

        if (runtime.ChatRegistryHasListed && !reader.IsDBNull(1) && reader.GetInt64(1) != 1)
            throw new InvalidOperationException("Чат не виден текущему экземпляру бота.");

        if (runtime.ChatRegistryHasTracked)
        {
            if (reader.IsDBNull(0) || reader.GetInt64(0) != 1)
                throw new InvalidOperationException("Чат не отмечен как отслеживаемый.");
        }
        else
        {
            warnings.Add("Проверка tracked-статуса ограничена legacy schema. Экспорт выполнен по server-side данным.");
        }
    }

    private void LoadExistingJobs()
    {
        foreach (var dir in Directory.EnumerateDirectories(_jobsRootPath))
        {
            var manifestPath = Path.Combine(dir, "job.json");
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                var json = File.ReadAllText(manifestPath, Encoding.UTF8);
                var manifest = JsonSerializer.Deserialize<JobManifest>(json, JsonOptions);
                if (manifest is not null)
                {
                    if (manifest.Status is AdminExportJobStatusDto.Pending or AdminExportJobStatusDto.Running)
                    {
                        manifest.Status = AdminExportJobStatusDto.Failed;
                        manifest.CompletedAtUtc = DateTimeOffset.UtcNow;
                        manifest.ErrorMessage = "AdminHost был перезапущен до завершения задачи выгрузки.";
                        manifest.Warnings = manifest.Warnings
                            .Append("Предыдущее выполнение было прервано рестартом AdminHost.")
                            .Distinct(StringComparer.Ordinal)
                            .ToList();
                        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8);
                    }

                    _jobs[manifest.JobId] = manifest;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to load persisted export job manifest from {Path}", manifestPath);
            }
        }
    }

    private async Task PersistManifestAsync(JobManifest manifest, CancellationToken ct)
    {
        var jobDir = EnsureJobDirectory(manifest.JobId);
        var manifestPath = Path.Combine(jobDir, "job.json");
        var tempPath = manifestPath + ".tmp";
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, ct).ConfigureAwait(false);
        File.Move(tempPath, manifestPath, overwrite: true);
    }

    private string EnsureJobDirectory(string jobId)
    {
        var fullPath = GetJobDirectory(jobId);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    private string GetJobDirectory(string jobId)
        => Path.Combine(_jobsRootPath, jobId);

    private AdminExportJobListItemDto ToListItemDto(JobManifest manifest)
    {
        return new AdminExportJobListItemDto
        {
            JobId = manifest.JobId,
            Kind = manifest.Kind,
            Label = manifest.Label,
            ScopeSummary = manifest.ScopeSummary,
            Status = manifest.Status,
            RequestedAtUtc = manifest.RequestedAtUtc,
            CompletedAtUtc = manifest.CompletedAtUtc,
            ResultSummary = manifest.ResultSummary,
            ErrorMessage = manifest.ErrorMessage,
            HasDownload = manifest.File is not null && manifest.Status == AdminExportJobStatusDto.Completed
        };
    }

    private AdminExportJobDetailDto ToDetailDto(JobManifest manifest)
    {
        return new AdminExportJobDetailDto
        {
            JobId = manifest.JobId,
            Kind = manifest.Kind,
            Label = manifest.Label,
            ScopeSummary = manifest.ScopeSummary,
            Status = manifest.Status,
            RequestedAtUtc = manifest.RequestedAtUtc,
            StartedAtUtc = manifest.StartedAtUtc,
            CompletedAtUtc = manifest.CompletedAtUtc,
            ResultSummary = manifest.ResultSummary,
            ErrorMessage = manifest.ErrorMessage,
            Warnings = manifest.Warnings,
            File = manifest.File is null
                ? null
                : new AdminExportFileDto
                {
                    FileName = manifest.File.FileName,
                    ContentType = manifest.File.ContentType,
                    SizeBytes = manifest.File.SizeBytes,
                    CreatedAtUtc = manifest.File.CreatedAtUtc
                }
        };
    }

    private static string GetJobLabel(AdminExportKindDto kind)
    {
        return kind switch
        {
            AdminExportKindDto.TrackedChatMessages => "Выгрузка отслеживаемого чата",
            AdminExportKindDto.StartedUsers => "Выгрузка private-start пользователей",
            _ => "Выгрузка"
        };
    }

    private static string BuildScopeSummary(AdminExportJobCreateRequestDto request)
    {
        return request.Kind switch
        {
            AdminExportKindDto.TrackedChatMessages => $"Chat ID: {request.ChatId?.ToString(CultureInfo.InvariantCulture) ?? "не задан"}",
            AdminExportKindDto.StartedUsers => "Все записи private-start из текущей runtime-базы",
            _ => "Без описания области"
        };
    }

    private static string? MaskPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return null;

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length <= 4)
            return "сохранён";

        return $"сохранён, оканчивается на {digits[^4..]}";
    }

    private SqliteConnection OpenReadOnly()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly;Cache=Shared");
        connection.Open();
        return connection;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT COUNT(1)
FROM sqlite_master
WHERE type = 'table' AND name = $name;";
        cmd.Parameters.AddWithValue("$name", tableName);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result ?? 0, CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string tableName, string columnName, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(1) &&
                string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class RuntimeCapabilities
    {
        public bool MessagesExists { get; init; }
        public bool PrivateStartUsersExists { get; init; }
        public bool ChatRegistryExists { get; init; }
        public bool ChatRegistryHasTracked { get; init; }
        public bool ChatRegistryHasListed { get; init; }
        public bool ChatTrackingValidationAvailable => ChatRegistryExists && ChatRegistryHasTracked;
    }

    private sealed class JobExecutionResult
    {
        public required string ResultSummary { get; init; }
        public IReadOnlyList<string> Warnings { get; init; } = [];
        public required PersistedFileInfo File { get; init; }
    }

    private sealed class JobManifest
    {
        public required string JobId { get; init; }
        public AdminExportKindDto Kind { get; init; }
        public long? ChatId { get; init; }
        public required string Label { get; init; }
        public required string ScopeSummary { get; init; }
        public AdminExportJobStatusDto Status { get; set; }
        public DateTimeOffset RequestedAtUtc { get; init; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public string? ResultSummary { get; set; }
        public string? ErrorMessage { get; set; }
        public List<string> Warnings { get; set; } = [];
        public PersistedFileInfo? File { get; set; }
    }

    private sealed class PersistedFileInfo
    {
        public required string RelativePath { get; init; }
        public required string FileName { get; init; }
        public required string ContentType { get; init; }
        public long SizeBytes { get; init; }
        public DateTimeOffset CreatedAtUtc { get; init; }
    }
}
