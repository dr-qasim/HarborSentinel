using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HarborSentinel.Bot.Islamic;

public sealed class JsonIslamicContentProvider : IIslamicContentProvider
{
    private readonly ILogger<JsonIslamicContentProvider> _log;
    private readonly DuaEntry[] _duas;
    private readonly DuaEntry[] _welcomeDuas;
    private readonly AzkarEntry[] _azkar;
    private readonly AyahEntry[] _ayat;

    public JsonIslamicContentProvider(ILogger<JsonIslamicContentProvider> log)
    {
        _log = log;

        var baseDir = Path.Combine(AppContext.BaseDirectory, "data");
        var jsonOpt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        _duas = LoadArray(
            path: Path.Combine(baseDir, "duas-ru.json"),
            fallback: BuildFallbackDuas(),
            jsonOpt,
            kind: "dua");

        _welcomeDuas = LoadArray(
            path: Path.Combine(baseDir, "welcome-duas-ru.json"),
            fallback: BuildFallbackWelcomeDuas(),
            jsonOpt,
            kind: "welcome_dua");

        _azkar = LoadArray(
            path: Path.Combine(baseDir, "azkar-ru.json"),
            fallback: BuildFallbackAzkar(),
            jsonOpt,
            kind: "azkar");

        _ayat = LoadArray(
            path: Path.Combine(baseDir, "ayat-ru.json"),
            fallback: BuildFallbackAyat(),
            jsonOpt,
            kind: "ayat");
    }

    public Task<DuaEntry?> GetRandomDuaAsync(string? topicQuery, CancellationToken ct = default)
    {
        var pool = FilterByTopic(_duas, topicQuery, static item => item.TopicTags);
        if (pool.Length == 0)
            return Task.FromResult<DuaEntry?>(null);

        return Task.FromResult<DuaEntry?>(pool[Random.Shared.Next(0, pool.Length)]);
    }

    public Task<DuaEntry?> GetSuggestedDuaAsync(string? topicQuery, CancellationToken ct = default)
    {
        var pool = FilterByTopic(_duas, topicQuery, static item => item.TopicTags);
        return Task.FromResult(SelectMostCompact(pool, static item => GetDuaCompactScore(item)));
    }

    public Task<string[]> GetDuaTopicsAsync(CancellationToken ct = default) =>
        Task.FromResult(GetTopics(_duas, static item => item.TopicTags));

