using HarborSentinel.Bot.Models;
using HarborSentinel.Bot.Options;
using HarborSentinel.Bot.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Globalization;


namespace HarborSentinel.Bot.AI
{
    public sealed class DeepSeekModerationService : IModerationService
    {
        private readonly ILogger<DeepSeekModerationService> _log;
        private readonly IHttpClientFactory _httpFactory;
        private readonly ModerationOptions _opt;
        private readonly SemaphoreSlim _gate;

        private static readonly JsonSerializerOptions JsonOpt = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static readonly HashSet<string> AllowedLabels = new(StringComparer.Ordinal)
        {
            "extremism",
            "terrorism",
            "narcotics",
            "violence",
            "hate_speech",
            "weapons_illegal",
            "child_exploitation",
            "distribution_illegal_materials",
            "suicide_selfharm",
            "other_illegal",
            "pro_ukraine_advocacy",
            "neutral",
            "unsure"
        };

        private static readonly HashSet<string> AllowedTopics = new(StringComparer.Ordinal)
        {
            "religion",
            "war_conflict",
            "extremism",
            "terrorism",
            "military_info",
            "narcotics",
            "weapons",
            "hate",
            "self_harm",
            "privacy_tools",
            "other",
            "unknown"
        };

        private static readonly HashSet<string> AllowedIntents = new(StringComparer.Ordinal)
        {
            "neutral_discussion",
            "information_request",
            "instruction_request",
            "advocacy",
            "recruitment",
            "operational_planning",
            "threat",
            "unclear"
        };

        private static readonly HashSet<string> AllowedStances = new(StringComparer.Ordinal)
        {
            "support",
            "neutral_report",
            "condemnation",
            "educational_quote",
            "unclear"
        };

        private static readonly HashSet<string> AllowedRiskLevels = new(StringComparer.Ordinal)
        {
            "none",
            "low",
            "medium",
            "high",
            "critical"
        };

        private static readonly IReadOnlyDictionary<string, string> LabelAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["weapons"] = "weapons_illegal",
                ["weapons_trade"] = "weapons_illegal",
                ["weapon"] = "weapons_illegal",
                ["hate"] = "hate_speech",
                ["hate_speech_or_abuse"] = "hate_speech",
                ["self_harm"] = "suicide_selfharm",
                ["selfharm"] = "suicide_selfharm",
                ["drug"] = "narcotics",
                ["drugs"] = "narcotics",
                ["violent"] = "violence",
                ["illegal_materials"] = "distribution_illegal_materials",
                ["illegal_distribution"] = "distribution_illegal_materials",
                ["child_abuse"] = "child_exploitation",
                ["ua_advocacy"] = "pro_ukraine_advocacy",
                ["pro_ukraine"] = "pro_ukraine_advocacy"
            };

