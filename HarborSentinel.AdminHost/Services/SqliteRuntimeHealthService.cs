using HarborSentinel.AdminContracts.Dashboard;
using HarborSentinel.AdminHost.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Reflection;
using System.Runtime.InteropServices;

namespace HarborSentinel.AdminHost.Services;

public sealed class SqliteRuntimeHealthService : IRuntimeHealthService
{
    private readonly HarborRuntimeOptions _runtimeOptions;
    private readonly AdminHostOptions _hostOptions;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<SqliteRuntimeHealthService> _log;

    public SqliteRuntimeHealthService(
        IOptions<HarborRuntimeOptions> runtimeOptions,
        IOptions<AdminHostOptions> hostOptions,
        IHostEnvironment environment,
        ILogger<SqliteRuntimeHealthService> log)
    {
        _runtimeOptions = runtimeOptions.Value;
        _hostOptions = hostOptions.Value;
        _environment = environment;
        _log = log;
    }

    public async Task<AdminDashboardSnapshotDto> GetSnapshotAsync(CancellationToken ct)
    {
        var databasePath = Path.GetFullPath(_runtimeOptions.DatabasePath);
        var storageBasePath = Path.GetFullPath(_runtimeOptions.StorageBasePath);
        var databaseExists = File.Exists(databasePath);
        var storageExists = Directory.Exists(storageBasePath);
        var warnings = new List<string>();
        var metrics = new List<DashboardMetricDto>();

        if (!storageExists)
            warnings.Add("Каталог runtime-хранилища не найден. Проверьте HarborRuntime:StorageBasePath.");

        if (!databaseExists)
        {
            warnings.Add("Файл runtime-базы данных не найден. AdminHost запущен, но чтение runtime работает в деградированном режиме.");
        }
        else
        {
            try
            {
                await using var connection = OpenReadOnly(databasePath);

                var hasListedChats = await ColumnExistsAsync(connection, "chat_registry", "is_listed", ct).ConfigureAwait(false);
                var chatScopeFilter = hasListedChats ? " WHERE is_listed = 1" : string.Empty;
                var activeWhere = hasListedChats ? "is_listed = 1 AND is_active = 1" : "is_active = 1";
                var trackedWhere = hasListedChats ? "is_listed = 1 AND is_tracked = 1" : "is_tracked = 1";

                metrics.Add(await BuildCountMetricAsync(connection, "messages_total", "Сообщения", "messages", "SELECT COUNT(1) FROM messages;", null, ct).ConfigureAwait(false));
                metrics.Add(await BuildCountMetricAsync(connection, "chats_total", "Чаты", "chat_registry", $"SELECT COUNT(1) FROM chat_registry{chatScopeFilter};", hasListedChats ? "Видимы текущему боту" : "Старая схема chat_registry", ct).ConfigureAwait(false));
                metrics.Add(await BuildCountMetricAsync(connection, "chats_active", "Активные чаты", "chat_registry", $"SELECT COUNT(1) FROM chat_registry WHERE {activeWhere};", null, ct, Array.Empty<(string Name, object Value)>(), "is_active").ConfigureAwait(false));
                metrics.Add(await BuildCountMetricAsync(connection, "chats_tracked", "Отслеживаемые чаты", "chat_registry", $"SELECT COUNT(1) FROM chat_registry WHERE {trackedWhere};", null, ct, Array.Empty<(string Name, object Value)>(), "is_tracked").ConfigureAwait(false));
                metrics.Add(await BuildCountMetricAsync(connection, "private_start_users", "Пользователи private-start", "private_start_users", "SELECT COUNT(1) FROM private_start_users;", null, ct).ConfigureAwait(false));
                metrics.Add(await BuildCountMetricAsync(connection, "flagged_events", "Помеченные события", "flagged_events", "SELECT COUNT(1) FROM flagged_events;", null, ct).ConfigureAwait(false));
                metrics.Add(await BuildCountMetricAsync(connection, "risk_events", "События риска", "risk_events", "SELECT COUNT(1) FROM risk_events;", null, ct).ConfigureAwait(false));
                metrics.Add(await BuildCountMetricAsync(connection, "active_vigilance", "Активная повышенная бдительность", "chat_vigilance_state", "SELECT COUNT(1) FROM chat_vigilance_state WHERE heightened_until_utc >= $now;", "Чаты с активным режимом повышенной бдительности", ct, [("$now", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"))], "heightened_until_utc").ConfigureAwait(false));

                var lastMessageUtc = await TryScalarStringAsync(connection, "messages", "SELECT MAX(timestamp_utc) FROM messages;", ct).ConfigureAwait(false);
                metrics.Add(new DashboardMetricDto
                {
                    Key = "last_message_utc",
                    Label = "Последнее сообщение (UTC)",
                    Value = string.IsNullOrWhiteSpace(lastMessageUtc) ? "нет данных" : lastMessageUtc,
                    Hint = "Определено по архиву runtime-сообщений"
                });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to build runtime dashboard snapshot");
                warnings.Add("Не удалось прочитать runtime-базу данных. Подробности см. в логах AdminHost.");
            }
        }

        if (string.IsNullOrWhiteSpace(_hostOptions.ApiToken))
            warnings.Add("Токен API не настроен. Используйте localhost-привязку и SSH-туннель.");

        if (!IsLoopbackOnly(_hostOptions.ListenUrl))
            warnings.Add("AdminHost не ограничен localhost-привязкой. Проверьте ListenUrl перед production-использованием.");

        return new AdminDashboardSnapshotDto
        {
            Product = "HarborSentinel AdminHost",
            Version = ResolveVersion(),
            Runtime = RuntimeInformation.FrameworkDescription,
            Environment = _environment.EnvironmentName,
            ListenAddress = _hostOptions.ListenUrl,
            DatabasePath = databasePath,
            StorageBasePath = storageBasePath,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            DatabaseExists = databaseExists,
            StorageExists = storageExists,
            DatabaseSizeBytes = databaseExists ? new FileInfo(databasePath).Length : null,
            ApiTokenRequired = !string.IsNullOrWhiteSpace(_hostOptions.ApiToken),
            LocalhostOnly = IsLoopbackOnly(_hostOptions.ListenUrl),
            Metrics = metrics,
            Warnings = warnings
        };
    }

    private static SqliteConnection OpenReadOnly(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Cache=Shared");
        connection.Open();
        return connection;
    }

    private static async Task<DashboardMetricDto> BuildCountMetricAsync(
        SqliteConnection connection,
        string key,
        string label,
        string tableName,
        string sql,
        string? hint,
        CancellationToken ct,
        (string Name, object Value)[] parameters,
        params string[] requiredColumns)
    {
        if (!await TableExistsAsync(connection, tableName, ct).ConfigureAwait(false))
        {
            return new DashboardMetricDto
            {
                Key = key,
                Label = label,
                Value = "нет данных",
                Hint = $"Таблица {tableName} отсутствует"
            };
        }

        foreach (var requiredColumn in requiredColumns)
        {
            if (await ColumnExistsAsync(connection, tableName, requiredColumn, ct).ConfigureAwait(false))
                continue;

            return new DashboardMetricDto
            {
                Key = key,
                Label = label,
                Value = "нет данных",
                Hint = $"Колонка {requiredColumn} отсутствует"
            };
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;

        foreach (var parameter in parameters)
            cmd.Parameters.AddWithValue(parameter.Name, parameter.Value);

        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        var count = Convert.ToInt64(scalar ?? 0);

        return new DashboardMetricDto
        {
            Key = key,
            Label = label,
            Value = count.ToString("N0"),
            Hint = hint
        };
    }

    private static Task<DashboardMetricDto> BuildCountMetricAsync(
        SqliteConnection connection,
        string key,
        string label,
        string tableName,
        string sql,
        string? hint,
        CancellationToken ct)
        => BuildCountMetricAsync(connection, key, label, tableName, sql, hint, ct, Array.Empty<(string Name, object Value)>(), Array.Empty<string>());

    private static async Task<string?> TryScalarStringAsync(
        SqliteConnection connection,
        string tableName,
        string sql,
        CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, tableName, ct).ConfigureAwait(false))
            return null;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result as string;
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
        return Convert.ToInt64(result ?? 0) > 0;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken ct)
    {
        if (!await TableExistsAsync(connection, tableName, ct).ConfigureAwait(false))
            return false;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsLoopbackOnly(string listenUrl)
    {
        if (!Uri.TryCreate(listenUrl, UriKind.Absolute, out var uri))
            return false;

        return string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
            return informational;

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
