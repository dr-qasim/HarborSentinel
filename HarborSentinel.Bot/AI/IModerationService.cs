using HarborSentinel.Bot.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HarborSentinel.Bot.AI
{
    /// <summary>
    /// Контракт сервиса модерации контента. Реализация будет вызывать DeepSeek.
    /// В случае сетевой ошибки/таймаута — возвращаем AiVerdict с Flagged = null и Reason = "llm_error" (или подобным),
    /// чтобы остальная система не падала.
    /// </summary>
    public interface IModerationService
    {
        /// <summary>
        /// Классифицирует текст сообщения. Должен быть безопасным к ошибкам: не бросать исключения наружу.
        /// </summary>
        /// <param name="text">Оригинальный текст сообщения (уже очищенный от null/пустых строк).</param>
        /// <param name="ct">Токен отмены.</param>
        /// <returns>
        /// AiVerdict:
        ///   Label — метка (напр. "extremism" | "terrorism" | "narcotics" | "neutral" | "unsure"),
        ///   Confidence — 0..1,
        ///   Flagged — true/false/null (null, если модерация отключена или ошибка),
        ///   Reason — краткое пояснение/диагностика.
        /// </returns>
        Task<AiVerdict> ModerateAsync(string text, CancellationToken ct);
    }
}