        private static readonly IReadOnlyDictionary<string, string> TopicAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["weapon"] = "weapons",
                ["weapons_illegal"] = "weapons",
                ["drug"] = "narcotics",
                ["drugs"] = "narcotics",
                ["selfharm"] = "self_harm",
                ["privacy"] = "privacy_tools",
                ["vpn"] = "privacy_tools",
                ["tor"] = "privacy_tools",
                ["military"] = "military_info",
                ["military_data"] = "military_info"
            };

        private static readonly IReadOnlyDictionary<string, string> IntentAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["neutral"] = "neutral_discussion",
                ["discussion"] = "neutral_discussion",
                ["question"] = "information_request",
                ["instructions"] = "instruction_request",
                ["instruction"] = "instruction_request",
                ["planning"] = "operational_planning",
                ["plan"] = "operational_planning"
            };

        private static readonly IReadOnlyDictionary<string, string> StanceAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["quote"] = "educational_quote",
                ["educational"] = "educational_quote",
                ["reported"] = "neutral_report",
                ["reporting"] = "neutral_report",
                ["against"] = "condemnation"
            };

        private static readonly Regex DirectThreatRegex = new(
            @"\b(уб(ь|и)ю|уб(ь|и)ем|убить|зареж\w*|расстреля\w*|сожг\w*|взорв\w*|подорв\w*|повес\w*|изоб(ь|и)ем)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex ExplosivesOrWeaponsRegex = new(
            @"\b(бомб\w*|взрывчат\w*|детонатор\w*|сву\b|самодельн\w+\s+взрыв\w*|гранат\w*|тротил\w*|пластид\w*|глушител\w*|ствол\w*|патрон\w*|оружи\w*|пистолет\w*|автомат\w*)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex WeaponsActionRegex = new(
            @"\b(где\s+купить|как\s+сделать|как\s+собрать|достать|передать|собрать|изготовить|продать|купить|перевезти|схрон|тайник)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex MilitaryReconRegex = new(
            @"\b(координат\w*|дислокаци\w*|расположени\w*|маршрут\w*|блокпост\w*|пво\b|военн\w+\s+(част\w*|объект\w*|техник\w*|колонн\w*|позици\w*|склад\w*)|склад\w+\s+боеприпас\w*)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex MilitaryReconActionRegex = new(
            @"\b(скинь|пришли|отправь|сними|сфотографир\w*|узнай|передай|собери|сведени\w*|координаты|фото|видео)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex ExtremismRecruitmentRegex = new(
            @"\b(вербуем|вербов\w*|вступай|присоединяйся|байат\w*|теракт\w*|джихад\w*|шахид\w*)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex NarcoticsRegex = new(
            @"\b(закладк\w*|кладмен\w*|меф\w*|героин\w*|амфетамин\w*|соль\b|кокаин\w*|фасовк\w*|клад\b|адрес\s+клада)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex ProUkraineAdvocacyRegex = new(
            @"\b(донат\w*|сбор\w*|помог\w*|поддерж\w*).{0,40}\b(всу|украин\w+\s+арм\w*|украин\w+\s+сторон\w*)\b|\b(слава\s+украин\w*|поможем\s+всу)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex SecrecyOrReconPreparationRegex = new(
            @"\b(шифр\w*|шифрова\w*|зашифр\w*|encryption\b|encrypt\w*|gpg\b|pgp\b|tor\b|vpn\b|proxy\b|прокс\w*|аноним\w*|osint\b|разведдан\w*|метаданн\w*|сбор\s+информац\w*|поиск\s+информац\w*|алгоритм\w+\s+поиск\w*|скан\w*|утечк\w*|база\s+данн\w*)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex FundraisingOrRequisitesRegex = new(
            @"\b(сбор\w*|донат\w*|пожертв\w*|реквизит\w*|оплат\w*|перевод\w*|заключенн\w*|долг\w*|адвокат\w*)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex TechnicalOpSecRegex = new(
            @"\b(sni\b|домен\w*\s+sni|мультихоп\w*|виртуалк\w*|айпишник\w*|ip\b|vpn\b|tor\b|proxy\b|прокс\w*|лев\w+\s+номер\w*|симк\w*|вайфай\w*|безопасн\w+\s+оплат\w*|подстав\w+\s+себя)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex CoordinationOrRecruitmentRegex = new(
            @"\b(брат(ья)?\b.*(зайд\w*|добав\w*|пишет|вступ\w*)|админ\w*|канал\w*|связ\w*|инструкц\w*|методичк\w*|скинь\w*|пришли\w*|добав\w+\s+сюда)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex IdentityCollectionRegex = new(
            @"\b(фио\b|номер\w*\s+скинь\w*|логи?\s+надо\s+состав\w*|каждый\s+сво[её]\s+фио)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex InstructionalLanguageRegex = new(
            @"\b(пошагов\w*|делаешь\s+по|меняешь\s+домен|инструкц\w*|как\s+сделать|как\s+настроить|как\s+брать|мануал\w*|обучени\w*|скину|сейчас\s+скину|углублен\w*|поверхностн\w*|переименуй\w*|сделал\s+нов\w*)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex FinancialObfuscationRegex = new(
            @"\b(монеро|monero|крипт\w*|обменник\w*|битк\w*|кошел[её]к\w*|wallet\b)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex MaterialDistributionRegex = new(
            @"(@[a-z0-9_]+\.pdf\b|\b[a-z0-9_]*jihad[a-z0-9_]*\.pdf\b|\b[a-z0-9_]*djihad[a-z0-9_]*\.pdf\b|\b(файл|pdf|архив|документ|тгк|канал)\b|\bприватн\w*\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex ExtremistMaterialMarkerRegex = new(
            @"\b(джихад\w*|jihad\w*|djihad\w*|legion[_-]?jihad|legion[_-]?djihad|легион)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex WeakFollowUpRegex = new(
            @"^\s*(да|да\?|знаю|интересно|знакомо|щас|готово|ашалеть|амин|нет\s+нет|ты\s+его\s+знаешь\?|давай\s+брат|очень\s+помог\s+бы)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private const string SystemPrompt =
            "Ты — строгий многоязычный модератор контента для чат-бота. " +
            "Твоя задача — оценивать сообщения и диалогический контекст на предмет нарушений законодательства РФ и рисков общественной безопасности. " +
            "Текущее сообщение всегда оценивай вместе с переданным контекстом, если он есть. " +
            "Не делай вывод только по одному неоднозначному слову без опоры на соседние реплики и намерение автора. " +
            "Особенно важно выявлять опасный диалог в сумме: повторяющиеся призывы, просьбы об инструкциях, координацию действий, вербовку, сбор сведений, агитацию и переход от обсуждения к планированию. " +
            "Если в payload указано chat_attention.heightened_vigilance=true, значит в этом чате уже были признаки угрозы или опасного эпизода. " +
            "В таком режиме анализируй новые сообщения с повышенной чувствительностью к follow-up обсуждению: технической подготовке, шифрованию, приватным каналам связи, поиску и сбору значимой информации, логистике, координации ролей, обмену инструкциями и данным, даже если каждая реплика по отдельности выглядит мягко или частично нейтрально. " +
            "При heightened_vigilance не требуй буквальных угроз, если по сумме есть признаки продолжения ранее выявленного опасного разговора; в сомнительных случаях предпочитай risk_level не ниже medium и needs_context_review=true. " +
            "Строго различай: поддержка/агитация, нейтральный пересказ новостей, осуждение, богословскую цитату, учебное цитирование и просьбу о помощи для предотвращения угрозы. " +
            "Если сообщение цитирует опасный контент с целью осудить его, предупредить, разобрать или сообщить о нём, не помечай это как нарушение без дополнительных признаков одобрения, инструкций или координации. " +
            "Если же контекст показывает одобрение, вербовку, практические инструкции, поиск ресурсов, сбор средств, передачу сведений, рекрутинг или координацию действий, не возвращай 'neutral' только из-за мягкой формулировки. " +
            "Особенно внимательно отслеживай: " +
            "1) пропаганду, оправдание или поддержку экстремизма и терроризма; " +
            "2) попытки вербовки, склонения к участию в незаконной деятельности, в том числе через «дружеское» общение или религиозную риторику; " +
            "3) призывы к сотрудничеству с иностранными спецслужбами/разведкой, шпионажу, сбору и передаче секретной или военной информации; " +
            "4) просьбы сфотографировать или снять на видео военные объекты, технику, блокпосты, позиции войск, стратегическую инфраструктуру; " +
            "5) призывы к сепаратизму, подрыву территориальной целостности, поддержке незаконных вооружённых формирований; " +
            "6) подстрекательство к насилию, межнациональной или межрелигиозной вражде, унижение по признаку религии, в том числе в отношении мусульман; " +
            "7) проукраинскую агитацию в контексте вооружённого конфликта против РФ: одобрение и поддержка враждебных действий против РФ, сбор средств/ресурсов в пользу противостоящей стороны, публичные призывы к содействию такой деятельности. " +
            "При этом НЕ считай нарушением: мирное обсуждение религии (в том числе ислама), цитаты из Корана и Сунны, хадисы, фетвы и богословские мнения, " +
            "если в них нет прямых призывов к незаконным действиям, насилию, дискриминации или разжиганию ненависти. " +
            "Также не помечай как нарушение нейтральное обсуждение новостей и фактов без признаков одобрения, призывов и агитации. " +
            "Классифицируй сообщение РОВНО в один ярлык из списка: " +
            "['extremism','terrorism','narcotics','violence','hate_speech','weapons_illegal'," +
            "'child_exploitation','distribution_illegal_materials','suicide_selfharm','other_illegal'," +
            "'pro_ukraine_advocacy','neutral','unsure']. " +
            "Используй ярлыки так: " +
            "- 'extremism' — пропаганда или оправдание экстремистских идей, призывы к нарушению конституционного строя, сепаратизм, поддержка запрещённых организаций; " +
            "- 'terrorism' — оправдание терроризма, угрозы или призывы к терактам, вербовка для террористической деятельности; " +
            "- 'narcotics' — пропаганда, реклама или инструкции по обороту наркотиков; " +
            "- 'violence' — прямые угрозы, призывы к побоям, убийствам, тяжкому вреду, не достигающие уровня терроризма/экстремизма; " +
            "- 'hate_speech' — оскорбления и враждебные высказывания в отношении групп по национальности, религии, расе, этничности и т.п.; " +
            "- 'weapons_illegal' — незаконный оборот оружия, взрывчатки, взрывных устройств; " +
            "- 'child_exploitation' — любые намёки на сексуальную эксплуатацию или насилие над детьми; " +
            "- 'distribution_illegal_materials' — распространение заведомо запрещённых материалов (например, экстремистских, террористических), если это не подпадает под другие ярлыки; " +
            "- 'suicide_selfharm' — призывы, подстрекательство или поощрение суицида и самоповреждения; " +
            "- 'other_illegal' — иные явно незаконные действия (мошенничество, шпионаж, помощь иностранным спецслужбам, сбор военной информации, подстрекательство к совершению преступлений), не подходящие под предыдущие категории; " +
            "- 'pro_ukraine_advocacy' — поддержка, агитация или призывы к содействию действиям проукраинской стороны в контексте конфликта против РФ; " +
            "- 'neutral' — законопослушный, безопасный контент без признаков нарушений; " +
            "- 'unsure' — когда информации недостаточно, контекст непонятен или невозможно надёжно определить. " +
            "Дополнительно определи тему сообщения/диалога: " +
            "['religion','war_conflict','extremism','terrorism','military_info','narcotics','weapons','hate','self_harm','privacy_tools','other','unknown'] " +
            "и намерение автора: " +
            "['neutral_discussion','information_request','instruction_request','advocacy','recruitment','operational_planning','threat','unclear']. " +
            "Отдельно определи stance: ['support','neutral_report','condemnation','educational_quote','unclear'] " +
            "и общий risk_level: ['none','low','medium','high','critical']. " +
            "Если сообщение само по себе неоднозначно, но контекст повышает риск, пометь needs_context_review=true. " +
            "Если по совокупности видно движение к незаконной деятельности, это важнее, чем мягкая формулировка отдельной реплики. " +
            "Не опирайся на приветствия, бытовую вежливость, отдельные короткие реплики или слабые мета-вопросы, если в том же контексте есть более сильные операционные маркеры. " +
            "В reasons ссылайся на самые сильные и предметные фрагменты: инструкции, координацию, сбор идентификаторов, реквизиты, сокрытие, безопасную связь, логистику. " +
            "Короткие follow-up реплики вроде 'да', 'знаю', 'интересно', 'ты его знаешь?' не должны автоматически получать тяжёлую метку только из-за старого контекста; " +
            "если в самой реплике нет нового сильного сигнала, отражай риск через needs_context_review и не завышай confidence. " +
            "Поле reasons обязано быть конкретным: перечисли 2-4 ключевых маркера из диалога (например, сборы/реквизиты, инструкции, координация, сбор ФИО/номеров, сокрытие следов), избегай расплывчатых формулировок. " +
            "Сообщения о шпионаже, сборе военной информации, подрыве территориальной целостности, вербовке в пользу запрещённых структур " +
            "обычно относить к 'extremism', 'terrorism' или 'other_illegal' в зависимости от контекста и степени прямоты призывов; " +
            "проукраинскую агитацию в контексте конфликта относить к 'pro_ukraine_advocacy'. " +
            "Если видишь вероятно релевантные статьи УК РФ, верни их как список строк, но не делай категорических юридических выводов. " +
            "Верни ТОЛЬКО JSON в ОДНУ СТРОКУ в формате: " +
            "{\"label\":string, \"confidence\":number, \"reasons\":string, \"topic\":string, \"intent\":string, \"stance\":string, \"risk_level\":string, \"needs_context_review\":boolean, \"uk_article_candidates\":string[]}. " +
            "label — один из указанных ярлыков; confidence — число от 0 до 1 (чем выше, тем увереннее); reasons — короткое текстовое объяснение на русском; " +
            "topic, intent, stance и risk_level — только из заданных списков; needs_context_review — true или false; uk_article_candidates — массив строк, можно пустой. " +
            "Не добавляй переносов строк и не пиши ничего, кроме одного JSON-объекта.";

        public DeepSeekModerationService(
            ILogger<DeepSeekModerationService> log,
            IHttpClientFactory httpFactory,
            IOptions<ModerationOptions> opt)
        {
            _log = log;
            _httpFactory = httpFactory;
            _opt = opt.Value;
            if (string.IsNullOrWhiteSpace(_opt.ApiKey))
                _opt.ApiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY"); // запасной вариант
            _gate = new SemaphoreSlim(Math.Max(1, _opt.MaxConcurrency));
        }
        private string? ResolveApiKey()
        {
            if (!string.IsNullOrWhiteSpace(_opt.ApiKey))
                return _opt.ApiKey;

            var fromOptionsEnv = Environment.GetEnvironmentVariable("Moderation__ApiKey");
            if (!string.IsNullOrWhiteSpace(fromOptionsEnv))
                return fromOptionsEnv;

            var fromLegacyEnv = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
            return string.IsNullOrWhiteSpace(fromLegacyEnv) ? null : fromLegacyEnv;
        }

        public Task<AiVerdict> ModerateAsync(string text, CancellationToken ct)
        {
            return ModerateAsync(
                new ModerationRequest
                {
                    Text = text,
                    RecentChatMessages = Array.Empty<ModerationContextMessage>(),
                    RecentUserMessages = Array.Empty<ModerationContextMessage>()
                },
                ct);
        }

        public async Task<AiVerdict> ModerateAsync(ModerationRequest request, CancellationToken ct)
        {
            if (!_opt.Enabled)
                return new AiVerdict { Label = "disabled", Confidence = 0, Flagged = null, Reason = "moderation disabled" };
            
            var apiKey = ResolveApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _log.LogWarning("DeepSeek API key not configured (checked Moderation:ApiKey, env Moderation__ApiKey, env DEEPSEEK_API_KEY); skipping moderation");
                return BuildFailureVerdict("no_api_key", request);
            }

            var sanitizedRequest = Sanitize(request);
            var reasonerModel = ResolveReasonerModel();
            var primaryCandidates = ResolvePrimaryModelCandidates(reasonerModel);

            await _gate.WaitAsync(ct);
            try
            {
                var client = _httpFactory.CreateClient("moderation");
                string? lastFailureReason = null;
                AiVerdict? primaryVerdict = null;
                var reasonerAttempted = false;
                var attemptedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var model in primaryCandidates)
                {
                    attemptedModels.Add(model);
                    var outcome = await TryModerateWithModelAsync(client, apiKey!, model, sanitizedRequest, ct).ConfigureAwait(false);
                    if (outcome.Verdict is not null)
                    {
                        primaryVerdict = outcome.Verdict;
                        break;
                    }

                    lastFailureReason = outcome.FailureReason ?? "model_failed";
                    _log.LogWarning(
                        "Moderation model {Model} failed ({Reason}); trying next model if available",
                        model,
                        lastFailureReason);
                }

                if (primaryVerdict is null && !string.IsNullOrWhiteSpace(reasonerModel) && !_opt.ReasonerEnabled)
                    reasonerModel = null;

                if (primaryVerdict is null &&
                    !string.IsNullOrWhiteSpace(reasonerModel) &&
                    !attemptedModels.Contains(reasonerModel))
                {
                    reasonerAttempted = true;
                    var reasonerOutcome = await TryModerateWithModelAsync(
                            client,
                            apiKey!,
                            reasonerModel!,
                            PromoteForHighSensitivityReview(sanitizedRequest),
                            ct)
                        .ConfigureAwait(false);

                    if (reasonerOutcome.Verdict is not null)
                    {
                        _log.LogInformation(
                            "Reasoner recovered moderation after primary failure for chat {ChatId} msg {MessageId}",
                            request.ChatId,
                            request.MessageId);
                        return reasonerOutcome.Verdict;
                    }

                    lastFailureReason = reasonerOutcome.FailureReason ?? lastFailureReason ?? "reasoner_failed";
                    _log.LogWarning(
                        "Reasoner model {Model} failed after primary failure ({Reason})",
                        reasonerModel,
                        lastFailureReason);
                }

                if (primaryVerdict is null)
                    return BuildFailureVerdict(lastFailureReason ?? "all_models_failed", request);

                if (_opt.ReasonerEnabled &&
                    !string.IsNullOrWhiteSpace(reasonerModel) &&
                    !reasonerAttempted &&
                    !attemptedModels.Contains(reasonerModel) &&
                    ShouldRunReasonerSecondPass(sanitizedRequest, primaryVerdict))
                {
                    var reasonerOutcome = await TryModerateWithModelAsync(
                            client,
                            apiKey!,
                            reasonerModel!,
                            PromoteForHighSensitivityReview(sanitizedRequest),
                            ct)
                        .ConfigureAwait(false);

                    if (reasonerOutcome.Verdict is not null)
                    {
                        var mergedVerdict = MergeVerdictsConservatively(
                            primaryVerdict,
                            reasonerOutcome.Verdict,
                            sanitizedRequest.ChatUnderHeightenedVigilance);

                        _log.LogInformation(
                            "Reasoner second pass applied for chat {ChatId} msg {MessageId}; primary={PrimaryLabel}/{PrimaryRisk}; final={FinalLabel}/{FinalRisk}; heightened={Heightened}",
                            request.ChatId,
                            request.MessageId,
                            primaryVerdict.Label,
                            primaryVerdict.RiskLevel,
                            mergedVerdict.Label,
                            mergedVerdict.RiskLevel,
                            sanitizedRequest.ChatUnderHeightenedVigilance);

                        return mergedVerdict;
                    }

                    _log.LogWarning(
                        "Reasoner second pass failed for chat {ChatId} msg {MessageId}: {Reason}. Keeping primary verdict.",
                        request.ChatId,
                        request.MessageId,
                        reasonerOutcome.FailureReason ?? "reasoner_failed");
                }

                return primaryVerdict;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "LLM error");
                return BuildFailureVerdict("llm_error", request);
            }
            finally
            {
                _gate.Release();
            }
        }

        private IReadOnlyList<string> ResolvePrimaryModelCandidates(string? reasonerModel)
        {
            var list = new List<string>();
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            static void TryAddModel(List<string> target, HashSet<string> seen, string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return;

                var model = value.Trim();
                if (seen.Add(model))
                    target.Add(model);
            }

            if (IsReasonerModel(_opt.Model))
            {
                TryAddModel(list, unique, _opt.Model);
                if (_opt.ModelFallbacks is not null)
                {
                    foreach (var fallback in _opt.ModelFallbacks)
                        TryAddModel(list, unique, fallback);
                }
            }
            else
            {
                TryAddModel(list, unique, _opt.Model);
                if (_opt.ModelFallbacks is not null)
                {
                    foreach (var fallback in _opt.ModelFallbacks)
                    {
                        if (_opt.ReasonerEnabled &&
                            IsReasonerModel(fallback) &&
                            !string.Equals(fallback, _opt.Model, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        TryAddModel(list, unique, fallback);
                    }
                }
            }

            if (list.Count == 0 && !_opt.ReasonerEnabled)
                list.Add("deepseek-chat");

            if (list.Count == 0 && string.IsNullOrWhiteSpace(reasonerModel))
                list.Add("deepseek-chat");

            return list;
        }

        private string? ResolveReasonerModel()
        {
            if (!_opt.ReasonerEnabled)
                return null;

            if (!string.IsNullOrWhiteSpace(_opt.ReasonerModel))
                return _opt.ReasonerModel.Trim();

            if (IsReasonerModel(_opt.Model))
                return _opt.Model.Trim();

            if (_opt.ModelFallbacks is not null)
            {
                foreach (var fallback in _opt.ModelFallbacks)
                {
                    if (IsReasonerModel(fallback))
                        return fallback.Trim();
                }
            }

            return "deepseek-reasoner";
        }

        private static bool IsReasonerModel(string? model)
        {
            return !string.IsNullOrWhiteSpace(model)
                && model.Contains("reasoner", StringComparison.OrdinalIgnoreCase);
        }

        private bool ShouldRunReasonerSecondPass(ModerationRequest request, AiVerdict verdict)
        {
            if (!_opt.ReasonerEnabled)
                return false;

            if (request.ChatUnderHeightenedVigilance && _opt.ReasonerUseForHeightenedVigilance)
                return true;

            if (verdict.Flagged == true && _opt.ReasonerUseForFlagged)
                return true;

            if (verdict.NeedsContextReview == true && _opt.ReasonerUseForNeedsContextReview)
                return true;

            if (_opt.ReasonerUseForMediumOrHigherRisk &&
                verdict.RiskLevel is "medium" or "high" or "critical")
            {
                return true;
            }

            if (verdict.Intent is "instruction_request" or "advocacy" or "recruitment" or "operational_planning" or "threat")
                return true;

            if (verdict.Label is "neutral" or "unsure")
            {
                var profile = BuildSignalProfile(request);
                if (profile.RequiresReasonerReview)
                    return true;

                return verdict.Topic is "privacy_tools" or "military_info" or "war_conflict" or "weapons" or "narcotics" or "extremism" or "terrorism" or "other";
            }

            if (BuildSignalProfile(request).RequiresReasonerReview)
                return true;

            return false;
        }

        private static ModerationRequest PromoteForHighSensitivityReview(ModerationRequest request)
        {
            return new ModerationRequest
            {
                Text = request.Text,
                ChatId = request.ChatId,
                ChatTitle = request.ChatTitle,
                UserId = request.UserId,
                Username = request.Username,
                MessageId = request.MessageId,
                ReplyToText = request.ReplyToText,
                ChatUnderHeightenedVigilance = request.ChatUnderHeightenedVigilance,
                ChatVigilanceUntilUtc = request.ChatVigilanceUntilUtc,
                ChatVigilanceTriggerCount = request.ChatVigilanceTriggerCount,
                ChatVigilanceLabel = request.ChatVigilanceLabel,
                ChatVigilanceTopic = request.ChatVigilanceTopic,
                ChatVigilanceRiskLevel = request.ChatVigilanceRiskLevel,
                ChatVigilanceReason = request.ChatVigilanceReason,
                HighSensitivityReview = true,
                RecentChatMessages = request.RecentChatMessages,
                RecentUserMessages = request.RecentUserMessages
            };
        }

        private static AiVerdict MergeVerdictsConservatively(
            AiVerdict primaryVerdict,
            AiVerdict reasonerVerdict,
            bool heightenedVigilance)
        {
            AiVerdict chosen;

            if (primaryVerdict.Flagged == true && reasonerVerdict.Flagged != true)
            {
                chosen = primaryVerdict;
            }
            else if (reasonerVerdict.Flagged == true && primaryVerdict.Flagged != true)
            {
                chosen = reasonerVerdict;
            }
            else if (primaryVerdict.Flagged == true && reasonerVerdict.Flagged == true)
            {
                chosen = ChooseMoreSevere(primaryVerdict, reasonerVerdict);
            }
            else if (heightenedVigilance)
            {
                chosen = ChooseMoreSevere(primaryVerdict, reasonerVerdict);
            }
            else if (primaryVerdict.Label is "neutral" or "unsure")
            {
                chosen = ChooseMoreSevere(primaryVerdict, reasonerVerdict);
            }
            else
            {
                chosen = primaryVerdict;
            }

            var secondary = ReferenceEquals(chosen, primaryVerdict) ? reasonerVerdict : primaryVerdict;
            return MergeVerdictMetadata(chosen, secondary, heightenedVigilance);
        }

        private static AiVerdict ChooseMoreSevere(AiVerdict first, AiVerdict second)
        {
            var firstSeverity = GetVerdictSeverity(first);
            var secondSeverity = GetVerdictSeverity(second);

            if (secondSeverity > firstSeverity)
                return second;

            if (firstSeverity > secondSeverity)
                return first;

            return (second.Confidence ?? 0d) > (first.Confidence ?? 0d) ? second : first;
        }

        private static double GetVerdictSeverity(AiVerdict verdict)
        {
            var severity = (verdict.Flagged == true ? 10d : 0d) + (verdict.Confidence ?? 0d);
            severity += verdict.RiskLevel switch
            {
                "critical" => 6d,
                "high" => 4d,
                "medium" => 2d,
                "low" => 1d,
                _ => 0d
            };
            severity += verdict.Intent switch
            {
                "threat" or "recruitment" or "operational_planning" => 3d,
                "advocacy" or "instruction_request" => 2d,
                _ => 0d
            };
            severity += verdict.Stance == "support" ? 1.5d : 0d;
            severity += verdict.NeedsContextReview == true ? 0.75d : 0d;
            return severity;
        }

        private static AiVerdict MergeVerdictMetadata(AiVerdict preferred, AiVerdict secondary, bool heightenedVigilance)
        {
            var articles = NormalizeUkArticleCandidates(
                (preferred.UkArticleCandidates ?? Array.Empty<string>())
                    .Concat(secondary.UkArticleCandidates ?? Array.Empty<string>())
                    .ToArray());

            var needsContextReview = preferred.NeedsContextReview == true ||
                                     secondary.NeedsContextReview == true ||
                                     heightenedVigilance;

            var riskLevel = preferred.RiskLevel;
            if (heightenedVigilance && riskLevel is "none" or "low")
                riskLevel = needsContextReview ? "medium" : "low";

            return new AiVerdict
            {
                Label = preferred.Label,
                Confidence = preferred.Confidence,
                Flagged = preferred.Flagged,
                Reason = preferred.Reason,
                Topic = preferred.Topic,
                Intent = preferred.Intent,
                Stance = preferred.Stance,
                RiskLevel = riskLevel,
                NeedsContextReview = needsContextReview,
                UkArticleCandidates = articles,
                IncidentScore = preferred.IncidentScore ?? secondary.IncidentScore,
                IncidentEventCount = preferred.IncidentEventCount ?? secondary.IncidentEventCount,
                IsModelFailureFallback = preferred.IsModelFailureFallback && secondary.IsModelFailureFallback
            };
        }

        private async Task<ModerationAttemptOutcome> TryModerateWithModelAsync(
            HttpClient client,
            string apiKey,
            string model,
            ModerationRequest request,
            CancellationToken ct)
        {
            var isReasoner = IsReasonerModel(model);
            var chatRequest = new ChatCompletionsRequest
            {
                Model = model,
                Messages =
                [
                    new ChatMessage("system", SystemPrompt),
                    new ChatMessage("user", BuildUserPrompt(request))
                ],
                ResponseFormat = isReasoner ? null : new ResponseFormat { Type = "json_object" },
                Temperature = isReasoner ? null : 0,
                MaxTokens = isReasoner ? 320 : 220
            };

            using var http = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(chatRequest, JsonOpt), Encoding.UTF8, "application/json")
            };

            http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var modelTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timeoutSeconds = isReasoner ? Math.Max(5, _opt.ReasonerTimeoutSeconds) : Math.Max(5, _opt.TimeoutSeconds);
            modelTimeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            HttpResponseMessage resp;
            string body;
            try
            {
                resp = await client.SendAsync(http, modelTimeoutCts.Token).ConfigureAwait(false);
                body = await resp.Content.ReadAsStringAsync(modelTimeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning("Moderation model {Model} timed out after {TimeoutSeconds}s", model, timeoutSeconds);
                return new ModerationAttemptOutcome(Verdict: null, FailureReason: "timeout");
            }
            catch (HttpRequestException ex)
            {
                _log.LogWarning(ex, "HTTP request failed for moderation model {Model}", model);
                return new ModerationAttemptOutcome(Verdict: null, FailureReason: "http_request_error");
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    _log.LogWarning("LLM HTTP {Status} for model {Model}: {Body}", (int)resp.StatusCode, model, body);
                    return new ModerationAttemptOutcome(Verdict: null, FailureReason: $"http_{(int)resp.StatusCode}");
                }

                ChatCompletionsResponse? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<ChatCompletionsResponse>(body, JsonOpt);
                }
                catch (JsonException ex)
                {
                    _log.LogWarning(ex, "Failed to parse chat completion envelope for model {Model}", model);
                    return new ModerationAttemptOutcome(Verdict: null, FailureReason: "invalid_response_json");
                }

                var message = parsed?.Choices?.FirstOrDefault()?.Message;
                var content = message?.Content;
                if (string.IsNullOrWhiteSpace(content))
                {
                    _log.LogWarning(
                        "Moderation model {Model} returned empty content (reasoning length={ReasoningLength})",
                        model,
                        message?.ReasoningContent?.Length ?? 0);
                    return new ModerationAttemptOutcome(Verdict: null, FailureReason: "empty_content");
                }

                var cls = TryParseClassification(content);
                if (cls is null)
                    return new ModerationAttemptOutcome(Verdict: null, FailureReason: "parse_failed");

                var rawLabel = cls.Label;
                var normalizedRawLabel = NormalizeToken(cls.Label, AllowedLabels, LabelAliases, fallback: null);
                var isKnownLabel = normalizedRawLabel is not null && AllowedLabels.Contains(normalizedRawLabel);

                if (!isKnownLabel)
                {
                    _log.LogWarning("LLM returned unsupported label '{Label}' for model {Model}", rawLabel, model);
                    return new ModerationAttemptOutcome(Verdict: null, FailureReason: $"unknown_label={rawLabel}");
                }

                var label = normalizedRawLabel!;
                var confidence = NormalizeConfidence(cls.Confidence);
                var rawReason = string.IsNullOrWhiteSpace(cls.Reasons)
                    ? $"model={model}; label={label}, confidence={confidence:F2}"
                    : cls.Reasons;
                var reason = TextSanitizer.NormalizeReasonText(rawReason, maxLen: 360);
                var topic = NormalizeToken(cls.Topic, AllowedTopics, TopicAliases, fallback: "unknown") ?? "unknown";
                var intent = NormalizeToken(cls.Intent, AllowedIntents, IntentAliases, fallback: "unclear") ?? "unclear";
                var stance = NormalizeToken(cls.Stance, AllowedStances, StanceAliases, fallback: "unclear") ?? "unclear";
                var needsContextReview = cls.NeedsContextReview;
                var riskLevel = ResolveRiskLevel(cls.RiskLevel, label, confidence, needsContextReview, request);
                var ukArticleCandidates = NormalizeUkArticleCandidates(cls.UkArticleCandidates);
                var flagged = ShouldFlag(label, confidence, intent, stance, riskLevel, needsContextReview, ukArticleCandidates, request);
                var rawVerdict = new AiVerdict
                {
                    Label = label,
                    Confidence = confidence,
                    Flagged = flagged,
                    Reason = reason,
                    Topic = topic,
                    Intent = intent,
                    Stance = stance,
                    RiskLevel = riskLevel,
                    NeedsContextReview = needsContextReview,
                    UkArticleCandidates = ukArticleCandidates,
                    IsModelFailureFallback = false
                };
                var refinedVerdict = RefineVerdictWithSignals(rawVerdict, request);

                return new ModerationAttemptOutcome(
                    Verdict: refinedVerdict,
                    FailureReason: null);
            }
        }

        private AiVerdict BuildFailureVerdict(string failureReason, ModerationRequest request)
        {
            var fallback = AssessFailureFallback(request);
            var flagged = fallback.ImmediateFlag;

            _log.LogWarning(
                "Using local moderation fallback due to {Reason}; label={Label}; risk={RiskLevel}; flagged={Flagged}",
                failureReason,
                fallback.Label,
                fallback.RiskLevel,
                flagged);

            var verdict = new AiVerdict
            {
                Label = fallback.Label,
                Confidence = fallback.Confidence,
                Flagged = flagged,
                Reason = $"{failureReason}; {fallback.Reason}",
                Topic = fallback.Topic,
                Intent = fallback.Intent,
                Stance = fallback.Stance,
                RiskLevel = fallback.RiskLevel,
                NeedsContextReview = true,
                UkArticleCandidates = fallback.UkArticleCandidates,
                IsModelFailureFallback = true
            };
            return RefineVerdictWithSignals(verdict, request);
        }

        private static FailureFallbackAssessment AssessFailureFallback(ModerationRequest request)
        {
            var text = BuildFallbackText(request);
            if (string.IsNullOrWhiteSpace(text))
            {
                return new FailureFallbackAssessment(
                    Label: "unsure",
                    Topic: "unknown",
                    Intent: "unclear",
                    Stance: "unclear",
                    RiskLevel: "low",
                    Confidence: 0,
                    ImmediateFlag: false,
                    Reason: "контекст недоступен, требуется ручной просмотр",
                    UkArticleCandidates: Array.Empty<string>());
            }

            if (DirectThreatRegex.IsMatch(text))
            {
                return new FailureFallbackAssessment(
                    Label: "violence",
                    Topic: "other",
                    Intent: "threat",
                    Stance: "support",
                    RiskLevel: "critical",
                    Confidence: 0.62d,
                    ImmediateFlag: true,
                    Reason: "локальный fallback зафиксировал прямую угрозу или призыв к насилию",
                    UkArticleCandidates: ["ст. 119 УК РФ", "ст. 280 УК РФ"]);
            }

            if (MilitaryReconRegex.IsMatch(text) && MilitaryReconActionRegex.IsMatch(text))
            {
                return new FailureFallbackAssessment(
                    Label: "other_illegal",
                    Topic: "military_info",
                    Intent: "operational_planning",
                    Stance: "support",
                    RiskLevel: "high",
                    Confidence: 0.52d,
                    ImmediateFlag: false,
                    Reason: "локальный fallback зафиксировал запрос или передачу военных сведений",
                    UkArticleCandidates: ["ст. 275 УК РФ", "ст. 276 УК РФ", "ст. 283 УК РФ"]);
            }

            if (ExplosivesOrWeaponsRegex.IsMatch(text) && WeaponsActionRegex.IsMatch(text))
            {
                return new FailureFallbackAssessment(
                    Label: "weapons_illegal",
                    Topic: "weapons",
                    Intent: "instruction_request",
                    Stance: "support",
                    RiskLevel: "high",
                    Confidence: 0.50d,
                    ImmediateFlag: false,
                    Reason: "локальный fallback зафиксировал обсуждение оружия или взрывчатки с практическим действием",
                    UkArticleCandidates: ["ст. 222 УК РФ", "ст. 223.1 УК РФ"]);
            }

            if (ExtremismRecruitmentRegex.IsMatch(text))
            {
                return new FailureFallbackAssessment(
                    Label: "extremism",
                    Topic: "extremism",
                    Intent: "recruitment",
                    Stance: "support",
                    RiskLevel: "high",
                    Confidence: 0.48d,
                    ImmediateFlag: false,
                    Reason: "локальный fallback зафиксировал признаки вербовки или оправдания насильственной деятельности",
                    UkArticleCandidates: ["ст. 205.1 УК РФ", "ст. 280 УК РФ", "ст. 282.2 УК РФ"]);
            }

            if (NarcoticsRegex.IsMatch(text))
            {
                return new FailureFallbackAssessment(
                    Label: "narcotics",
                    Topic: "narcotics",
                    Intent: "instruction_request",
                    Stance: "support",
                    RiskLevel: "medium",
                    Confidence: 0.42d,
                    ImmediateFlag: false,
                    Reason: "локальный fallback зафиксировал признаки оборота или сбыта наркотиков",
                    UkArticleCandidates: ["ст. 228.1 УК РФ", "ст. 230 УК РФ"]);
            }

            if (ProUkraineAdvocacyRegex.IsMatch(text))
            {
                return new FailureFallbackAssessment(
                    Label: "pro_ukraine_advocacy",
                    Topic: "war_conflict",
                    Intent: "advocacy",
                    Stance: "support",
                    RiskLevel: "medium",
                    Confidence: 0.40d,
                    ImmediateFlag: false,
                    Reason: "локальный fallback зафиксировал агитацию или поддержку действий в пользу проукраинской стороны",
                    UkArticleCandidates: ["ст. 280.3 УК РФ", "ст. 205.1 УК РФ"]);
            }

            if (request.ChatUnderHeightenedVigilance && SecrecyOrReconPreparationRegex.IsMatch(text))
            {
                return new FailureFallbackAssessment(
                    Label: "other_illegal",
                    Topic: "privacy_tools",
                    Intent: "information_request",
                    Stance: "unclear",
                    RiskLevel: "medium",
                    Confidence: 0.34d,
                    ImmediateFlag: false,
                    Reason: "чат под повышенным вниманием, локальный fallback зафиксировал техническую подготовку, сокрытие или сбор значимой информации",
                    UkArticleCandidates: ["ст. 272 УК РФ", "ст. 273 УК РФ", "ст. 275 УК РФ"]);
            }

            return new FailureFallbackAssessment(
                Label: "unsure",
                Topic: "unknown",
                Intent: "unclear",
                Stance: "unclear",
                RiskLevel: "low",
                Confidence: 0,
                ImmediateFlag: false,
                Reason: "LLM недоступен, требуется ручной просмотр",
                UkArticleCandidates: Array.Empty<string>());
        }

        private static string BuildFallbackText(ModerationRequest request)
        {
            var parts = new List<string>(capacity: 1 + request.RecentChatMessages.Count + request.RecentUserMessages.Count);

            if (!string.IsNullOrWhiteSpace(request.Text))
                parts.Add(request.Text);

            if (!string.IsNullOrWhiteSpace(request.ReplyToText))
                parts.Add(request.ReplyToText);

            foreach (var item in request.RecentChatMessages)
            {
                if (!string.IsNullOrWhiteSpace(item.Text))
                    parts.Add(item.Text);
            }

            foreach (var item in request.RecentUserMessages)
            {
                if (!string.IsNullOrWhiteSpace(item.Text))
                    parts.Add(item.Text);
            }

            return string.Join('\n', parts);
        }

        private AiVerdict RefineVerdictWithSignals(AiVerdict verdict, ModerationRequest request)
        {
            var profile = BuildSignalProfile(request);
            if (!profile.HasSignals)
                return verdict;

            var label = verdict.Label ?? "unsure";
            var confidence = Math.Clamp(verdict.Confidence ?? 0d, 0d, 1d);
            var topic = verdict.Topic ?? "unknown";
            var intent = verdict.Intent ?? "unclear";
            var stance = verdict.Stance ?? "unclear";
            var riskLevel = verdict.RiskLevel ?? "low";
            var needsContext = verdict.NeedsContextReview == true;
            var flagged = verdict.Flagged == true;
            var weakFollowUpOnly =
                profile.CurrentMessageLooksWeakFollowUp &&
                !profile.CurrentMessageHasStrongSignals &&
                profile.ContextWeightedScore >= 3d;

            if (weakFollowUpOnly)
            {
                label = "unsure";
                intent = request.Text.TrimEnd().EndsWith("?", StringComparison.Ordinal) ? "information_request" : "unclear";
                stance = ResolveWeakFollowUpStance(stance);
                riskLevel = request.ChatUnderHeightenedVigilance ? "medium" : MaxRisk(riskLevel, "low");
                confidence = Math.Min(confidence == 0d ? 0.45d : confidence, 0.45d);
                needsContext = true;
                flagged = false;

                var refinedReason = BuildStructuredReason(modelReason: verdict.Reason, profile, request, label, intent, riskLevel);
                return new AiVerdict
                {
                    Label = label,
                    Confidence = confidence,
                    Flagged = flagged,
                    Reason = refinedReason,
                    Topic = topic,
                    Intent = intent,
                    Stance = stance,
                    RiskLevel = riskLevel,
                    NeedsContextReview = needsContext,
                    UkArticleCandidates = verdict.UkArticleCandidates,
                    IncidentScore = verdict.IncidentScore,
                    IncidentEventCount = verdict.IncidentEventCount,
                    IsModelFailureFallback = verdict.IsModelFailureFallback
                };
            }

            var deterministicEscalation =
                (profile.HasIdentityCollection && (profile.HasInstructionalLanguage || profile.HasCoordinationSignals)) ||
                (profile.HasFundraisingSignals && (profile.HasFinancialObfuscationSignals || profile.HasTechnicalOpSec) && (profile.HasCoordinationSignals || profile.HasInstructionalLanguage)) ||
                (profile.HasMaterialDistributionSignals && (profile.HasInstructionalLanguage || profile.HasCoordinationSignals)) ||
                (request.ChatUnderHeightenedVigilance && profile.HasTechnicalOpSec && profile.HasInstructionalLanguage);

            var strongSuspicion =
                profile.WeightedScore >= 4.25d ||
                (profile.WeightedScore >= 3d && (request.ChatUnderHeightenedVigilance || profile.HasInstructionalLanguage));

            if (deterministicEscalation || (label is "neutral" or "unsure" && strongSuspicion))
            {
                label = "other_illegal";
                if (profile.HasMaterialDistributionSignals)
                    label = "distribution_illegal_materials";

                topic = profile.HasTechnicalOpSec
                    ? "privacy_tools"
                    : profile.HasMaterialDistributionSignals
                        ? "extremism"
                        : (profile.HasFundraisingSignals || profile.HasFinancialObfuscationSignals ? "other" : topic);
                intent = profile.HasCoordinationSignals || profile.HasIdentityCollection
                    ? "operational_planning"
                    : "instruction_request";
                stance = ResolveEscalatedStance(stance);
                riskLevel = deterministicEscalation ? "high" : MaxRisk(riskLevel, request.ChatUnderHeightenedVigilance ? "high" : "medium");
                confidence = Math.Max(confidence, deterministicEscalation ? 0.82d : 0.72d);
                needsContext = true;
                flagged = deterministicEscalation || (request.ChatUnderHeightenedVigilance && profile.WeightedScore >= 3d);
            }
            else if (request.ChatUnderHeightenedVigilance && profile.WeightedScore >= 2.25d)
            {
                riskLevel = MaxRisk(riskLevel, "medium");
                needsContext = true;
            }

            var reason = BuildStructuredReason(verdict.Reason, profile, request, label, intent, riskLevel);

            return new AiVerdict
            {
                Label = label,
                Confidence = confidence,
                Flagged = flagged,
                Reason = reason,
                Topic = topic,
                Intent = intent,
                Stance = stance,
                RiskLevel = riskLevel,
                NeedsContextReview = needsContext,
                UkArticleCandidates = verdict.UkArticleCandidates,
                IncidentScore = verdict.IncidentScore,
                IncidentEventCount = verdict.IncidentEventCount,
                IsModelFailureFallback = verdict.IsModelFailureFallback
            };
        }

        private SignalProfile BuildSignalProfile(ModerationRequest request)
        {
            var lines = CollectEvidenceLines(request);
            if (lines.Count == 0)
                return SignalProfile.Empty;

            var scoredLines = new List<SignalEvidence>(lines.Count);
            var hasFundraising = false;
            var hasOpSec = false;
            var hasFinancialObfuscation = false;
            var hasCoordination = false;
            var hasIdentityCollection = false;
            var hasInstructionalLanguage = false;
            var hasMaterialDistribution = false;
            var currentWeightedScore = 0d;
            var currentMessageHasStrongSignals = false;

            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                var lineScore = 0d;
                var isCurrentMessage = index == 0;

                if (FundraisingOrRequisitesRegex.IsMatch(line))
                {
                    hasFundraising = true;
                    lineScore += 1.0d;
                }

                if (TechnicalOpSecRegex.IsMatch(line) || SecrecyOrReconPreparationRegex.IsMatch(line))
                {
                    hasOpSec = true;
                    lineScore += 1.5d;
                }

                if (FinancialObfuscationRegex.IsMatch(line))
                {
                    hasFinancialObfuscation = true;
                    lineScore += 1.35d;
                }

                if (CoordinationOrRecruitmentRegex.IsMatch(line))
                {
                    hasCoordination = true;
                    lineScore += 1.2d;
                }

                if (IdentityCollectionRegex.IsMatch(line))
                {
                    hasIdentityCollection = true;
                    lineScore += 2.0d;
                }

                if (InstructionalLanguageRegex.IsMatch(line))
                {
                    hasInstructionalLanguage = true;
                    lineScore += 1.8d;
                }

                if (MaterialDistributionRegex.IsMatch(line) && ExtremistMaterialMarkerRegex.IsMatch(line))
                {
                    hasMaterialDistribution = true;
                    lineScore += 2.2d;
                }

                if (lineScore > 0d)
                    scoredLines.Add(new SignalEvidence(index, line, lineScore));

                if (isCurrentMessage)
                {
                    currentWeightedScore = lineScore;
                    currentMessageHasStrongSignals = lineScore >= 1.6d || hasMaterialDistribution;
                }
            }

            var weightedScore =
                (hasFundraising ? 1.0d : 0d) +
                (hasOpSec ? 1.5d : 0d) +
                (hasFinancialObfuscation ? 1.35d : 0d) +
                (hasCoordination ? 1.2d : 0d) +
                (hasIdentityCollection ? 2.0d : 0d) +
                (hasInstructionalLanguage ? 1.8d : 0d) +
                (hasMaterialDistribution ? 2.2d : 0d);

            var evidence = scoredLines
                .OrderByDescending(static item => item.Score)
                .ThenByDescending(static item => item.Index)
                .Select(static item => NormalizeEvidenceLine(item.Text))
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToArray();

            return new SignalProfile(
                HasSignals: scoredLines.Count > 0,
                HasFundraisingSignals: hasFundraising,
                HasTechnicalOpSec: hasOpSec,
                HasFinancialObfuscationSignals: hasFinancialObfuscation,
                HasCoordinationSignals: hasCoordination,
                HasIdentityCollection: hasIdentityCollection,
                HasInstructionalLanguage: hasInstructionalLanguage,
                HasMaterialDistributionSignals: hasMaterialDistribution,
                WeightedScore: weightedScore,
                ContextWeightedScore: Math.Max(0d, weightedScore - currentWeightedScore),
                CurrentMessageWeightedScore: currentWeightedScore,
                CurrentMessageHasStrongSignals: currentMessageHasStrongSignals,
                CurrentMessageLooksWeakFollowUp: IsWeakFollowUp(request.Text),
                RequiresReasonerReview:
                    weightedScore >= 3.5d ||
                    (hasIdentityCollection && hasInstructionalLanguage) ||
                    (hasOpSec && hasInstructionalLanguage) ||
                    ((hasFundraising || hasFinancialObfuscation) && hasOpSec && hasCoordination) ||
                    (hasMaterialDistribution && (hasInstructionalLanguage || hasCoordination)),
                Evidence: evidence);
        }

        private static string ResolveEscalatedStance(string? currentStance)
        {
            return currentStance switch
            {
                "support" => "support",
                "condemnation" => "condemnation",
                "educational_quote" => "educational_quote",
                "neutral_report" => "neutral_report",
                _ => "unclear"
            };
        }

        private static string ResolveWeakFollowUpStance(string? currentStance)
        {
            return currentStance switch
            {
                "condemnation" => "condemnation",
                "educational_quote" => "educational_quote",
                "neutral_report" => "neutral_report",
                _ => "unclear"
            };
        }

        private static bool IsWeakFollowUp(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var normalized = text.Trim();
            if (WeakFollowUpRegex.IsMatch(normalized))
                return true;

            return normalized.Length <= 18 &&
                   normalized.Count(static ch => !char.IsWhiteSpace(ch)) <= 16 &&
                   !normalized.Contains('\n');
        }

        private static List<string> CollectEvidenceLines(ModerationRequest request)
        {
            var result = new List<string>();
            void AddIfText(string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    result.Add(value.Trim());
            }

            AddIfText(request.Text);
            AddIfText(request.ReplyToText);
            foreach (var item in request.RecentChatMessages)
                AddIfText(item.Text);
            foreach (var item in request.RecentUserMessages)
                AddIfText(item.Text);

            return result;
        }

        private static string BuildStructuredReason(
            string? modelReason,
            SignalProfile profile,
            ModerationRequest request,
            string label,
            string intent,
            string riskLevel)
        {
            var tags = new List<string>(capacity: 5);
            if (profile.HasFundraisingSignals) tags.Add("сборы/реквизиты");
            if (profile.HasTechnicalOpSec) tags.Add("опсек/анонимизация");
            if (profile.HasFinancialObfuscationSignals) tags.Add("финансовая анонимизация");
            if (profile.HasCoordinationSignals) tags.Add("координация участников");
            if (profile.HasIdentityCollection) tags.Add("сбор персональных данных");
            if (profile.HasInstructionalLanguage) tags.Add("практические инструкции");
            if (profile.HasMaterialDistributionSignals) tags.Add("распространение/маскировка материалов");

            var evidence = profile.Evidence.Count == 0
                ? "n/a"
                : string.Join(" | ", profile.Evidence.Select(static e => $"\"{e}\""));

            var prefix = request.ChatUnderHeightenedVigilance
                ? "Чат в режиме heightened_vigilance."
                : "Контекстный анализ диалога.";

            if (profile.CurrentMessageLooksWeakFollowUp && !profile.CurrentMessageHasStrongSignals)
                prefix += " Текущее сообщение короткое и само по себе не содержит сильного нового сигнала.";

            var normalizedModelReason = TextSanitizer.NormalizeReasonText(modelReason, maxLen: 260);
            var modelPart = string.IsNullOrWhiteSpace(normalizedModelReason)
                ? string.Empty
                : $" Модель: {normalizedModelReason}";

            return $"{prefix} Выявлены сигналы: {string.Join(", ", tags)}. " +
                   $"Ключевые реплики: {evidence}. " +
                   $"Итог: label={label}, intent={intent}, risk={riskLevel}.{modelPart}";
        }

        private static string NormalizeEvidenceLine(string value)
        {
            return TextSanitizer.NormalizeReasonText(value, maxLen: 120);
        }

        private static string MaxRisk(string current, string candidate)
        {
            static int Rank(string value) => value switch
            {
                "none" => 0,
                "low" => 1,
                "medium" => 2,
                "high" => 3,
                "critical" => 4,
                _ => 1
            };

            return Rank(candidate) > Rank(current) ? candidate : current;
        }

        private static double NormalizeConfidence(double? value)
        {
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
                return 0d;

            var normalized = value.Value;

            if (normalized > 1d && normalized <= 100d)
                normalized /= 100d;

            if (normalized < 0d)
                return 0d;

            if (normalized > 1d)
                return 1d;

            return normalized;
        }
        private Classification? TryParseClassification(string content)
        {
            var candidateContent = ExtractFirstJsonObject(content) ?? content;

            // Нормальный JSON
            try
            {
                var parsed = JsonSerializer.Deserialize<Classification>(candidateContent, JsonOpt);
                if (parsed is not null) return parsed;
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "LLM returned non-JSON content; snippet={ContentSnippet}",
                    TextSanitizer.CreateSafeLogSnippet(content, maxLen: 220));
            }

            // Fallback: выдернем поля регэкспами из «полу-JSON»
            // label: строка в кавычках
            var mLabel = Regex.Match(candidateContent, "\"label\"\\s*:\\s*\"(?<l>[^\"]+)", RegexOptions.IgnoreCase);
            // confidence: число (1, 0.85, 0.9 и т.п.)
            var mConf = Regex.Match(candidateContent, "\"confidence\"\\s*:\\s*(?<c>[0-9]+(?:\\.[0-9]+)?)", RegexOptions.IgnoreCase);
            var topic = MatchJsonStringField(candidateContent, "topic");
            var intent = MatchJsonStringField(candidateContent, "intent");
            var stance = MatchJsonStringField(candidateContent, "stance");
            var riskLevel = MatchJsonStringField(candidateContent, "risk_level");
            var needsContextReview = MatchJsonBoolField(candidateContent, "needs_context_review");

            // reasons: всё, что после "reasons":"..., возможно с незакрытой кавычкой/фигурной скобкой
            var mReas = Regex.Match(candidateContent, "\"reasons\"\\s*:\\s*\"(?<r>.+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var ukArticleCandidates = MatchJsonStringArrayField(candidateContent, "uk_article_candidates");

            string? label = mLabel.Success ? mLabel.Groups["l"].Value : null;

            double? conf = null;
            if (mConf.Success &&
                double.TryParse(mConf.Groups["c"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cVal))
            {
                conf = cVal;
            }

            string? reasons = null;
            if (mReas.Success)
            {
                reasons = mReas.Groups["r"].Value;
                // подчистим незакрытый хвост
                reasons = reasons.Replace("\r", "").Replace("\n", " ");
                reasons = reasons.TrimEnd(' ', '"', '}');
                if (reasons.Length > 300) reasons = reasons[..300] + "…";
            }

            if (label is null && conf is null && reasons is null && topic is null && intent is null && stance is null && riskLevel is null && needsContextReview is null && ukArticleCandidates.Count == 0)
                return null;

            return new Classification
            {
                Label = label,
                Confidence = conf,
                Reasons = reasons,
                Topic = topic,
                Intent = intent,
                Stance = stance,
                RiskLevel = riskLevel,
                NeedsContextReview = needsContextReview,
                UkArticleCandidates = ukArticleCandidates.Count == 0 ? null : ukArticleCandidates
            };
        }

        // DTOs для OpenAI-совместимого API
        private sealed record ChatCompletionsRequest
        {
            [JsonPropertyName("model")] public required string Model { get; init; }
            [JsonPropertyName("messages")] public required List<ChatMessage> Messages { get; init; }
            [JsonPropertyName("response_format")] public ResponseFormat? ResponseFormat { get; init; }
            [JsonPropertyName("temperature")] public double? Temperature { get; init; }
            [JsonPropertyName("max_tokens")] public int? MaxTokens { get; init; }
        }

        private sealed record ChatMessage(
            [property: JsonPropertyName("role")] string Role,
            [property: JsonPropertyName("content")] string Content);

        private sealed record ResponseFormat
        {
            [JsonPropertyName("type")] public string Type { get; init; } = "json_object";
        }

        private sealed record ChatCompletionsResponse
        {
            [JsonPropertyName("choices")] public List<Choice>? Choices { get; init; }
        }

        private sealed record Choice
        {
            [JsonPropertyName("message")] public MessageContent? Message { get; init; }
        }

        private sealed record MessageContent
        {
            [JsonPropertyName("content")] public string? Content { get; init; }
            [JsonPropertyName("reasoning_content")] public string? ReasoningContent { get; init; }
        }

        private sealed record Classification
        {
            [JsonPropertyName("label")] public string? Label { get; init; }
            [JsonPropertyName("confidence")] public double? Confidence { get; init; }
            [JsonPropertyName("reasons")] public string? Reasons { get; init; }
            [JsonPropertyName("topic")] public string? Topic { get; init; }
            [JsonPropertyName("intent")] public string? Intent { get; init; }
            [JsonPropertyName("stance")] public string? Stance { get; init; }
            [JsonPropertyName("risk_level")] public string? RiskLevel { get; init; }
            [JsonPropertyName("needs_context_review")] public bool? NeedsContextReview { get; init; }
            [JsonPropertyName("uk_article_candidates")] public List<string>? UkArticleCandidates { get; init; }
        }

        private readonly record struct ModerationAttemptOutcome(
            AiVerdict? Verdict,
            string? FailureReason);

        // Мини-санитизация перед отправкой в модель
        private static ModerationRequest Sanitize(ModerationRequest request)
        {
            return new ModerationRequest
            {
                Text = TextSanitizer.MaskSensitive(request.Text, maxLen: 3000),
                ChatId = request.ChatId,
                ChatTitle = null,
                UserId = request.UserId,
                Username = null,
                MessageId = request.MessageId,
                ReplyToText = TextSanitizer.MaskSensitive(request.ReplyToText, maxLen: 500),
                ChatUnderHeightenedVigilance = request.ChatUnderHeightenedVigilance,
                ChatVigilanceUntilUtc = request.ChatVigilanceUntilUtc,
                ChatVigilanceTriggerCount = request.ChatVigilanceTriggerCount,
                ChatVigilanceLabel = request.ChatVigilanceLabel,
                ChatVigilanceTopic = request.ChatVigilanceTopic,
                ChatVigilanceRiskLevel = request.ChatVigilanceRiskLevel,
                ChatVigilanceReason = TextSanitizer.MaskSensitive(request.ChatVigilanceReason, maxLen: 200),
                HighSensitivityReview = request.HighSensitivityReview,
                RecentChatMessages = request.RecentChatMessages
                    .Select(static item => new ModerationContextMessage
                    {
                        Timestamp = item.Timestamp,
                        MessageId = item.MessageId,
                        UserId = item.UserId,
                        Username = null,
                        Text = TextSanitizer.MaskSensitive(item.Text, maxLen: 280)
                    })
                    .ToArray(),
                RecentUserMessages = request.RecentUserMessages
                    .Select(static item => new ModerationContextMessage
                    {
                        Timestamp = item.Timestamp,
                        MessageId = item.MessageId,
                        UserId = item.UserId,
                        Username = null,
                        Text = TextSanitizer.MaskSensitive(item.Text, maxLen: 280)
                    })
                    .ToArray()
            };
        }

        private static string BuildUserPrompt(ModerationRequest request)
        {
            var actorAliases = BuildActorAliases(request);
            var recentChatContext = request.RecentChatMessages
                .Select((item, index) => new
                {
                    order = index + 1,
                    actor = ResolveActorAlias(item, request, actorAliases, preferCurrentAuthor: false),
                    same_as_current_author = IsSameActor(item, request),
                    text = item.Text
                });

            var recentUserContext = request.RecentUserMessages
                .Select((item, index) => new
                {
                    order = index + 1,
                    actor = "current_author",
                    text = item.Text
                });

            var payload = new
            {
                review_mode = request.HighSensitivityReview ? "high_sensitivity_review" : "standard",
                context_policy = "all identifiers are anonymized; actor labels are local to this request and must not be treated as real usernames or ids",
                chat_attention = new
                {
                    heightened_vigilance = request.ChatUnderHeightenedVigilance,
                    heightened_until_utc = request.ChatUnderHeightenedVigilance ? request.ChatVigilanceUntilUtc : null,
                    trigger_count = request.ChatUnderHeightenedVigilance ? request.ChatVigilanceTriggerCount : null,
                    prior_label = request.ChatUnderHeightenedVigilance ? request.ChatVigilanceLabel : null,
                    prior_topic = request.ChatUnderHeightenedVigilance ? request.ChatVigilanceTopic : null,
                    prior_risk_level = request.ChatUnderHeightenedVigilance ? request.ChatVigilanceRiskLevel : null,
                    prior_reason = request.ChatUnderHeightenedVigilance ? request.ChatVigilanceReason : null
                },
                current_message = new
                {
                    actor = "current_author",
                    text = request.Text
                },
                reply_to = string.IsNullOrWhiteSpace(request.ReplyToText)
                    ? null
                    : new
                    {
                        relation = "reply_to",
                        text = request.ReplyToText
                    },
                recent_chat_context = recentChatContext,
                recent_user_context = recentUserContext
            };

            return JsonSerializer.Serialize(payload, JsonOpt);
        }

        private static Dictionary<string, string> BuildActorAliases(ModerationRequest request)
        {
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            var index = 1;

            foreach (var item in request.RecentChatMessages)
            {
                var key = GetActorKey(item.UserId, item.Username);
                if (key is null || aliases.ContainsKey(key))
                    continue;

                if (IsSameActor(item, request))
                {
                    aliases[key] = "current_author";
                    continue;
                }

                aliases[key] = $"participant_{index}";
                index++;
            }

            return aliases;
        }

        private static string ResolveActorAlias(
            ModerationContextMessage item,
            ModerationRequest request,
            IReadOnlyDictionary<string, string> aliases,
            bool preferCurrentAuthor)
        {
            if (preferCurrentAuthor || IsSameActor(item, request))
                return "current_author";

            var key = GetActorKey(item.UserId, item.Username);
            if (key is not null && aliases.TryGetValue(key, out var alias))
                return alias;

            return "participant_unknown";
        }

        private static bool IsSameActor(ModerationContextMessage item, ModerationRequest request)
        {
            if (request.UserId.HasValue && item.UserId.HasValue)
                return request.UserId.Value == item.UserId.Value;

            if (!string.IsNullOrWhiteSpace(request.Username) && !string.IsNullOrWhiteSpace(item.Username))
                return string.Equals(request.Username, item.Username, StringComparison.OrdinalIgnoreCase);

            return false;
        }

        private static string? GetActorKey(long? userId, string? username)
        {
            if (userId.HasValue)
                return $"id:{userId.Value}";

            if (!string.IsNullOrWhiteSpace(username))
                return $"username:{username.Trim().ToLowerInvariant()}";

            return null;
        }

        private static string? NormalizeToken(
            string? value,
            ISet<string> allowed,
            IReadOnlyDictionary<string, string>? aliases,
            string? fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            var normalized = value.Trim().ToLowerInvariant();
            if (aliases is not null && aliases.TryGetValue(normalized, out var alias))
                normalized = alias;

            return allowed.Contains(normalized) ? normalized : fallback;
        }

        private static string ResolveRiskLevel(
            string? rawRiskLevel,
            string label,
            double confidence,
            bool? needsContextReview,
            ModerationRequest request)
        {
            var normalized = NormalizeToken(rawRiskLevel, AllowedRiskLevels, aliases: null, fallback: null);
            if (normalized is not null)
            {
                if (request.ChatUnderHeightenedVigilance &&
                    label is not "neutral" &&
                    normalized is "none" or "low")
                {
                    return "medium";
                }

                return normalized;
            }

            if (label == "neutral" && needsContextReview != true)
                return request.ChatUnderHeightenedVigilance ? "low" : "none";

            if (label == "unsure")
                return request.ChatUnderHeightenedVigilance || needsContextReview == true ? "medium" : "low";

            if (confidence >= 0.9d)
                return "critical";

            if (confidence >= 0.75d)
                return "high";

            if (request.ChatUnderHeightenedVigilance)
                return "medium";

            return needsContextReview == true ? "medium" : "low";
        }

        private bool ShouldFlag(
            string label,
            double confidence,
            string intent,
            string stance,
            string riskLevel,
            bool? needsContextReview,
            IReadOnlyList<string> ukArticleCandidates,
            ModerationRequest request)
        {
            var hasUkSignals = ukArticleCandidates.Count > 0;

            if (label is "neutral" or "unsure")
            {
                if (request.ChatUnderHeightenedVigilance &&
                    label == "unsure" &&
                    riskLevel is "high" or "critical")
                {
                    return true;
                }

                return false;
            }

            if (confidence >= _opt.ConfidenceThreshold)
                return true;

            if (riskLevel == "critical")
                return true;

            if (riskLevel == "high" && confidence >= _opt.ConfidenceThreshold - 0.15d)
                return true;

            if (hasUkSignals && riskLevel is "high" or "critical")
                return true;

            if (request.ChatUnderHeightenedVigilance &&
                hasUkSignals &&
                intent is "instruction_request" or "operational_planning" or "recruitment" &&
                (confidence >= _opt.ConfidenceThreshold - 0.25d || needsContextReview == true))
            {
                return true;
            }

            if (needsContextReview == true &&
                intent is "advocacy" or "recruitment" or "operational_planning" or "threat" &&
                confidence >= _opt.ConfidenceThreshold - 0.20d)
            {
                return true;
            }

            if (request.ChatUnderHeightenedVigilance &&
                riskLevel == "high" &&
                intent is "instruction_request" or "operational_planning" or "recruitment" or "threat")
            {
                return true;
            }

            if (stance == "support" &&
                label is "extremism" or "terrorism" or "other_illegal" or "pro_ukraine_advocacy" or "weapons_illegal" &&
                confidence >= _opt.ConfidenceThreshold - 0.15d)
            {
                return true;
            }

            return false;
        }

        private static string? ExtractFirstJsonObject(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return null;

            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start < 0 || end <= start)
                return null;

            return content[start..(end + 1)];
        }

        private static string? MatchJsonStringField(string content, string fieldName)
        {
            var match = Regex.Match(
                content,
                $"\"{Regex.Escape(fieldName)}\"\\s*:\\s*\"(?<v>[^\"]+)\"",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? match.Groups["v"].Value : null;
        }

        private static bool? MatchJsonBoolField(string content, string fieldName)
        {
            var match = Regex.Match(
                content,
                $"\"{Regex.Escape(fieldName)}\"\\s*:\\s*(?<v>true|false)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                return null;

            return string.Equals(match.Groups["v"].Value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> MatchJsonStringArrayField(string content, string fieldName)
        {
            var result = new List<string>();
            var arrayMatch = Regex.Match(
                content,
                $"\"{Regex.Escape(fieldName)}\"\\s*:\\s*\\[(?<items>[^\\]]*)\\]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
            if (arrayMatch.Success)
            {
                foreach (Match item in Regex.Matches(arrayMatch.Groups["items"].Value, "\"(?<v>[^\"]+)\""))
                {
                    var value = item.Groups["v"].Value;
                    if (!string.IsNullOrWhiteSpace(value))
                        result.Add(value);
                }

                return result;
            }

            var singleMatch = Regex.Match(
                content,
                $"\"{Regex.Escape(fieldName)}\"\\s*:\\s*\"(?<v>[^\"]+)\"",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (singleMatch.Success)
                result.Add(singleMatch.Groups["v"].Value);

            return result;
        }

        private static IReadOnlyList<string> NormalizeUkArticleCandidates(IReadOnlyList<string>? values)
        {
            if (values is null || values.Count == 0)
                return Array.Empty<string>();

            var result = new List<string>(capacity: Math.Min(values.Count, 5));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                var normalized = value.Trim();
                if (normalized.Length > 80)
                    normalized = normalized[..80];

                if (seen.Add(normalized))
                    result.Add(normalized);

                if (result.Count >= 5)
                    break;
            }

            return result;
        }

        private sealed record FailureFallbackAssessment(
            string Label,
            string Topic,
            string Intent,
            string Stance,
            string RiskLevel,
            double Confidence,
            bool ImmediateFlag,
            string Reason,
            IReadOnlyList<string> UkArticleCandidates);

        private readonly record struct SignalProfile(
            bool HasSignals,
            bool HasFundraisingSignals,
            bool HasTechnicalOpSec,
            bool HasFinancialObfuscationSignals,
            bool HasCoordinationSignals,
            bool HasIdentityCollection,
            bool HasInstructionalLanguage,
            bool HasMaterialDistributionSignals,
            double WeightedScore,
            double ContextWeightedScore,
            double CurrentMessageWeightedScore,
            bool CurrentMessageHasStrongSignals,
            bool CurrentMessageLooksWeakFollowUp,
            bool RequiresReasonerReview,
            IReadOnlyList<string> Evidence)
        {
            public static SignalProfile Empty => new(
                HasSignals: false,
                HasFundraisingSignals: false,
                HasTechnicalOpSec: false,
                HasFinancialObfuscationSignals: false,
                HasCoordinationSignals: false,
                HasIdentityCollection: false,
                HasInstructionalLanguage: false,
                HasMaterialDistributionSignals: false,
                WeightedScore: 0d,
                ContextWeightedScore: 0d,
                CurrentMessageWeightedScore: 0d,
                CurrentMessageHasStrongSignals: false,
                CurrentMessageLooksWeakFollowUp: false,
                RequiresReasonerReview: false,
                Evidence: Array.Empty<string>());
        }

        private readonly record struct SignalEvidence(
            int Index,
            string Text,
            double Score);
    }
}

