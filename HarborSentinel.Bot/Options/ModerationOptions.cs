using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HarborSentinel.Bot.Options
{
    public sealed class ModerationOptions
    {
        /// <summary>Включена ли модерация вообще (можно выключить на проде одним флагом).</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Базовый URL API DeepSeek (OpenAI-совместимый эндпоинт).</summary>
        public string ApiBaseUrl { get; set; } = "https://api.deepseek.com";

        /// <summary>Ключ API. Рекомендуется задавать через переменную окружения DEEPSEEK_API_KEY.</summary>
        public string? ApiKey { get; set; }

        /// <summary>Имя модели (например, deepseek-chat).</summary>
        public string Model { get; set; } = "deepseek-chat";

        /// <summary>Порог уверенности для флага нарушения (0..1).</summary>
        public double ConfidenceThreshold { get; set; } = 0.75;

        /// <summary>HTTP-таймаут запроса к модели (секунды).</summary>
        public int TimeoutSeconds { get; set; } = 15;

        /// <summary>Максимум одновременных запросов к ИИ.</summary>
        public int MaxConcurrency { get; set; } = 4;
    }
}
