using HarborSentinel.Bot.Models;

namespace HarborSentinel.Bot.Services;

/// <summary>
/// Собирает историю сообщений пользователя за N дней из наших JSONL-логов по конкретному чату
/// и сохраняет JSON-файл. Возвращает абсолютный путь к созданному файлу.
/// </summary>
public interface IFlaggedDialogExporter
{
    /// <param name="trigger">Запись, которая спровоцировала экспорт (содержит ChatId, FromId и т.д.).</param>
    /// <param name="chatId">ID чата, из которого собираем историю.</param>
    /// <param name="daysBack">За сколько дней назад собирать историю (например, 14).</param>
    /// <returns>Путь к JSON-файлу с диалогом.</returns>
    Task<string> ExportAsync(MessageLogRecord trigger, long chatId, int daysBack, CancellationToken ct);
}
