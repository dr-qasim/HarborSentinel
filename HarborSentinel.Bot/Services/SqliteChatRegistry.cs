using HarborSentinel.Bot.Models;
using HarborSentinel.Bot.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HarborSentinel.Bot.Services;

public sealed class SqliteChatRegistry : IChatRegistry
{
    private readonly ILogger<SqliteChatRegistry> _log;
    private readonly string _dbPath;

    public SqliteChatRegistry(ILogger<SqliteChatRegistry> log, IOptions<DatabaseOptions> opt)
    {
        _log = log;
        _dbPath = opt.Value.DbPath;
        var full = Path.GetFullPath(_dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    }

    public async Task InitAsync(CancellationToken ct)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
CREATE TABLE IF NOT EXISTS chat_registry(
  chat_id         INTEGER PRIMARY KEY,
  chat_title      TEXT,
  chat_username   TEXT,
  chat_type       TEXT    NOT NULL,
  status          TEXT,
  is_active       INTEGER NOT NULL,
  is_tracked      INTEGER NOT NULL DEFAULT 0,
  first_seen_utc  TEXT    NOT NULL,
  last_seen_utc   TEXT    NOT NULL,
  removed_utc     TEXT,
  updated_utc     TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_chat_registry_active
ON chat_registry(is_active);
CREATE INDEX IF NOT EXISTS ix_chat_registry_tracked_last_seen
ON chat_registry(is_tracked, last_seen_utc DESC);
CREATE INDEX IF NOT EXISTS ix_chat_registry_last_seen
ON chat_registry(last_seen_utc);";
        await cmd.ExecuteNonQueryAsync(ct);

        await EnsureTrackedColumnAsync(c, ct).ConfigureAwait(false);
        _log.LogInformation("Chat registry ready at {Path}", Path.GetFullPath(_dbPath));
    }

    public async Task UpsertAsync(ChatRegistryUpdate update, CancellationToken ct)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT INTO chat_registry(
  chat_id, chat_title, chat_username, chat_type, status,
  is_active, first_seen_utc, last_seen_utc, removed_utc, updated_utc
)
VALUES($id, $title, $username, $type, $status, $active, $first, $last, $removed, $updated)
ON CONFLICT(chat_id) DO UPDATE SET
  chat_title = COALESCE(excluded.chat_title, chat_registry.chat_title),
  chat_username = COALESCE(excluded.chat_username, chat_registry.chat_username),
  chat_type = excluded.chat_type,
  status = CASE
             WHEN excluded.status IS NOT NULL THEN excluded.status
             ELSE chat_registry.status
           END,
  is_active = excluded.is_active,
  last_seen_utc = excluded.last_seen_utc,
  removed_utc = CASE WHEN excluded.is_active = 1 THEN NULL ELSE excluded.removed_utc END,
  updated_utc = excluded.updated_utc
WHERE excluded.updated_utc >= chat_registry.updated_utc;";

        var nowIso = update.SeenAtUtc.UtcDateTime.ToString("O");
        var active = update.IsActive ? 1 : 0;
        var removedIso = update.IsActive ? null : nowIso;

        cmd.Parameters.AddWithValue("$id", update.ChatId);
        cmd.Parameters.AddWithValue("$title", (object?)update.ChatTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$username", (object?)update.ChatUsername ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$type", update.ChatType);
        cmd.Parameters.AddWithValue("$status", (object?)update.Status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$active", active);
        cmd.Parameters.AddWithValue("$first", nowIso);
        cmd.Parameters.AddWithValue("$last", nowIso);
        cmd.Parameters.AddWithValue("$removed", (object?)removedIso ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated", nowIso);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ChatRegistryEntry?> GetByChatIdAsync(long chatId, CancellationToken ct)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT chat_id, chat_title, chat_username, chat_type, status,
       is_active, is_tracked, first_seen_utc, last_seen_utc, removed_utc
FROM chat_registry
WHERE chat_id = $id
LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", chatId);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;

        return MapEntry(r);
    }

    public async Task<bool> SetTrackedAsync(long chatId, bool isTracked, CancellationToken ct)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
UPDATE chat_registry
SET is_tracked = $tracked
WHERE chat_id = $id;";

        cmd.Parameters.AddWithValue("$tracked", isTracked ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", chatId);

        var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<int> SetTrackedByFilterAsync(ChatRegistryFilter filter, bool isTracked, CancellationToken ct)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();

        var where = filter switch
        {
            ChatRegistryFilter.Active => "WHERE is_active = 1",
            ChatRegistryFilter.Removed => "WHERE is_active = 0",
            ChatRegistryFilter.Tracked => "WHERE is_tracked = 1",
            _ => string.Empty
        };

        cmd.CommandText = $@"
UPDATE chat_registry
SET is_tracked = $tracked
{where};";
        cmd.Parameters.AddWithValue("$tracked", isTracked ? 1 : 0);

        var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected;
    }

    public async Task<int> CountAsync(ChatRegistryFilter filter, CancellationToken ct)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = filter switch
        {
            ChatRegistryFilter.Active => "SELECT COUNT(1) FROM chat_registry WHERE is_active = 1;",
            ChatRegistryFilter.Removed => "SELECT COUNT(1) FROM chat_registry WHERE is_active = 0;",
            ChatRegistryFilter.Tracked => "SELECT COUNT(1) FROM chat_registry WHERE is_tracked = 1;",
            _ => "SELECT COUNT(1) FROM chat_registry;"
        };

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result ?? 0);
    }

