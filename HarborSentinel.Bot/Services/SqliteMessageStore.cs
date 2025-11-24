using Microsoft.Data.Sqlite;
using HarborSentinel.Bot.Models;
using HarborSentinel.Bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HarborSentinel.Bot.Services;

public sealed class SqliteMessageStore : IMessageStore
{
    private readonly ILogger<SqliteMessageStore> _log;
    private readonly string _dbPath;

    public SqliteMessageStore(ILogger<SqliteMessageStore> log, IOptions<DatabaseOptions> opt)
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
CREATE TABLE IF NOT EXISTS messages(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  timestamp_utc   TEXT    NOT NULL,
  chat_id         INTEGER NOT NULL,
  chat_title      TEXT,
  chat_username   TEXT,
  user_id         INTEGER,
  user_username   TEXT,
  user_first_name TEXT,
  user_last_name  TEXT,
  message_id      INTEGER,
  text            TEXT
);
CREATE INDEX IF NOT EXISTS ix_messages_chat_user_time
ON messages(chat_id, user_id, timestamp_utc);";
        await cmd.ExecuteNonQueryAsync(ct);
        _log.LogInformation("SQLite ready at {Path}", Path.GetFullPath(_dbPath));
    }

    public async Task AppendAsync(MessageLogRecord r, CancellationToken ct)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
INSERT INTO messages(timestamp_utc, chat_id, chat_title, chat_username,
                     user_id, user_username, user_first_name, user_last_name,
                     message_id, text)
VALUES($ts,$chat,$ctitle,$cuser,$uid,$uuser,$uf,$ul,$mid,$text);";
        cmd.Parameters.AddWithValue("$ts", r.Timestamp.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$chat", r.ChatId);
        cmd.Parameters.AddWithValue("$ctitle", r.ChatTitle ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$cuser", r.ChatUsername ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$uid", r.FromId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$uuser", r.FromUsername ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$uf", r.FromFirstName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$ul", r.FromLastName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$mid", r.MessageId);
        cmd.Parameters.AddWithValue("$text", r.Text ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<DialogMessage>> QueryUserMessagesAsync(
        long chatId, long? userId, DateTimeOffset? sinceUtc, DateTimeOffset? untilUtc,
        int maxCount, CancellationToken ct)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT timestamp_utc, chat_id, chat_title, user_id, user_username, text
FROM messages
WHERE chat_id = $chat
  AND (($uid IS NULL AND user_id IS NULL) OR user_id = $uid)
  AND ($since IS NULL OR timestamp_utc >= $since)
  AND ($until IS NULL OR timestamp_utc <  $until)
ORDER BY timestamp_utc ASC
LIMIT $lim;";
        cmd.Parameters.AddWithValue("$chat", chatId);
        cmd.Parameters.AddWithValue("$uid", (object?)userId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$since", sinceUtc?.UtcDateTime.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$until", untilUtc?.UtcDateTime.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$lim", Math.Max(1, maxCount));

        var list = new List<DialogMessage>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var ts = DateTimeOffset.Parse(r.GetString(0));
            var chat = r.GetInt64(1);
            var title = r.IsDBNull(2) ? null : r.GetString(2);
            long? uid = r.IsDBNull(3) ? null : r.GetInt64(3);
            var un = r.IsDBNull(4) ? null : r.GetString(4);
            var text = r.IsDBNull(5) ? null : r.GetString(5);
            list.Add(new DialogMessage { Timestamp = ts, ChatId = chat, ChatTitle = title, UserId = uid, Username = un, Text = text });
        }
        return list;
    }

    private SqliteConnection Open()
    {
        var full = Path.GetFullPath(_dbPath);
        var conn = new SqliteConnection($"Data Source={full};Cache=Shared");
        conn.Open();
        return conn;
    }
}
