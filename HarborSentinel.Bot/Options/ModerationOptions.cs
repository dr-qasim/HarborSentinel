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

        /// <summary>Резервные модели по порядку, если основная недоступна/ошибается.</summary>
        public List<string> ModelFallbacks { get; set; } = new();

        /// <summary>Включать ли второй проход через reasoning-модель для неоднозначных и приоритетных случаев.</summary>
        public bool ReasonerEnabled { get; set; } = true;

        /// <summary>Имя reasoning-модели для второго прохода.</summary>
        public string ReasonerModel { get; set; } = "deepseek-reasoner";

        /// <summary>Таймаут reasoning-прохода в секундах.</summary>
        public int ReasonerTimeoutSeconds { get; set; } = 25;

        /// <summary>Запускать ли reasoning-проход для уже флагнутых сообщений.</summary>
        public bool ReasonerUseForFlagged { get; set; } = true;

        /// <summary>Запускать ли reasoning-проход, если первичная модель попросила дополнительный контекст.</summary>
        public bool ReasonerUseForNeedsContextReview { get; set; } = true;

        /// <summary>Запускать ли reasoning-проход для medium/high/critical риска.</summary>
        public bool ReasonerUseForMediumOrHigherRisk { get; set; } = true;

        /// <summary>Запускать ли reasoning-проход в чатах под повышенным вниманием.</summary>
        public bool ReasonerUseForHeightenedVigilance { get; set; } = true;

        /// <summary>Порог уверенности для флага нарушения (0..1).</summary>
        public double ConfidenceThreshold { get; set; } = 0.75;

        /// <summary>Включать ли контекстный анализ диалога.</summary>
        public bool ContextEnabled { get; set; } = true;

        /// <summary>Глубина контекста по чату в минутах.</summary>
        public int ChatContextWindowMinutes { get; set; } = 20;

        /// <summary>Сколько последних сообщений чата передавать модели.</summary>
        public int ChatContextMaxMessages { get; set; } = 8;

        /// <summary>Глубина контекста по текущему пользователю в минутах.</summary>
        public int UserContextWindowMinutes { get; set; } = 60;

        /// <summary>Сколько последних сообщений того же пользователя передавать модели.</summary>
        public int UserContextMaxMessages { get; set; } = 5;

        /// <summary>Окно накопительного риска по диалогу в чате.</summary>
        public int IncidentWindowMinutes { get; set; } = 30;

        /// <summary>Порог накопительного риска для отдельной эскалации по диалогу в чате.</summary>
        public double IncidentThreshold { get; set; } = 8.0;

        /// <summary>Минимальная пауза между инцидентными уведомлениями по одному чату.</summary>
        public int IncidentCooldownMinutes { get; set; } = 20;

        /// <summary>Сколько часов чат остаётся под повышенным вниманием после угрозы или инцидента.</summary>
        public int ChatVigilanceHours { get; set; } = 72;

        /// <summary>HTTP-таймаут запроса к модели (секунды).</summary>
        public int TimeoutSeconds { get; set; } = 15;

        /// <summary>Максимум одновременных запросов к ИИ.</summary>
        public int MaxConcurrency { get; set; } = 4;
    }
}
