using HarborSentinel.Bot.Models;

namespace HarborSentinel.Bot.Logging
{
    public interface IAuditSink
    {
        /// <summary>Помещает запись в очередь на сохранение (не блокирует диск).</summary>
        ValueTask EnqueueAsync(MessageLogRecord record, CancellationToken ct);
        
        /// <summary>
        /// Дожидается, пока все записи, поставленные в очередь до вызова, будут записаны на диск.
        /// Гарантирует, что экспорт сразу увидит актуальные данные.
        /// </summary>
        Task FlushAsync(CancellationToken ct);
    }
}
