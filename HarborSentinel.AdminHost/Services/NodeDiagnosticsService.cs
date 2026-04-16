using HarborSentinel.AdminContracts.Diagnostics;
using HarborSentinel.AdminContracts.Exports;
using HarborSentinel.AdminHost.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Reflection;
using System.Runtime.InteropServices;

namespace HarborSentinel.AdminHost.Services;

public sealed class NodeDiagnosticsService : INodeDiagnosticsService
{
    private readonly HarborRuntimeOptions _runtimeOptions;
    private readonly AdminHostOptions _hostOptions;
    private readonly IHostEnvironment _environment;
    private readonly IExportJobService _exportJobService;
    private readonly IRecentDiagnosticEventBuffer _recentEvents;
    private readonly ILogger<NodeDiagnosticsService> _log;

    public NodeDiagnosticsService(
        IOptions<HarborRuntimeOptions> runtimeOptions,
        IOptions<AdminHostOptions> hostOptions,
        IHostEnvironment environment,
        IExportJobService exportJobService,
        IRecentDiagnosticEventBuffer recentEvents,
        ILogger<NodeDiagnosticsService> log)
    {
        _runtimeOptions = runtimeOptions.Value;
        _hostOptions = hostOptions.Value;
        _environment = environment;
        _exportJobService = exportJobService;
        _recentEvents = recentEvents;
        _log = log;
    }