    public Task<DuaEntry?> GetDuaOfDayAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var pool = BuildCompactSubset(_duas, static item => GetDuaCompactScore(item));
        return Task.FromResult(SelectByDay(pool, nowUtc));
    }

    public Task<DuaEntry?> GetRandomWelcomeDuaAsync(CancellationToken ct = default)
    {
        if (_welcomeDuas.Length == 0)
            return Task.FromResult<DuaEntry?>(null);

        return Task.FromResult<DuaEntry?>(_welcomeDuas[Random.Shared.Next(0, _welcomeDuas.Length)]);
    }

    public Task<AzkarEntry?> GetRandomAzkarAsync(string? topicQuery, CancellationToken ct = default)
    {
        var pool = FilterByTopic(_azkar, topicQuery, static item => item.TopicTags);
        if (pool.Length == 0)
            return Task.FromResult<AzkarEntry?>(null);

        return Task.FromResult<AzkarEntry?>(pool[Random.Shared.Next(0, pool.Length)]);
    }

    public Task<AzkarEntry?> GetSuggestedAzkarAsync(string? topicQuery, CancellationToken ct = default)
    {
        var pool = FilterByTopic(_azkar, topicQuery, static item => item.TopicTags);
        return Task.FromResult(SelectMostCompact(pool, static item => GetAzkarCompactScore(item)));
    }

    public Task<string[]> GetAzkarTopicsAsync(CancellationToken ct = default) =>
        Task.FromResult(GetTopics(_azkar, static item => item.TopicTags));

    public Task<AzkarEntry?> GetAzkarOfDayAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var pool = BuildCompactSubset(_azkar, static item => GetAzkarCompactScore(item));
        return Task.FromResult(SelectByDay(pool, nowUtc));
    }

    public Task<AyahEntry?> GetAyahOfDayAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        if (_ayat.Length == 0)
            return Task.FromResult<AyahEntry?>(null);

        var dayNumber = DateOnly.FromDateTime(nowUtc.UtcDateTime).DayNumber;
        var index = Math.Abs(dayNumber) % _ayat.Length;
        return Task.FromResult<AyahEntry?>(_ayat[index]);
    }

    private T[] LoadArray<T>(
        string path,
        T[] fallback,
        JsonSerializerOptions jsonOpt,
        string kind)
    {
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                var items = JsonSerializer.Deserialize<T[]>(text, jsonOpt);
                if (items is { Length: > 0 })
                {
                    _log.LogInformation("Islamic content loaded: {Kind}={Count} from {Path}", kind, items.Length, path);
                    return items;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load {Kind} data from {Path}; using fallback", kind, path);
        }

        _log.LogWarning("Using fallback {Kind} data (count={Count})", kind, fallback.Length);
        return fallback;
    }

    private static T[] FilterByTopic<T>(
        T[] source,
        string? topicQuery,
        Func<T, string[]> tagsSelector)
    {
        if (source.Length == 0)
            return [];

        var query = topicQuery?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return source;

        var byTopic = source
            .Where(item => tagsSelector(item)
                .Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        return byTopic;
    }

    private static string[] GetTopics<T>(T[] source, Func<T, string[]> tagsSelector) =>
        source
            .SelectMany(tagsSelector)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static T? SelectMostCompact<T>(T[] source, Func<T, int> scoreSelector)
        where T : class
    {
        if (source.Length == 0)
            return null;

        return source
            .OrderBy(scoreSelector)
            .FirstOrDefault();
    }

    private static T[] BuildCompactSubset<T>(T[] source, Func<T, int> scoreSelector, int maxItems = 16)
    {
        if (source.Length <= maxItems)
            return source.OrderBy(scoreSelector).ToArray();

        return source
            .OrderBy(scoreSelector)
            .Take(maxItems)
            .ToArray();
    }

    private static T? SelectByDay<T>(T[] source, DateTimeOffset nowUtc)
        where T : class
    {
        if (source.Length == 0)
            return null;

        var dayNumber = DateOnly.FromDateTime(nowUtc.UtcDateTime).DayNumber;
        var index = Math.Abs(dayNumber) % source.Length;
        return source[index];
    }

    private static int GetDuaCompactScore(DuaEntry item) =>
        item.Title.Length +
        item.Arabic.Length +
        item.Transliteration.Length +
        item.TranslationRu.Length +
        (item.WhenToReadRu?.Length ?? 0) / 2 +
        (item.HowToReadRu?.Length ?? 0) / 2;

    private static int GetAzkarCompactScore(AzkarEntry item) =>
        item.Title.Length +
        item.Arabic.Length +
        item.Transliteration.Length +
        item.TranslationRu.Length +
        (item.TimeHintRu?.Length ?? 0) / 2 +
        (item.RecommendedCount?.Length ?? 0);

    private static DuaEntry[] BuildFallbackDuas() =>
    [
        new DuaEntry
        {
            Id = "dua_2_201",
            Title = "Дуа о благе в обеих жизнях",
            Arabic = "رَبَّنَا آتِنَا فِي الدُّنْيَا حَسَنَةً وَفِي الْآخِرَةِ حَسَنَةً وَقِنَا عَذَابَ النَّارِ",
            Transliteration = "Rabbana atina fid-dunya hasanatan wa fil-akhirati hasanatan wa qina 'adhaban-nar.",
            TranslationRu = "Господь наш! Даруй нам благо в этом мире и благо в мире вечном и защити нас от наказания Огня.",
            WhenToReadRu = "В любой мольбе: после намаза, в пути, в личных просьбах.",
            HowToReadRu = "Читайте с осознанностью и смирением, начиная с хвалы Аллаху и салавата Пророку ﷺ.",
            Source = "Коран, 2:201",
            TopicTags = ["общее", "благо", "защита", "мольба"]
        },
        new DuaEntry
        {
            Id = "dua_20_25",
            Title = "Дуа перед важным делом",
            Arabic = "رَبِّ اشْرَحْ لِي صَدْرِي وَيَسِّرْ لِي أَمْرِي وَاحْلُلْ عُقْدَةً مِنْ لِسَانِي يَفْقَهُوا قَوْلِي",
            Transliteration = "Rabbi-shrah li sadri wa yassir li amri wahlul 'uqdatan min lisani yafqahu qawli.",
            TranslationRu = "Господи! Раскрой мою грудь, облегчи моё дело и развяжи узел на моём языке, чтобы люди понимали мою речь.",
            WhenToReadRu = "Перед выступлением, учебой, собеседованием, ответственным разговором.",
            HowToReadRu = "Повторите несколько раз спокойно и попросите Аллаха о ясности и пользе.",
            Source = "Коран, 20:25–28",
            TopicTags = ["дела", "учёба", "работа", "волнение"]
        },
        new DuaEntry
        {
            Id = "dua_ham_hazan",
            Title = "Дуа от тревоги и печали",
            Arabic = "اللَّهُمَّ إِنِّي أَعُوذُ بِكَ مِنَ الْهَمِّ وَالْحَزَنِ ...",
            Transliteration = "Allahumma inni a'udhu bika minal-hammi wal-hazan...",
            TranslationRu = "О Аллах, я прибегаю к Тебе от тревоги и печали...",
            WhenToReadRu = "В периоды стресса, тревоги, душевной тяжести.",
            HowToReadRu = "Читайте вдумчиво утром и вечером, соединяя с таваккулем и истигфаром.",
            Source = "Сахих аль-Бухари (дуа от тревоги и печали)",
            TopicTags = ["тревога", "печаль", "стресс", "душевное"]
        }
    ];

    private static DuaEntry[] BuildFallbackWelcomeDuas() =>
    [
        new DuaEntry
        {
            Id = "welcome_dua_q_2_201",
            Title = "Дуа о благе в обоих мирах",
            Arabic = "رَبَّنَا آتِنَا فِي الدُّنْيَا حَسَنَةً وَفِي الْآخِرَةِ حَسَنَةً وَقِنَا عَذَابَ النَّارِ",
            Transliteration = "Раббана атина фид-дунья хасанатан ва филь-ахирати хасанатан ва кына 'азабан-нар.",
            TranslationRu = "Господь наш, даруй нам благо в этом мире и благо в Последней жизни и защити нас от наказания Огня.",
            WhenToReadRu = "Как общую мольбу о благе, особенно после намаза и в начале добрых дел.",
            HowToReadRu = "Читайте с надеждой на милость Аллаха и с искренним желанием блага для себя и других.",
            Source = "Коран, 2:201",
            TopicTags = ["welcome", "благо", "общая мольба"]
        },
        new DuaEntry
        {
            Id = "welcome_dua_q_3_8",
            Title = "Дуа о твёрдости сердца",
            Arabic = "رَبَّنَا لَا تُزِغْ قُلُوبَنَا بَعْدَ إِذْ هَدَيْتَنَا وَهَبْ لَنَا مِن لَّدُنكَ رَحْمَةً ۚ إِنَّكَ أَنتَ الْوَهَّابُ",
            Transliteration = "Раббана ля тузиг кулюбана ба'да из хадайтана ва хаб ляна мил-лядунка рахма, иннака анта-ль-Ваххаб.",
            TranslationRu = "Господь наш, не уклоняй наши сердца после того, как Ты наставил нас, и даруй нам от Себя милость. Воистину, Ты — Дарующий.",
            WhenToReadRu = "Когда просите о стойкости, сохранности сердец и милости Аллаха.",
            HowToReadRu = "Читайте спокойно и с искренней просьбой о прямом пути для себя и окружающих.",
            Source = "Коран, 3:8",
            TopicTags = ["welcome", "сердце", "милость", "прямой путь"]
        },
        new DuaEntry
        {
            Id = "welcome_dua_h_muslim_2721a",
            Title = "Дуа о руководстве и богобоязненности",
            Arabic = "اللَّهُمَّ إِنِّي أَسْأَلُكَ الْهُدَى وَالتُّقَى وَالْعَفَافَ وَالْغِنَى",
            Transliteration = "Аллахумма инни ас'алюка-ль-худа ват-тука валь-'афафа валь-гына.",
            TranslationRu = "О Аллах, прошу у Тебя правильного пути, богобоязненности, целомудрия и достатка.",
            WhenToReadRu = "В ежедневной мольбе, когда просите о правильной жизни и внутренней собранности.",
            HowToReadRu = "Читайте с намерением исправить себя и просить у Аллаха устойчивости в религии.",
            Source = "Сахих Муслим, 2721a",
            TopicTags = ["welcome", "руководство", "богобоязненность", "нравственность"]
        },
        new DuaEntry
        {
            Id = "welcome_dua_q_20_114",
            Title = "Дуа о прибавлении полезного знания",
            Arabic = "رَبِّ زِدْنِي عِلْمًا",
            Transliteration = "Рабби зидни 'ильма.",
            TranslationRu = "Господи, прибавь мне знания.",
            WhenToReadRu = "Когда просите у Аллаха полезного знания, ясности ума и пользы в словах и делах.",
            HowToReadRu = "Читайте с намерением искать знание ради довольства Аллаха и пользы для людей.",
            Source = "Коран, 20:114",
            TopicTags = ["welcome", "знание", "польза", "учёба"]
        },
        new DuaEntry
        {
            Id = "welcome_dua_h_ibnmajah_925",
            Title = "Дуа о полезном знании, благом уделе и принятых делах",
            Arabic = "اللَّهُمَّ إِنِّي أَسْأَلُكَ عِلْمًا نَافِعًا، وَرِزْقًا طَيِّبًا، وَعَمَلًا مُتَقَبَّلًا",
            Transliteration = "Аллахумма инни ас'алука 'ильман нафи'ан, ва ризкан таййибан, ва 'амалан мутакаббалан.",
            TranslationRu = "О Аллах, прошу у Тебя полезного знания, благого удела и принятых дел.",
            WhenToReadRu = "Когда просите о пользе в знании, баракате в уделе и принятии ваших дел.",
            HowToReadRu = "Читайте с надеждой на баракат, пользу и искренность в делах.",
            Source = "Сунан Ибн Маджа, 925 (сахих)",
            TopicTags = ["welcome", "знание", "ризк", "баракат"]
        }
    ];

    private static AzkarEntry[] BuildFallbackAzkar() =>
    [
        new AzkarEntry
        {
            Id = "zikr_tahlil_100",
            Title = "Тахлиль дня",
            Arabic = "لَا إِلَهَ إِلَّا اللَّهُ وَحْدَهُ لَا شَرِيكَ لَهُ، لَهُ الْمُلْكُ وَلَهُ الْحَمْدُ وَهُوَ عَلَى كُلِّ شَيْءٍ قَدِيرٌ",
            Transliteration = "La ilaha illallahu wahdahu la sharika lahu, lahul-mulku wa lahul-hamdu wa huwa 'ala kulli shay'in qadir.",
            TranslationRu = "Нет божества, достойного поклонения, кроме Аллаха Единого, у Которого нет сотоварища...",
            TimeHintRu = "В течение дня, особенно утром.",
            RecommendedCount = "100 раз",
            Source = "Сахих аль-Бухари, Сахих Муслим",
            TopicTags = ["утро", "день", "таухид", "защита"]
        },
        new AzkarEntry
        {
            Id = "zikr_after_salah_33",
            Title = "Азкар после намаза",
            Arabic = "سُبْحَانَ اللَّهِ، الْحَمْدُ لِلَّهِ، اللَّهُ أَكْبَرُ",
            Transliteration = "Subhanallah, Alhamdulillah, Allahu Akbar.",
            TranslationRu = "Пречист Аллах, хвала Аллаху, Аллах Велик.",
            TimeHintRu = "После обязательного намаза.",
            RecommendedCount = "33/33/34",
            Source = "Сахих аль-Бухари, Сахих Муслим",
            TopicTags = ["после намаза", "награда", "поминание"]
        },
        new AzkarEntry
        {
            Id = "zikr_subhanallahi_wabihamdihi",
            Title = "Лёгкий, но весомый зикр",
            Arabic = "سُبْحَانَ اللَّهِ وَبِحَمْدِهِ",
            Transliteration = "Subhanallahi wa bihamdihi.",
            TranslationRu = "Пречист Аллах и хвала Ему.",
            TimeHintRu = "Утром и вечером, а также в любое свободное время.",
            RecommendedCount = "100 раз",
            Source = "Сахих аль-Бухари, Сахих Муслим",
            TopicTags = ["утро", "вечер", "поминание", "прощение"]
        }
    ];

    private static AyahEntry[] BuildFallbackAyat() =>
    [
        new AyahEntry
        {
            Id = "ayah_13_28",
            SurahNameRu = "Гром",
            SurahNameAr = "الرعد",
            SurahNumber = 13,
            AyahNumber = "28",
            Arabic = "أَلَا بِذِكْرِ اللَّهِ تَطْمَئِنُّ الْقُلُوبُ",
            Transliteration = "Аля би-зикри-Ллахи татма'иннуль-кулюб.",
            TranslationRu = "Воистину, поминанием Аллаха успокаиваются сердца.",
            ReflectionRu = "Сердце укрепляется не шумом мира, а постоянным зикром и связью с Господом."
        },
        new AyahEntry
        {
            Id = "ayah_39_53",
            SurahNameRu = "Толпы",
            SurahNameAr = "الزمر",
            SurahNumber = 39,
            AyahNumber = "53",
            Arabic = "لَا تَقْنَطُوا مِن رَّحْمَةِ اللَّهِ",
            Transliteration = "Ля такнату мир-рахмати-Ллах.",
            TranslationRu = "Не отчаивайтесь в милости Аллаха.",
            ReflectionRu = "Даже после ошибок верующий возвращается к Аллаху с надеждой и искренним покаянием."
        },
        new AyahEntry
        {
            Id = "ayah_94_5_6",
            SurahNameRu = "Раскрытие",
            SurahNameAr = "الشرح",
            SurahNumber = 94,
            AyahNumber = "5–6",
            Arabic = "فَإِنَّ مَعَ الْعُسْرِ يُسْرًا • إِنَّ مَعَ الْعُسْرِ يُسْرًا",
            Transliteration = "Фа инна ма'аль-'усри юсра. Инна ма'аль-'усри юсра.",
            TranslationRu = "Воистину, за тягостью — облегчение. Воистину, за тягостью — облегчение.",
            ReflectionRu = "Испытания не вечны: терпение, дуа и упование приводят к облегчению."
        }
    ];
}
