# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

Requires .NET 10.0.201 (see `global.json`).

```bash
# Build everything
dotnet build HarborSentinel.sln

# Run the bot (development)
dotnet run --project HarborSentinel.Bot/HarborSentinel.Bot.csproj

# Run the admin API
dotnet run --project HarborSentinel.AdminHost/HarborSentinel.AdminHost.csproj

# Run the admin desktop client
dotnet run --project HarborSentinel.AdminConsole/HarborSentinel.AdminConsole.csproj
```

There are no test projects. There is no lint step.

**Publishing** (PowerShell 7+ required):
```powershell
./scripts/Publish-HarborSentinelBot.ps1
./scripts/Publish-HarborSentinelAdminHost.ps1
```
Produces self-contained Linux x64 binaries + tar.gz + SHA256 in `artifacts/releases/`. Published artifacts have `appsettings.json` removed for security.

## Project Architecture

Four projects sharing a solution:

- **HarborSentinel.Bot** — Worker service (Telegram bot). Core loop, AI moderation, Islamic content delivery, SQLite storage, JSONL audit trail.
- **HarborSentinel.AdminHost** — ASP.NET minimal API (`http://127.0.0.1:58421`). Exposes bot internals (chats, incidents, users, exports, diagnostics) over REST. Local-only in production. Auth via `X-Admin-Token` header.
- **HarborSentinel.AdminConsole** — WPF desktop app. Consumes AdminHost API; includes SSH tunnel manager for remote connections. Windows-only (`net10.0-windows`).
- **HarborSentinel.AdminContracts** — Shared DTOs between AdminHost and AdminConsole. No logic, just contracts.

## Bot Architecture

Message flow: Telegram → `UpdateHandler` → `SqliteMessageStore` + `IRecentMessagesCache` → `DeepSeekModerationService` → incident tracking / moderator notification.

**Moderation** uses the DeepSeek API (OpenAI-compatible endpoint at `https://api.deepseek.com`). Primary model: `deepseek-reasoner`; fallback: `deepseek-chat`. Moderation evaluates risk label, risk level, confidence, and context flag. A cumulative risk window (30 min) triggers vigilance mode (72 hrs) for a chat.

**Islamic content**: Hadiths, Quran ayat, duas, and azkar are loaded from JSON files in `data/`. The `DailyHadithBackgroundService` delivers scheduled hadith to registered chats. External API (`HadeethEncClient`) is used for on-demand content.

**Storage layout:**
- `data/harborsentinel.db` — SQLite (messages, chats, members, incidents, users)
- `data/messages-*.jsonl` — JSONL audit log (append-only, date-keyed)
- `data/flagged_lists/` — exported flagged dialogs
- `data/tracked_exports/` — tracked chat exports

## Configuration

Hierarchical config: `appsettings.json` → `appsettings.{Environment}.json` → environment variables / user secrets.

**Required secrets (never hardcode):**
- `TELEGRAM_BOT_TOKEN` or `Bot:Token`
- `DEEPSEEK_API_KEY` or `Moderation:ApiKey`

**Key config sections (Bot):**
- `Bot.ModeratorIds` — Telegram user IDs that receive moderation alerts and can use bot commands
- `Moderation.*` — AI moderation tuning (thresholds, concurrency, timeouts, context window)
- `Storage.*` — paths and flush intervals for JSONL audit log
- `Database.DbPath` — SQLite path

**AdminHost production requirements:** `AdminHost.ApiToken` must be non-empty; `AdminHost.ListenUrl` must be localhost. Startup validates both or throws.

## Version

Defined in `Directory.Build.props` as `<VersionPrefix>`. Format: `YYYY.M.DD`.

## Language Note

Source code comments and some configuration strings are in Russian. This is intentional.