    public async Task<AdminNodeStatusDto> GetNodeStatusAsync(CancellationToken ct)
    {
        var generatedAtUtc = DateTimeOffset.UtcNow;
        var databasePath = Path.GetFullPath(_runtimeOptions.DatabasePath);
        var storageBasePath = Path.GetFullPath(_runtimeOptions.StorageBasePath);
        var trackedExportsPath = Path.Combine(storageBasePath, NormalizeDir(_runtimeOptions.TrackedExportsDir, "tracked_exports"));
        var adminExportsPath = Path.Combine(storageBasePath, NormalizeDir(_runtimeOptions.AdminExportsDir, "admin_exports"));
        var adminStatePath = Path.Combine(storageBasePath, NormalizeDir(_runtimeOptions.AdminStateDir, "adminhost_state"));
        var adminJobsPath = Path.Combine(adminExportsPath, "adminhost_jobs");
        var walPath = databasePath + "-wal";
        var shmPath = databasePath + "-shm";

        var localhostOnly = IsLoopbackOnly(_hostOptions.ListenUrl);
        var apiTokenRequired = !string.IsNullOrWhiteSpace(_hostOptions.ApiToken);

        var paths = new List<AdminNodePathStatusDto>
        {
            CheckDirectoryPath("storage_base", "Runtime storage", storageBasePath, AdminDiagnosticSeverityDto.Critical, false),
            CheckFilePath("runtime_db", "Runtime SQLite", databasePath, AdminDiagnosticSeverityDto.Critical),
            CheckFilePath("runtime_db_wal", "Runtime SQLite WAL", walPath, AdminDiagnosticSeverityDto.Info),
            CheckFilePath("runtime_db_shm", "Runtime SQLite SHM", shmPath, AdminDiagnosticSeverityDto.Info),
            CheckDirectoryPath("tracked_exports", "Tracked exports", trackedExportsPath, AdminDiagnosticSeverityDto.Warning, false),
            CheckDirectoryPath("admin_exports", "Admin exports", adminExportsPath, AdminDiagnosticSeverityDto.Warning, true),
            CheckDirectoryPath("admin_jobs", "AdminHost jobs", adminJobsPath, AdminDiagnosticSeverityDto.Warning, true),
            CheckDirectoryPath("admin_state", "AdminHost state", adminStatePath, AdminDiagnosticSeverityDto.Warning, true)
        };

        var runtimeTables = new List<AdminRuntimeTableStatusDto>();
        var capabilities = new List<AdminNodeCapabilityStatusDto>();
        var components = new List<AdminNodeComponentStatusDto>();

        var schemaMode = AdminRuntimeSchemaModeDto.Missing;
        var runtimeDatabaseReadable = false;
        string? journalMode = null;
        Exception? runtimeReadException = null;

        if (File.Exists(databasePath))
        {
            try
            {
                await using var connection = OpenReadOnly(databasePath);
                runtimeDatabaseReadable = true;
                journalMode = await TryScalarStringAsync(connection, "PRAGMA journal_mode;", ct).ConfigureAwait(false);
                runtimeTables.AddRange(await LoadRuntimeTablesAsync(connection, ct).ConfigureAwait(false));
                schemaMode = DetectSchemaMode(runtimeTables);
            }
            catch (Exception ex)
            {
                runtimeReadException = ex;
                _log.LogWarning(ex, "Node diagnostics failed to read runtime SQLite");
            }
        }

        AdminExportCatalogResponseDto? exportCatalog = null;
        if (runtimeDatabaseReadable)
        {
            try
            {
                exportCatalog = await _exportJobService.GetCatalogAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Node diagnostics failed to read export catalog");
            }
        }

        var chatRegistry = FindTable(runtimeTables, "chat_registry");
        var messages = FindTable(runtimeTables, "messages");
        var privateStartUsers = FindTable(runtimeTables, "private_start_users");
        var riskEvents = FindTable(runtimeTables, "risk_events");

        capabilities.Add(BuildChatsCapability(runtimeDatabaseReadable, chatRegistry));
        capabilities.Add(BuildUsersCapability(runtimeDatabaseReadable, privateStartUsers));
        capabilities.Add(BuildIncidentsCapability(runtimeDatabaseReadable, riskEvents));
        capabilities.Add(BuildExportsCapability(runtimeDatabaseReadable, exportCatalog, adminExportsPath, adminJobsPath));
        capabilities.Add(BuildInboxCapability(runtimeDatabaseReadable, riskEvents, adminStatePath));

        components.Add(new AdminNodeComponentStatusDto
        {
            Key = "adminhost_service",
            Label = "AdminHost",
            Severity = AdminDiagnosticSeverityDto.Healthy,
            Summary = "AdminHost отвечает и сформировал diagnostics snapshot.",
            Details = $"{ResolveVersion()} | {_environment.EnvironmentName} | {RuntimeInformation.FrameworkDescription}"
        });
        components.Add(BuildSecurityComponent(localhostOnly, apiTokenRequired));
        components.Add(BuildRuntimeDatabaseComponent(databasePath, runtimeDatabaseReadable, journalMode, runtimeReadException));
        components.Add(BuildSchemaComponent(schemaMode, chatRegistry, messages, runtimeDatabaseReadable));

        var exportsCapability = capabilities.First(static item => item.Key == "exports");
        components.Add(new AdminNodeComponentStatusDto
        {
            Key = "exports_component",
            Label = "Server-side выгрузки",
            Severity = exportsCapability.Severity,
            Summary = exportsCapability.Summary,
            Details = exportsCapability.Impact
        });

        var inboxCapability = capabilities.First(static item => item.Key == "incident_inbox");
        components.Add(new AdminNodeComponentStatusDto
        {
            Key = "inbox_component",
            Label = "Durable incident inbox",
            Severity = inboxCapability.Severity,
            Summary = inboxCapability.Summary,
            Details = inboxCapability.Impact
        });

        var warningCount = CountSeverity(AdminDiagnosticSeverityDto.Warning, components, paths, runtimeTables, capabilities);
        var criticalCount = CountSeverity(AdminDiagnosticSeverityDto.Critical, components, paths, runtimeTables, capabilities)
            + CountSeverity(AdminDiagnosticSeverityDto.Unavailable, components, paths, runtimeTables, capabilities);

        return new AdminNodeStatusDto
        {
            Product = "HarborSentinel AdminHost",
            Version = ResolveVersion(),
            Runtime = RuntimeInformation.FrameworkDescription,
            Environment = _environment.EnvironmentName,
            ListenAddress = _hostOptions.ListenUrl,
            DatabasePath = databasePath,
            StorageBasePath = storageBasePath,
            GeneratedAtUtc = generatedAtUtc,
            SchemaMode = schemaMode,
            LocalhostOnly = localhostOnly,
            ApiTokenRequired = apiTokenRequired,
            RuntimeDatabaseReadable = runtimeDatabaseReadable,
            WarningCount = warningCount,
            CriticalCount = criticalCount,
            Components = components,
            Paths = paths,
            RuntimeTables = runtimeTables,
            Capabilities = capabilities,
            RecentIssues = _recentEvents.GetRecent(20)
        };
    }

