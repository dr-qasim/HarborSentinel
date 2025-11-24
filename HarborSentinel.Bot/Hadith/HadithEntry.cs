using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HarborSentinel.Bot.Hadith
{
    public sealed class HadithEntry
    {
        public required string Id { get; set; }
        /// <summary>
        /// Текст хадиса на русском языке
        /// </summary>
        public required string TextRu { get; set; }
        /// <summary>
        /// Текст хадиса на арабском языке
        /// </summary>
        public string? TextAr { get; set; }
        /// <summary>
        /// Источник хадиса: сборник и номер
        /// </summary>
        public required string Source { get; set; }
        /// <summary>
        /// Краткое пояснение смысла хадиса (1-3 предложения)
        /// </summary>
        public string? ExplanationRu { get; set; }
        /// <summary>
        /// Тематические теги: "Терпение", "Намаз", "Родители", "Нрав" и т.п.
        /// </summary>
        public string[] TopicTags { get; set; } = System.Array.Empty<string>();
    }
}
