using HarborSentinel.Bot.Hadith;
using HarborSentinel.Bot.Hadith.Daily;
using HarborSentinel.Bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HarborSentinel.Bot.Services;

public sealed class JsonDailyHadithSettingsStore : IDailyHadithSettingsStore
{
    private readonly ILogger<JsonDailyHadithSettingsStore> _log;
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonDailyHadithSettingsStore(
        ILogger<JsonDailyHadithSettingsStore> log,
        IOptions<StorageOptions> storage)
    {
        _log = log;
        var basePath = storage.Value.BasePath ?? "data";
        Directory.CreateDirectory(basePath);
        _filePath = Path.Combine(basePath, "hadith_daily_settings.json");
    }

    public async Task<IReadOnlyList<DailyHadithSettings>> GetAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
                return Array.Empty<DailyHadithSettings>();

            var json = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
                return Array.Empty<DailyHadithSettings>();

            var list = JsonSerializer.Deserialize<List<DailyHadithSettings>>(json)
                       ?? new List<DailyHadithSettings>();

            return list;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to read daily hadith settings file");
            return Array.Empty<DailyHadithSettings>();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DailyHadithSettings?> GetForChatAsync(long chatId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = await LoadInternalAsync(ct).ConfigureAwait(false);
            return list.FirstOrDefault(s => s.ChatId == chatId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to get daily hadith settings for chat {ChatId}", chatId);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpsertAsync(DailyHadithSettings settings, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = await LoadInternalAsync(ct).ConfigureAwait(false);
            var idx = list.FindIndex(s => s.ChatId == settings.ChatId);
            if (idx >= 0)
                list[idx] = settings;
            else
                list.Add(settings);

            await SaveInternalAsync(list, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to upsert daily hadith settings for chat {ChatId}", settings.ChatId);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(long chatId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = await LoadInternalAsync(ct).ConfigureAwait(false);
            var removed = list.RemoveAll(s => s.ChatId == chatId);
            if (removed > 0)
                await SaveInternalAsync(list, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to delete daily hadith settings for chat {ChatId}", chatId);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<DailyHadithSettings>> LoadInternalAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
            return new List<DailyHadithSettings>();

        var json = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return new List<DailyHadithSettings>();

        var list = JsonSerializer.Deserialize<List<DailyHadithSettings>>(json);
        return list ?? new List<DailyHadithSettings>();
    }

    private async Task SaveInternalAsync(List<DailyHadithSettings> list, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(list, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(_filePath, json, ct).ConfigureAwait(false);
    }
}