    private async Task<IReadOnlyList<AdminRuntimeTableStatusDto>> LoadRuntimeTablesAsync(SqliteConnection connection, CancellationToken ct)
    {
        return
        [
            await InspectTableAsync(connection, "chat_registry", AdminDiagnosticSeverityDto.Critical, ct, "is_listed").ConfigureAwait(false),
            await InspectTableAsync(connection, "messages", AdminDiagnosticSeverityDto.Critical, ct).ConfigureAwait(false),
            await InspectTableAsync(connection, "private_start_users", AdminDiagnosticSeverityDto.Warning, ct).ConfigureAwait(false),
            await InspectTableAsync(connection, "flagged_events", AdminDiagnosticSeverityDto.Warning, ct).ConfigureAwait(false),
            await InspectTableAsync(connection, "risk_events", AdminDiagnosticSeverityDto.Warning, ct).ConfigureAwait(false),
            await InspectTableAsync(connection, "risk_alert_state", AdminDiagnosticSeverityDto.Warning, ct).ConfigureAwait(false),
            await InspectTableAsync(connection, "chat_vigilance_state", AdminDiagnosticSeverityDto.Warning, ct).ConfigureAwait(false)
        ];
    }

    private static string NormalizeDir(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static AdminNodePathStatusDto CheckDirectoryPath(
        string key,
        string label,
        string fullPath,
        AdminDiagnosticSeverityDto missingSeverity,
        bool allowWriteProbe)
    {
        if (!Directory.Exists(fullPath))
        {
            return new AdminNodePathStatusDto
            {
                Key = key,
                Label = label,
                Kind = "directory",
                FullPath = fullPath,
                Severity = missingSeverity,
                Exists = false,
                Readable = false,
                Writable = allowWriteProbe ? false : null,
                Summary = "Каталог не найден."
            };
        }

        var readable = true;
        var summary = "Каталог доступен для чтения.";
        try
        {
            using var enumerator = Directory.EnumerateFileSystemEntries(fullPath).GetEnumerator();
            _ = enumerator.MoveNext();
        }
        catch
        {
            readable = false;
            summary = "Каталог найден, но чтение недоступно.";
        }

        bool? writable = null;
        var severity = readable ? AdminDiagnosticSeverityDto.Healthy : AdminDiagnosticSeverityDto.Critical;
        if (allowWriteProbe && readable)
        {
            writable = TryWriteProbe(fullPath);
            if (writable == true)
            {
                summary = "Каталог доступен для чтения и записи.";
            }
            else
            {
                severity = AdminDiagnosticSeverityDto.Warning;
                summary = "Каталог читается, но запись недоступна.";
            }
        }

        var lastWriteUtc = new DirectoryInfo(fullPath).LastWriteTimeUtc;
        return new AdminNodePathStatusDto
        {
            Key = key,
            Label = label,
            Kind = "directory",
            FullPath = fullPath,
            Severity = severity,
            Exists = true,
            Readable = readable,
            Writable = writable,
            LastWriteUtc = lastWriteUtc == DateTime.MinValue ? null : new DateTimeOffset(lastWriteUtc, TimeSpan.Zero),
            Summary = summary
        };
    }

    private static AdminNodePathStatusDto CheckFilePath(
        string key,
        string label,
        string fullPath,
        AdminDiagnosticSeverityDto missingSeverity)
    {
        if (!File.Exists(fullPath))
        {
            return new AdminNodePathStatusDto
            {
                Key = key,
                Label = label,
                Kind = "file",
                FullPath = fullPath,
                Severity = missingSeverity,
                Exists = false,
                Readable = false,
                Summary = missingSeverity == AdminDiagnosticSeverityDto.Info
                    ? "Файл сейчас отсутствует. Это допустимо, если sidecar не используется."
                    : "Файл не найден."
            };
        }

        var readable = true;
        var summary = "Файл доступен для чтения.";
        try
        {
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            _ = stream.Length;
        }
        catch
        {
            readable = false;
            summary = "Файл найден, но чтение недоступно.";
        }

        var info = new FileInfo(fullPath);
        var lastWriteUtc = info.LastWriteTimeUtc;
        return new AdminNodePathStatusDto
        {
            Key = key,
            Label = label,
            Kind = "file",
            FullPath = fullPath,
            Severity = readable ? AdminDiagnosticSeverityDto.Healthy : AdminDiagnosticSeverityDto.Critical,
            Exists = true,
            Readable = readable,
            SizeBytes = readable ? info.Length : null,
            LastWriteUtc = lastWriteUtc == DateTime.MinValue ? null : new DateTimeOffset(lastWriteUtc, TimeSpan.Zero),
            Summary = summary
        };
    }

    private static bool TryWriteProbe(string directoryPath)
    {
        var probePath = Path.Combine(directoryPath, $".write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probePath, "ok");
            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static SqliteConnection OpenReadOnly(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Cache=Shared");
        connection.Open();
        return connection;
    }

    private static async Task<AdminRuntimeTableStatusDto> InspectTableAsync(
        SqliteConnection connection,
        string tableName,
        AdminDiagnosticSeverityDto missingSeverity,
        CancellationToken ct,
        params string[] requiredColumns)
    {
        if (!await TableExistsAsync(connection, tableName, ct).ConfigureAwait(false))
        {
            return new AdminRuntimeTableStatusDto
            {
                TableName = tableName,
                Severity = missingSeverity,
                Exists = false,
                MissingColumns = [],
                Summary = $"Таблица {tableName} отсутствует."
            };
        }

        var missingColumns = new List<string>();
        foreach (var columnName in requiredColumns)
        {
            if (!await ColumnExistsAsync(connection, tableName, columnName, ct).ConfigureAwait(false))
                missingColumns.Add(columnName);
        }

        return new AdminRuntimeTableStatusDto
        {
            TableName = tableName,
            Severity = missingColumns.Count == 0 ? AdminDiagnosticSeverityDto.Healthy : AdminDiagnosticSeverityDto.Warning,
            Exists = true,
            MissingColumns = missingColumns,
            Summary = missingColumns.Count == 0
                ? $"Таблица {tableName} доступна."
                : $"Таблица {tableName} доступна, но не хватает колонок: {string.Join(", ", missingColumns)}."
        };
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
SELECT COUNT(1)
FROM sqlite_master
WHERE type = 'table' AND name = $name;
""";
        cmd.Parameters.AddWithValue("$name", tableName);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result ?? 0) > 0;
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string tableName, string columnName, CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, tableName, ct).ConfigureAwait(false))
            return false;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var currentName = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (string.Equals(currentName, columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static async Task<string?> TryScalarStringAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result?.ToString();
    }

    private static AdminRuntimeSchemaModeDto DetectSchemaMode(IReadOnlyList<AdminRuntimeTableStatusDto> tables)
    {
        if (tables.Count == 0 || tables.All(static table => !table.Exists))
            return AdminRuntimeSchemaModeDto.Missing;

        var chatRegistry = FindTable(tables, "chat_registry");
        var messages = FindTable(tables, "messages");
        var allPresent = tables.All(static table => table.Exists && table.MissingColumns.Count == 0);
        if (allPresent)
            return AdminRuntimeSchemaModeDto.Full;

        if (chatRegistry?.Exists == true &&
            chatRegistry.MissingColumns.Contains("is_listed", StringComparer.OrdinalIgnoreCase) &&
            messages?.Exists == true)
        {
            return AdminRuntimeSchemaModeDto.Legacy;
        }

        return AdminRuntimeSchemaModeDto.Partial;
    }

    private static AdminNodeCapabilityStatusDto BuildChatsCapability(bool runtimeReadable, AdminRuntimeTableStatusDto? chatRegistry)
    {
        if (!runtimeReadable)
        {
            return CreateCapability(
                "chats",
                "Чаты",
                AdminDiagnosticSeverityDto.Critical,
                "Экран «Чаты» недоступен: runtime SQLite не читается.",
                "Нужно восстановить чтение runtime DB и WAL-доступ.");
        }

        if (chatRegistry?.Exists != true)
        {
            return CreateCapability(
                "chats",
                "Чаты",
                AdminDiagnosticSeverityDto.Critical,
                "Экран «Чаты» недоступен: таблица chat_registry отсутствует.",
                "Реестр чатов и current scope не могут быть построены.");
        }

        if (chatRegistry.MissingColumns.Contains("is_listed", StringComparer.OrdinalIgnoreCase))
        {
            return CreateCapability(
                "chats",
                "Чаты",
                AdminDiagnosticSeverityDto.Warning,
                "Экран «Чаты» работает в legacy schema режиме.",
                "Current-scope фильтрация и visibility текущего бота ограничены.");
        }

        return CreateCapability(
            "chats",
            "Чаты",
            AdminDiagnosticSeverityDto.Healthy,
            "Экран «Чаты» работает в полном scope-aware режиме.",
            "Реестр чатов и признаки current scope доступны.");
    }

    private static AdminNodeCapabilityStatusDto BuildUsersCapability(bool runtimeReadable, AdminRuntimeTableStatusDto? privateStartUsers)
    {
        if (!runtimeReadable)
        {
            return CreateCapability(
                "users",
                "Пользователи",
                AdminDiagnosticSeverityDto.Critical,
                "Экран «Пользователи» недоступен: runtime SQLite не читается.",
                "Нужно восстановить чтение runtime DB.");
        }

        if (privateStartUsers?.Exists != true)
        {
            return CreateCapability(
                "users",
                "Пользователи",
                AdminDiagnosticSeverityDto.Warning,
                "Таблица private_start_users отсутствует. Экран «Пользователи» работает частично.",
                "Источники private-start и связанные фильтры урезаны.");
        }

        return CreateCapability(
            "users",
            "Пользователи",
            AdminDiagnosticSeverityDto.Healthy,
            "Экран «Пользователи» работает без урезаний.",
            "Private-start и historical user review доступны.");
    }

    private static AdminNodeCapabilityStatusDto BuildIncidentsCapability(bool runtimeReadable, AdminRuntimeTableStatusDto? riskEvents)
    {
        if (!runtimeReadable)
        {
            return CreateCapability(
                "incidents",
                "Инциденты",
                AdminDiagnosticSeverityDto.Critical,
                "Экран «Инциденты» недоступен: runtime SQLite не читается.",
                "Incident feed и historical review не могут быть построены.");
        }

        if (riskEvents?.Exists != true)
        {
            return CreateCapability(
                "incidents",
                "Инциденты",
                AdminDiagnosticSeverityDto.Warning,
                "Таблица risk_events отсутствует. Incident feed работает в degraded mode.",
                "Historical risk visibility и incident inbox ограничены.");
        }

        return CreateCapability(
            "incidents",
            "Инциденты",
            AdminDiagnosticSeverityDto.Healthy,
            "Экран «Инциденты» работает с historical context.",
            "Risk events доступны для review и durable inbox.");
    }

    private static AdminNodeCapabilityStatusDto BuildExportsCapability(
        bool runtimeReadable,
        AdminExportCatalogResponseDto? exportCatalog,
        string adminExportsPath,
        string adminJobsPath)
    {
        if (!runtimeReadable)
        {
            return CreateCapability(
                "exports",
                "Выгрузки",
                AdminDiagnosticSeverityDto.Critical,
                "Выгрузки недоступны: runtime SQLite не читается.",
                "Server-side export jobs не смогут читать источники данных.");
        }

        if (!Directory.Exists(adminExportsPath))
        {
            return CreateCapability(
                "exports",
                "Выгрузки",
                AdminDiagnosticSeverityDto.Warning,
                "Каталог admin_exports отсутствует.",
                "Экран «Выгрузки» будет работать частично до создания server-side storage.");
        }

        if (!Directory.Exists(adminJobsPath))
        {
            return CreateCapability(
                "exports",
                "Выгрузки",
                AdminDiagnosticSeverityDto.Warning,
                "Каталог adminhost_jobs отсутствует.",
                "Новые export jobs пока не могут быть сохранены.");
        }

        if (exportCatalog is null)
        {
            return CreateCapability(
                "exports",
                "Выгрузки",
                AdminDiagnosticSeverityDto.Warning,
                "Каталог выгрузок не удалось прочитать.",
                "Проверьте server-side export diagnostics и логи AdminHost.");
        }

        var availableItems = exportCatalog.Items.Count(static item => item.IsAvailable);
        if (availableItems == 0)
        {
            return CreateCapability(
                "exports",
                "Выгрузки",
                AdminDiagnosticSeverityDto.Warning,
                "Каталог выгрузок доступен, но ни один export source сейчас не готов.",
                string.Join(" | ", exportCatalog.Warnings.DefaultIfEmpty("Runtime schema ограничивает export kinds.")));
        }

        return CreateCapability(
            "exports",
            "Выгрузки",
            AdminDiagnosticSeverityDto.Healthy,
            $"Server-side выгрузки доступны: активных источников {availableItems}.",
            exportCatalog.Warnings.Count == 0
                ? "Export catalog и server-side jobs готовы."
                : string.Join(" | ", exportCatalog.Warnings));
    }

    private static AdminNodeCapabilityStatusDto BuildInboxCapability(
        bool runtimeReadable,
        AdminRuntimeTableStatusDto? riskEvents,
        string adminStatePath)
    {
        if (!runtimeReadable)
        {
            return CreateCapability(
                "incident_inbox",
                "Durable inbox",
                AdminDiagnosticSeverityDto.Critical,
                "Durable incident inbox недоступен: runtime SQLite не читается.",
                "Unread/startup sync не могут быть построены.");
        }

        if (riskEvents?.Exists != true)
        {
            return CreateCapability(
                "incident_inbox",
                "Durable inbox",
                AdminDiagnosticSeverityDto.Warning,
                "Durable incident inbox работает в degraded mode: таблица risk_events отсутствует.",
                "Unread backlog и incident review ограничены.");
        }

        if (!Directory.Exists(adminStatePath))
        {
            return CreateCapability(
                "incident_inbox",
                "Durable inbox",
                AdminDiagnosticSeverityDto.Warning,
                "Каталог adminhost_state пока не создан.",
                "Он будет создан при первом рабочем использовании inbox state.");
        }

        return CreateCapability(
            "incident_inbox",
            "Durable inbox",
            AdminDiagnosticSeverityDto.Healthy,
            "Durable incident inbox доступен.",
            "Unread/read/acknowledged state хранится в AdminHost-owned storage.");
    }

    private static AdminNodeCapabilityStatusDto CreateCapability(
        string key,
        string label,
        AdminDiagnosticSeverityDto severity,
        string summary,
        string impact)
        => new()
        {
            Key = key,
            Label = label,
            Severity = severity,
            Summary = summary,
            Impact = impact
        };

    private static AdminNodeComponentStatusDto BuildSecurityComponent(bool localhostOnly, bool apiTokenRequired)
    {
        if (localhostOnly && apiTokenRequired)
        {
            return new AdminNodeComponentStatusDto
            {
                Key = "security_posture",
                Label = "Безопасность доступа",
                Severity = AdminDiagnosticSeverityDto.Healthy,
                Summary = "AdminHost ограничен localhost и требует X-Admin-Token.",
                Details = "Desktop должен работать через SSH tunnel и token-based auth."
            };
        }

        if (localhostOnly)
        {
            return new AdminNodeComponentStatusDto
            {
                Key = "security_posture",
                Label = "Безопасность доступа",
                Severity = AdminDiagnosticSeverityDto.Warning,
                Summary = "AdminHost ограничен localhost, но API token не настроен.",
                Details = "Транспорт защищён SSH tunnel, но app-level защита ослаблена."
            };
        }

        return new AdminNodeComponentStatusDto
        {
            Key = "security_posture",
            Label = "Безопасность доступа",
            Severity = AdminDiagnosticSeverityDto.Critical,
            Summary = "AdminHost не ограничен localhost-привязкой.",
            Details = "Нужно вернуть ListenUrl к 127.0.0.1 или localhost."
        };
    }

    private static AdminNodeComponentStatusDto BuildRuntimeDatabaseComponent(
        string databasePath,
        bool runtimeReadable,
        string? journalMode,
        Exception? runtimeReadException)
    {
        if (!File.Exists(databasePath))
        {
            return new AdminNodeComponentStatusDto
            {
                Key = "runtime_database",
                Label = "Runtime SQLite",
                Severity = AdminDiagnosticSeverityDto.Critical,
                Summary = "Файл runtime SQLite отсутствует.",
                Details = databasePath
            };
        }

        if (!runtimeReadable)
        {
            return new AdminNodeComponentStatusDto
            {
                Key = "runtime_database",
                Label = "Runtime SQLite",
                Severity = AdminDiagnosticSeverityDto.Critical,
                Summary = "Runtime SQLite найдена, но чтение недоступно.",
                Details = runtimeReadException?.Message
            };
        }

        return new AdminNodeComponentStatusDto
        {
            Key = "runtime_database",
            Label = "Runtime SQLite",
            Severity = AdminDiagnosticSeverityDto.Healthy,
            Summary = string.IsNullOrWhiteSpace(journalMode)
                ? "Runtime SQLite доступна для чтения."
                : $"Runtime SQLite доступна для чтения. Journal mode: {journalMode}.",
            Details = databasePath
        };
    }

    private static AdminNodeComponentStatusDto BuildSchemaComponent(
        AdminRuntimeSchemaModeDto schemaMode,
        AdminRuntimeTableStatusDto? chatRegistry,
        AdminRuntimeTableStatusDto? messages,
        bool runtimeReadable)
    {
        if (!runtimeReadable)
        {
            return new AdminNodeComponentStatusDto
            {
                Key = "schema_mode",
                Label = "Schema mode",
                Severity = AdminDiagnosticSeverityDto.Critical,
                Summary = "Schema mode не определён: runtime SQLite недоступна.",
                Details = "Проверьте runtime DB, WAL и filesystem rights."
            };
        }

        return schemaMode switch
        {
            AdminRuntimeSchemaModeDto.Full => new AdminNodeComponentStatusDto
            {
                Key = "schema_mode",
                Label = "Schema mode",
                Severity = AdminDiagnosticSeverityDto.Healthy,
                Summary = "Runtime schema полная.",
                Details = "Все ключевые таблицы и scope-aware признаки доступны."
            },
            AdminRuntimeSchemaModeDto.Legacy => new AdminNodeComponentStatusDto
            {
                Key = "schema_mode",
                Label = "Schema mode",
                Severity = AdminDiagnosticSeverityDto.Warning,
                Summary = "Runtime schema legacy.",
                Details = "Часть scope-aware логики ограничена, например current-scope visibility по чатам."
            },
            AdminRuntimeSchemaModeDto.Partial => new AdminNodeComponentStatusDto
            {
                Key = "schema_mode",
                Label = "Schema mode",
                Severity = AdminDiagnosticSeverityDto.Warning,
                Summary = "Runtime schema частичная.",
                Details = BuildPartialSchemaDetails(chatRegistry, messages)
            },
            _ => new AdminNodeComponentStatusDto
            {
                Key = "schema_mode",
                Label = "Schema mode",
                Severity = AdminDiagnosticSeverityDto.Critical,
                Summary = "Runtime schema не обнаружена.",
                Details = "Ключевые runtime-таблицы отсутствуют."
            }
        };
    }

    private static string BuildPartialSchemaDetails(AdminRuntimeTableStatusDto? chatRegistry, AdminRuntimeTableStatusDto? messages)
    {
        var parts = new List<string>();
        if (chatRegistry?.Exists != true)
            parts.Add("chat_registry отсутствует");
        if (messages?.Exists != true)
            parts.Add("messages отсутствует");

        return parts.Count == 0
            ? "Часть вспомогательных таблиц отсутствует, поэтому отдельные экраны работают частично."
            : string.Join(" | ", parts);
    }

    private static AdminRuntimeTableStatusDto? FindTable(IReadOnlyList<AdminRuntimeTableStatusDto> tables, string tableName)
        => tables.FirstOrDefault(item => string.Equals(item.TableName, tableName, StringComparison.OrdinalIgnoreCase));

    private static int CountSeverity(
        AdminDiagnosticSeverityDto severity,
        IReadOnlyList<AdminNodeComponentStatusDto> components,
        IReadOnlyList<AdminNodePathStatusDto> paths,
        IReadOnlyList<AdminRuntimeTableStatusDto> runtimeTables,
        IReadOnlyList<AdminNodeCapabilityStatusDto> capabilities)
    {
        return components.Count(item => item.Severity == severity)
            + paths.Count(item => item.Severity == severity)
            + runtimeTables.Count(item => item.Severity == severity)
            + capabilities.Count(item => item.Severity == severity);
    }

    private static bool IsLoopbackOnly(string listenUrl)
    {
        if (!Uri.TryCreate(listenUrl, UriKind.Absolute, out var uri))
            return false;

        return string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational;

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