    public async Task<IReadOnlyList<ChatRegistryEntry>> GetPageAsync(
        ChatRegistryFilter filter,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Max(1, pageSize);

        var where = filter switch
        {
            ChatRegistryFilter.Active => "WHERE is_active = 1",
            ChatRegistryFilter.Removed => "WHERE is_active = 0",
            ChatRegistryFilter.Tracked => "WHERE is_tracked = 1",
            _ => string.Empty
        };

        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $@"
SELECT chat_id, chat_title, chat_username, chat_type, status,
       is_active, is_tracked, first_seen_utc, last_seen_utc, removed_utc
FROM chat_registry
{where}
ORDER BY last_seen_utc DESC
LIMIT $lim OFFSET $off;";
        cmd.Parameters.AddWithValue("$lim", pageSize);
        cmd.Parameters.AddWithValue("$off", (page - 1) * pageSize);

        var list = new List<ChatRegistryEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(MapEntry(r));
        }

        return list;
    }

    private async Task EnsureTrackedColumnAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (await HasColumnAsync(connection, "chat_registry", "is_tracked", ct).ConfigureAwait(false))
            return;

        await using (var alterCmd = connection.CreateCommand())
        {
            alterCmd.CommandText = "ALTER TABLE chat_registry ADD COLUMN is_tracked INTEGER NOT NULL DEFAULT 0;";
            await alterCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var indexCmd = connection.CreateCommand();
        indexCmd.CommandText = "CREATE INDEX IF NOT EXISTS ix_chat_registry_tracked_last_seen ON chat_registry(is_tracked, last_seen_utc DESC);";
        await indexCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken ct)
    {
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

    private static ChatRegistryEntry MapEntry(SqliteDataReader r)
    {
        var chatId = r.GetInt64(0);
        var title = r.IsDBNull(1) ? null : r.GetString(1);
        var username = r.IsDBNull(2) ? null : r.GetString(2);
        var type = r.IsDBNull(3) ? null : r.GetString(3);
        var status = r.IsDBNull(4) ? null : r.GetString(4);
        var isActive = r.GetInt64(5) == 1;
        var isTracked = r.GetInt64(6) == 1;
        var firstSeen = DateTimeOffset.Parse(r.GetString(7));
        var lastSeen = DateTimeOffset.Parse(r.GetString(8));
        DateTimeOffset? removed = r.IsDBNull(9) ? null : DateTimeOffset.Parse(r.GetString(9));

        return new ChatRegistryEntry
        {
            ChatId = chatId,
            ChatTitle = title,
            ChatUsername = username,
            ChatType = type,
            Status = status,
            IsActive = isActive,
            IsTracked = isTracked,
            FirstSeenUtc = firstSeen,
            LastSeenUtc = lastSeen,
            RemovedUtc = removed
        };
    }

    private SqliteConnection Open()
    {
        var full = Path.GetFullPath(_dbPath);
        var conn = new SqliteConnection($"Data Source={full};Cache=Shared");
        conn.Open();
        return conn;
    }
}
