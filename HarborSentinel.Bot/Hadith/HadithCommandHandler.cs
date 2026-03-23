using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarborSentinel.Bot.Islamic;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace HarborSentinel.Bot.Hadith;

/// <summary>
/// Обрабатывает команды, связанные с хадисами:
/// /hadith, /hadith_topics + callback-кнопки для листания тем.
/// </summary>
public sealed class HadithCommandHandler
{
    private readonly IHadithProvider _provider;
    private readonly IIslamicContentProvider _islamicProvider;

    // Запоминаем последний хадис по chatId, чтобы не повторять его подряд
    private static readonly ConcurrentDictionary<long, string?> _lastHadithByChat = new();

    // Сколько тем показываем на одной "странице" списка
    private const int TopicsPerPage = 20;
    private const int MaxButtonTextLength = 64;
    private const string TopicsPagePrefix = "ht:";
    private const string TopicsSelectPrefix = "hts:";
    private const string TopicsRandomPrefix = "htr:";
    private const string TopicsNoopCallback = "ht:noop";
    private const string DuaTopicsPagePrefix = "dt:";
    private const string DuaTopicsSelectPrefix = "dts:";
    private const string DuaTopicsRandomPrefix = "dtr:";
    private const string DuaTopicsNoopCallback = "dt:noop";
    private const string AzkarTopicsPagePrefix = "at:";
    private const string AzkarTopicsSelectPrefix = "ats:";
    private const string AzkarTopicsRandomPrefix = "atr:";
    private const string AzkarTopicsNoopCallback = "at:noop";
    private const string HelpStatePrefix = "hs:";

    private static readonly IReadOnlyList<HelpStateOption> HelpStateOptions =
    [
        new("anxiety", "Тревожно", "Когда тревожно", "тревога", "защита", "Когда сердце тревожится, полезно соединить мольбу с частым зикром и упованием на Аллаха.", ["тревога", "тревожно", "страх", "волнение"]),
        new("hardship", "Трудно", "Когда тяжело и трудно", "трудности", "упование", "Когда тяжело, лучше просить у Аллаха облегчения и укреплять сердце упованием.", ["трудно", "тяжело", "испытания", "облегчение"]),
        new("knowledge", "Нужны знания", "Когда нужны знания и ясность", "знание", null, "Если нужен свет в понимании и польза в делах, начните с дуа о знании и ясности.", ["знание", "знания", "учёба", "учеба", "ясность"]),
        new("parents", "За родителей", "Когда хотите сделать дуа за родителей", "родители", null, "Дуа за родителей — одно из самых тёплых и полезных напоминаний для сердца.", ["родители", "мама", "папа", "семья"]),
        new("morning", "Утро", "Утреннее напоминание", "утро", "утро", "Утро хорошо начинать с короткой дуа и азкаров, чтобы день прошёл с пользой и защитой.", ["утро", "утром", "после фаджра"]),
        new("evening", "Вечер", "Вечернее напоминание", "вечер", "вечер", "Вечером полезно успокоить сердце азкарами и дуа о защите и благополучии.", ["вечер", "вечером"]),
        new("repentance", "Покаяние", "Когда хотите сделать тауба", "покаяние", "покаяние", "Для покаяния полезно соединять просьбу о прощении с искренним зикром и надеждой на милость Аллаха.", ["покаяние", "тауба", "прощение"]),
        new("sleep", "Перед сном", "Напоминание перед сном", "перед сном", "сон", "Перед сном полезно завершить день дуа и спокойным зикром, поручая себя Аллаху.", ["сон", "сном", "перед сном", "ночь"])
    ];

    private sealed record HelpStateOption(
        string Key,
        string ButtonText,
        string Title,
        string? DuaTopic,
        string? AzkarTopic,
        string Intro,
        string[] Aliases);

    public HadithCommandHandler(IHadithProvider provider, IIslamicContentProvider islamicProvider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _islamicProvider = islamicProvider ?? throw new ArgumentNullException(nameof(islamicProvider));
    }

    /// <summary>
    /// Обработка обычных сообщений (команды /hadith и /hadith_topics).
    /// </summary>
    public async Task<bool> TryHandleAsync(
        ITelegramBotClient bot,
        Update update,
        CancellationToken ct = default)
    {
        if (bot is null) throw new ArgumentNullException(nameof(bot));
        if (update is null) throw new ArgumentNullException(nameof(update));

        if (update.Type != UpdateType.Message || update.Message is null)
            return false;

        var msg = update.Message;

        if (string.IsNullOrWhiteSpace(msg.Text))
            return false;

        if (msg.Chat.Type != ChatType.Group && msg.Chat.Type != ChatType.Supergroup)
            return false;

        var text = msg.Text;

        // Пытаемся вытащить команду из entities типа BotCommand
        string? command = null;
        string? args = null;

        var cmdEntity = msg.Entities?.FirstOrDefault(e => e.Type == MessageEntityType.BotCommand);
        if (cmdEntity is not null && cmdEntity.Offset == 0)
        {
            // Команда в начале сообщения, как /hadith или /hadith@BotName
            command = text.Substring(cmdEntity.Offset, cmdEntity.Length);

            // Всё, что после команды — аргументы
            if (cmdEntity.Offset + cmdEntity.Length < text.Length)
            {
                args = text[(cmdEntity.Offset + cmdEntity.Length)..].TrimStart();
            }
        }
        else
        {
            // Fallback: если по какой-то причине entities нет
            var trimmed = text.Trim();
            if (trimmed.StartsWith("/", StringComparison.Ordinal))
            {
                var spaceIndex = trimmed.IndexOf(' ');
                if (spaceIndex < 0)
                {
                    command = trimmed;
                    args = null;
                }
                else
                {
                    command = trimmed[..spaceIndex];
                    args = trimmed[(spaceIndex + 1)..].Trim();
                }
            }
        }

        if (string.IsNullOrEmpty(command))
            return false;

        // Убираем @BotName, если команда выглядит как /hadith@MyBot или /hadith_topics@MyBot
        var atIndex = command.IndexOf('@');
        if (atIndex >= 0)
            command = command[..atIndex];

        if (string.Equals(command, "/about", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "/help", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "/start", StringComparison.OrdinalIgnoreCase))
        {
            var aboutText =
               "Ас-саляму алейкум ва рахматуллахи ва баракятух.\n\n" +
               "Я — бот «Крепость Имана».\n" +
               "Прихожу не с шумом, а с напоминанием о том, что смягчает сердце, укрепляет иман и приносит пользу.\n" +
               "Помогаю находить дуа, азкары, хадисы и аяты для личной пользы и для групп, где хотят больше добрых слов, знания и бараката.\n\n" +
               "Всевышний сказал: «Напоминай, ибо напоминание приносит пользу верующим» (Коран, 51:55).\n" +
               "Посланник Аллаха ﷺ сказал: «Передайте от меня хотя бы один аят» (Сахих аль-Бухари).\n\n" +
                "Команды:\n" +
                "/hadith — получить хадис. Пример по теме: /hadith терпение.\n" +
                "/hadith_topics — посмотреть темы хадисов.\n" +
                "/dua — получить дуа. Пример по теме: /dua знания.\n" +
                "/dua_topics — посмотреть темы дуа.\n" +
                "/azkar — получить азкар. Пример по теме: /azkar утро.\n" +
                "/azkar_topics — посмотреть темы азкаров.\n" +
                "/ayat_day — аят дня.\n" +
                "/today — краткая польза на сегодня: аят, дуа и азкар.\n" +
                "/help_me — подобрать дуа и азкар по состоянию. Пример: /help_me тревога.\n" +
                "/about — информация о боте.\n\n" +
                "Если напоминание принесло пользу — поделитесь им с близкими ради довольства Аллаха.\n" +
                "Добавьте меня в свою группу, и, по воле Аллаха, в ней станет больше пользы, знания и бараката.";

            await bot.SendMessage(
                chatId: msg.Chat.Id,
                text: aboutText,
                cancellationToken: ct
            ).ConfigureAwait(false);

            return true;
        }

        // Обработка /hadith_topics
        if (string.Equals(command, "/hadith_topics", StringComparison.OrdinalIgnoreCase))
        {
            await SendTopicsPageAsync(bot, msg.Chat.Id, pageIndex: 0, messageId: null, ct).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(command, "/dua_topics", StringComparison.OrdinalIgnoreCase))
        {
            await SendDuaTopicsPageAsync(bot, msg.Chat.Id, pageIndex: 0, messageId: null, ct).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(command, "/azkar_topics", StringComparison.OrdinalIgnoreCase))
        {
            await SendAzkarTopicsPageAsync(bot, msg.Chat.Id, pageIndex: 0, messageId: null, ct).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(command, "/ayat_day", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "/ayah_day", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "/ayat", StringComparison.OrdinalIgnoreCase))
        {
            await SendAyahOfDayAsync(bot, msg.Chat.Id, ct).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(command, "/today", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "/daily", StringComparison.OrdinalIgnoreCase))
        {
            await SendTodayAsync(bot, msg.Chat.Id, ct).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(command, "/dua", StringComparison.OrdinalIgnoreCase))
        {
            var duaTopicQuery = args?.Trim();
            await SendDuaAsync(bot, msg.Chat.Id, duaTopicQuery, ct).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(command, "/azkar", StringComparison.OrdinalIgnoreCase))
        {
            var azkarTopicQuery = args?.Trim();
            await SendAzkarAsync(bot, msg.Chat.Id, azkarTopicQuery, ct).ConfigureAwait(false);
            return true;
        }

        if (string.Equals(command, "/help_me", StringComparison.OrdinalIgnoreCase))
        {
            var stateQuery = args?.Trim();
            if (string.IsNullOrWhiteSpace(stateQuery))
            {
                await SendHelpStatePickerAsync(bot, msg.Chat.Id, ct).ConfigureAwait(false);
                return true;
            }

            var option = FindHelpStateOption(stateQuery);
            if (option is null)
            {
                await bot.SendMessage(
                    chatId: msg.Chat.Id,
                    text: "Пока не нашёл такую подборку. Выберите состояние из списка ниже.",
                    replyMarkup: BuildHelpStateKeyboard(),
                    cancellationToken: ct
                ).ConfigureAwait(false);
                return true;
            }

            await SendHelpStateAsync(bot, msg.Chat.Id, option, ct).ConfigureAwait(false);
            return true;
        }

        // Обработка /hadith (случайный или по теме)
        if (!string.Equals(command, "/hadith", StringComparison.OrdinalIgnoreCase))
            return false;

        var topicQuery = args?.Trim();
        await SendHadithAsync(bot, msg.Chat.Id, topicQuery, ct).ConfigureAwait(false);
        return true;

    }

    /// <summary>
    /// Обработка callback-кнопок (листание /hadith_topics).
    /// </summary>
    public async Task<bool> TryHandleCallbackAsync(
        ITelegramBotClient bot,
        CallbackQuery cq,
        CancellationToken ct = default)
    {
        if (bot is null) throw new ArgumentNullException(nameof(bot));
        if (cq is null) throw new ArgumentNullException(nameof(cq));

        var data = cq.Data;
        if (string.IsNullOrEmpty(data))
            return false;

        if (data.StartsWith(HelpStatePrefix, StringComparison.Ordinal))
        {
            if (cq.Message is null)
            {
                await bot.AnswerCallbackQuery(
                    callbackQueryId: cq.Id,
                    text: "Не удалось открыть подборку.",
                    cancellationToken: ct
                ).ConfigureAwait(false);
                return true;
            }

            var stateKey = data[HelpStatePrefix.Length..];
            var option = HelpStateOptions.FirstOrDefault(item => string.Equals(item.Key, stateKey, StringComparison.Ordinal));
            if (option is null)
            {
                await bot.AnswerCallbackQuery(
                    callbackQueryId: cq.Id,
                    text: "Подборка обновилась. Откройте список снова.",
                    cancellationToken: ct
                ).ConfigureAwait(false);
                return true;
            }

            await bot.AnswerCallbackQuery(
                callbackQueryId: cq.Id,
                cancellationToken: ct
            ).ConfigureAwait(false);

            await SendHelpStateAsync(bot, cq.Message.Chat.Id, option, ct).ConfigureAwait(false);
            return true;
        }

        if (data.StartsWith(DuaTopicsRandomPrefix, StringComparison.Ordinal))
        {
            if (cq.Message is null)
            {
                await bot.AnswerCallbackQuery(
                    callbackQueryId: cq.Id,
                    text: "Не удалось отправить дуа: нет чата.",
                    cancellationToken: ct
                ).ConfigureAwait(false);
                return true;
            }

            await bot.AnswerCallbackQuery(
                callbackQueryId: cq.Id,
                cancellationToken: ct
            ).ConfigureAwait(false);

            await SendDuaAsync(bot, cq.Message.Chat.Id, topicQuery: null, ct).ConfigureAwait(false);
            return true;
        }

        if (data.StartsWith(DuaTopicsSelectPrefix, StringComparison.Ordinal))
        {
            if (cq.Message is null)
            {
                await bot.AnswerCallbackQuery(
                    callbackQueryId: cq.Id,
                    text: "Не удалось отправить дуа: нет чата.",
                    cancellationToken: ct
                ).ConfigureAwait(false);
                return true;
            }

            var indexPart = data[DuaTopicsSelectPrefix.Length..];
            if (!int.TryParse(indexPart, out var topicIndex))
            {
                await bot.AnswerCallbackQuery(
                    callbackQueryId: cq.Id,
                    text: "Не удалось определить тему.",
                    cancellationToken: ct
                ).ConfigureAwait(false);
                return true;
            }

            var topics = await _islamicProvider.GetDuaTopicsAsync(ct).ConfigureAwait(false);
            if (topicIndex < 0 || topicIndex >= topics.Length)
            {
                await bot.AnswerCallbackQuery(
                    callbackQueryId: cq.Id,
                    text: "Темы обновились. Откройте список снова.",
                    cancellationToken: ct
                ).ConfigureAwait(false);
                return true;
            }

            await bot.AnswerCallbackQuery(
                callbackQueryId: cq.Id,
                cancellationToken: ct
            ).ConfigureAwait(false);

            await SendDuaAsync(bot, cq.Message.Chat.Id, topics[topicIndex], ct).ConfigureAwait(false);
            return true;
        }

        if (data.StartsWith(DuaTopicsPagePrefix, StringComparison.Ordinal))
        {
            if (data == DuaTopicsNoopCallback)
            {
                await bot.AnswerCallbackQuery(
                    callbackQueryId: cq.Id,
                    cancellationToken: ct
                ).ConfigureAwait(false);
                return true;
            }

            if (cq.Message is null)
            {
                await bot.AnswerCallbackQuery(
                    callbackQueryId: cq.Id,
                    text: "Не удалось обновить список.",
                    cancellationToken: ct
                ).ConfigureAwait(false);
                return true;
            }

            var duaPagePart = data[DuaTopicsPagePrefix.Length..];
            if (!int.TryParse(duaPagePart, out var duaPageIndex))
            {
                await bot.AnswerCallbackQuery(
                    callbackQueryId: cq.Id,
                    text: "Не удалось определить страницу.",
                    cancellationToken: ct
                ).ConfigureAwait(false);
                return true;
            }

            await SendDuaTopicsPageAsync(
                bot,
                cq.Message.Chat.Id,
                duaPageIndex,
                cq.Message.MessageId,
                ct).ConfigureAwait(false);

            await bot.AnswerCallbackQuery(
                callbackQueryId: cq.Id,
                cancellationToken: ct
            ).ConfigureAwait(false);

            return true;
        }

        if (data.StartsWith(AzkarTopicsRandomPrefix, StringComparison.Ordinal))
        {
            if (cq.Message is null)
            {
                await bot.AnswerCallbackQuery(
                    callbackQueryId: cq.Id,
                    text: "Не удалось отправить азкар: нет чата.",
                    cancellationToken: ct
                ).ConfigureAwait(false);
                return true;
            }

            await bot.AnswerCallbackQuery(
                callbackQueryId: cq.Id,
                cancellationToken: ct
            ).ConfigureAwait(false);

            await SendAzkarAsync(bot, cq.Message.Chat.Id, topicQuery: null, ct).ConfigureAwait(false);
            return true;
        }

        if (data.StartsWith(AzkarTopicsSelectPrefix, StringComparison.Ordinal))
        {
            if (cq.Message is null)
            {
                await bot.AnswerCallbackQuery(
                    callbackQueryId: cq.Id,
                    text: "Не удалось отправить азкар: нет чата.",
                    cancellationToken: ct
                ).ConfigureAwait(false);
                return true;
            }

            var indexPart = data[AzkarTopicsSelectPrefix.Length..];
            if (!int.TryParse(indexPart, out var topicIndex))
            {
                await bot.AnswerCallbackQuery(
                    callbackQueryId: cq.Id,
                    text: "Не удалось определить тему.",
                    cancellationToken: ct
                ).ConfigureAwait(false);
                return true;
            }

            var topics = await _islamicProvider.GetAzkarTopicsAsync(ct).ConfigureAwait(false);
            if (topicIndex < 0 || topicIndex >= topics.Length)
            {
                await bot.AnswerCallbackQuery(
                    callbackQueryId: cq.Id,
                    text: "Темы обновились. Откройте список снова.",
                    cancellationToken: ct
                ).ConfigureAwait(false);
                return true;
            }

            await bot.AnswerCallbackQuery(
                callbackQueryId: cq.Id,
                cancellationToken: ct
            ).ConfigureAwait(false);

            await SendAzkarAsync(bot, cq.Message.Chat.Id, topics[topicIndex], ct).ConfigureAwait(false);
            return true;
        }

        if (data.StartsWith(AzkarTopicsPagePrefix, StringComparison.Ordinal))
        {
            if (data == AzkarTopicsNoopCallback)
            {
                await bot.AnswerCallbackQuery(
                    callbackQueryId: cq.Id,
                    cancellationToken: ct
                ).ConfigureAwait(false);
                return true;
            }

            if (cq.Message is null)
            {
                await bot.AnswerCallbackQuery(
                    callbackQueryId: cq.Id,
                    text: "Не удалось обновить список.",
                    cancellationToken: ct
                ).ConfigureAwait(false);
                return true;
            }

            var azkarPagePart = data[AzkarTopicsPagePrefix.Length..];
            if (!int.TryParse(azkarPagePart, out var azkarPageIndex))
            {
                await bot.AnswerCallbackQuery(
                    callbackQueryId: cq.Id,
                    text: "Не удалось определить страницу.",
                    cancellationToken: ct
                ).ConfigureAwait(false);
                return true;
            }

            await SendAzkarTopicsPageAsync(
                bot,
                cq.Message.Chat.Id,
                azkarPageIndex,
                cq.Message.MessageId,
                ct).ConfigureAwait(false);

            await bot.AnswerCallbackQuery(
                callbackQueryId: cq.Id,
                cancellationToken: ct
            ).ConfigureAwait(false);

            return true;
        }

        if (data.StartsWith(TopicsRandomPrefix, StringComparison.Ordinal))
        {
            if (cq.Message is null)
            {
                await bot.AnswerCallbackQuery(
                    callbackQueryId: cq.Id,
                    text: "Не удалось отправить хадис: нет чата.",
                    cancellationToken: ct
                ).ConfigureAwait(false);
                return true;
            }

            await bot.AnswerCallbackQuery(
                callbackQueryId: cq.Id,
                cancellationToken: ct
            ).ConfigureAwait(false);

            await SendHadithAsync(bot, cq.Message.Chat.Id, topicQuery: null, ct).ConfigureAwait(false);
            return true;
        }

        if (data.StartsWith(TopicsSelectPrefix, StringComparison.Ordinal))
        {
            if (cq.Message is null)
            {
                await bot.AnswerCallbackQuery(
                    callbackQueryId: cq.Id,
                    text: "Не удалось отправить хадис: нет чата.",
                    cancellationToken: ct
                ).ConfigureAwait(false);
                return true;
            }

            var indexPart = data[TopicsSelectPrefix.Length..];
            if (!int.TryParse(indexPart, out var topicIndex))
            {
                await bot.AnswerCallbackQuery(
                    callbackQueryId: cq.Id,
                    text: "Не удалось определить тему.",
                    cancellationToken: ct
                ).ConfigureAwait(false);
                return true;
            }

            var topics = await _provider.GetAllTopicsAsync(ct).ConfigureAwait(false);
            if (topicIndex < 0 || topicIndex >= topics.Length)
            {
                await bot.AnswerCallbackQuery(
                    callbackQueryId: cq.Id,
                    text: "Темы обновились. Откройте список снова.",
                    cancellationToken: ct
                ).ConfigureAwait(false);
                return true;
            }

            await bot.AnswerCallbackQuery(
                callbackQueryId: cq.Id,
                cancellationToken: ct
            ).ConfigureAwait(false);

            await SendHadithAsync(bot, cq.Message.Chat.Id, topics[topicIndex], ct).ConfigureAwait(false);
            return true;
        }

        if (!data.StartsWith(TopicsPagePrefix, StringComparison.Ordinal))
            return false;

        if (data == TopicsNoopCallback)
        {
            await bot.AnswerCallbackQuery(
                callbackQueryId: cq.Id,
                cancellationToken: ct
            ).ConfigureAwait(false);
            return true;
        }

        if (cq.Message is null)
        {
            await bot.AnswerCallbackQuery(
                callbackQueryId: cq.Id,
                text: "Не удалось обновить список.",
                cancellationToken: ct
            ).ConfigureAwait(false);
            return true;
        }

        var pagePart = data[TopicsPagePrefix.Length..];
        if (!int.TryParse(pagePart, out var pageIndex))
        {
            await bot.AnswerCallbackQuery(
                callbackQueryId: cq.Id,
                text: "Не удалось определить страницу.",
                cancellationToken: ct
            ).ConfigureAwait(false);
            return true;
        }

        await SendTopicsPageAsync(
            bot,
            cq.Message.Chat.Id,
            pageIndex,
            cq.Message.MessageId,
            ct).ConfigureAwait(false);

        await bot.AnswerCallbackQuery(
            callbackQueryId: cq.Id,
            cancellationToken: ct
        ).ConfigureAwait(false);

        return true;
    }

    private async Task SendHadithAsync(
        ITelegramBotClient bot,
        long chatId,
        string? topicQuery,
        CancellationToken ct)
    {
        var query = topicQuery?.Trim();

        HadithEntry? hadith;
        if (string.IsNullOrWhiteSpace(query))
        {
            hadith = await _provider.GetRandomAsync(ct).ConfigureAwait(false);
        }
        else
        {
            hadith = await _provider.GetRandomByTopicAsync(query, ct).ConfigureAwait(false);
        }

        // Простая защита от повтора того же самого хадиса подряд в одном чате
        if (hadith is not null)
        {
            if (_lastHadithByChat.TryGetValue(chatId, out var lastId) &&
                !string.IsNullOrEmpty(lastId) &&
                string.Equals(lastId, hadith.Id, StringComparison.Ordinal))
            {
                // Делаем одну дополнительную попытку взять другой хадис
                HadithEntry? secondTry;
                if (string.IsNullOrWhiteSpace(query))
                    secondTry = await _provider.GetRandomAsync(ct).ConfigureAwait(false);
                else
                    secondTry = await _provider.GetRandomByTopicAsync(query, ct).ConfigureAwait(false);

                if (secondTry is not null)
                    hadith = secondTry;
            }

            // Запоминаем тот хадис, который в итоге отдаем пользователю
            if (!string.IsNullOrEmpty(hadith.Id))
                _lastHadithByChat[chatId] = hadith.Id;
        }

        if (hadith is null)
        {
            var text = string.IsNullOrWhiteSpace(query)
                ? "Пока нет доступных хадисов."
                : "Пока не нашёл хадис по такой теме. Попробуйте выбрать тему из списка /hadith_topics.";

            await bot.SendMessage(
                chatId: chatId,
                text: text,
                cancellationToken: ct
            ).ConfigureAwait(false);

            return;
        }

        var response =
            "Хадис\n\n" +
            hadith.TextRu;

        if (!string.IsNullOrWhiteSpace(hadith.ExplanationRu))
        {
            response += "\n\nПояснение: " + hadith.ExplanationRu;
        }

        response += HadithTextFormatter.BuildSourceLine(hadith.Source);

        await bot.SendMessage(
            chatId: chatId,
            text: response,
            cancellationToken: ct
        ).ConfigureAwait(false);
    }

    private async Task SendDuaAsync(
        ITelegramBotClient bot,
        long chatId,
        string? topicQuery,
        CancellationToken ct)
    {
        var query = topicQuery?.Trim();
        var dua = await _islamicProvider.GetRandomDuaAsync(query, ct).ConfigureAwait(false);

        if (dua is null)
        {
            var text = string.IsNullOrWhiteSpace(query)
                ? "Пока нет доступных дуа."
                : "Пока не нашёл дуа по такой теме. Попробуйте выбрать тему из списка /dua_topics.";

            await bot.SendMessage(
                chatId: chatId,
                text: text,
                cancellationToken: ct
            ).ConfigureAwait(false);

            return;
        }

        var sb = new StringBuilder();
        sb.Append("Дуа: ");
        sb.AppendLine(dua.Title);
        sb.AppendLine();
        AppendSection(sb, "Арабский текст", dua.Arabic);
        AppendSection(sb, "Транслитерация", dua.Transliteration);
        AppendSection(sb, "Перевод", dua.TranslationRu);
        AppendSection(sb, "Когда читать", dua.WhenToReadRu);
        AppendSection(sb, "Как читать", dua.HowToReadRu);
        AppendSection(sb, "Источник", dua.Source, addTrailingBlankLine: false);

        await bot.SendMessage(
            chatId: chatId,
            text: sb.ToString(),
            cancellationToken: ct
        ).ConfigureAwait(false);
    }

    private async Task SendAzkarAsync(
        ITelegramBotClient bot,
        long chatId,
        string? topicQuery,
        CancellationToken ct)
    {
        var query = topicQuery?.Trim();
        var azkar = await _islamicProvider.GetRandomAzkarAsync(query, ct).ConfigureAwait(false);

        if (azkar is null)
        {
            var text = string.IsNullOrWhiteSpace(query)
                ? "Пока нет доступных азкаров."
                : "Пока не нашёл азкар по такой теме. Попробуйте выбрать тему из списка /azkar_topics.";

            await bot.SendMessage(
                chatId: chatId,
                text: text,
                cancellationToken: ct
            ).ConfigureAwait(false);

            return;
        }

        var sb = new StringBuilder();
        sb.Append("Азкар: ");
        sb.AppendLine(azkar.Title);
        sb.AppendLine();
        AppendSection(sb, "Арабский текст", azkar.Arabic);
        AppendSection(sb, "Транслитерация", azkar.Transliteration);
        AppendSection(sb, "Перевод", azkar.TranslationRu);
        AppendSection(sb, "Когда читать", azkar.TimeHintRu);

        if (!string.IsNullOrWhiteSpace(azkar.RecommendedCount))
        {
            AppendSection(sb, "Рекомендуемое количество", azkar.RecommendedCount);
        }

        AppendSection(sb, "Источник", azkar.Source, addTrailingBlankLine: false);

        await bot.SendMessage(
            chatId: chatId,
            text: sb.ToString(),
            cancellationToken: ct
        ).ConfigureAwait(false);
    }

    private static void AppendSection(
        StringBuilder sb,
        string title,
        string? value,
        bool addTrailingBlankLine = true)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        sb.Append(title);
        sb.AppendLine(":");
        sb.AppendLine(value.Trim());

        if (addTrailingBlankLine)
            sb.AppendLine();
    }

    private async Task SendAyahOfDayAsync(
        ITelegramBotClient bot,
        long chatId,
        CancellationToken ct)
    {
        var ayah = await _islamicProvider.GetAyahOfDayAsync(DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        if (ayah is null)
        {
            await bot.SendMessage(
                chatId: chatId,
                text: "Пока нет доступного аята дня.",
                cancellationToken: ct
            ).ConfigureAwait(false);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Аят дня");
        sb.Append("Сура: ");
        sb.Append(ayah.SurahNameRu);
        sb.Append(" (");
        sb.Append(ayah.SurahNameAr);
        sb.AppendLine(")");
        sb.Append("Аят: ");
        sb.AppendLine(ayah.AyahNumber);
        sb.AppendLine();
        AppendSection(sb, "Арабский текст", ayah.Arabic);
        AppendSection(sb, "Транслитерация", ayah.Transliteration);
        AppendSection(sb, "Перевод", ayah.TranslationRu);
        AppendSection(sb, "Краткое напоминание", ayah.ReflectionRu);
        AppendSection(sb, "Источник", $"Коран {ayah.SurahNumber}:{ayah.AyahNumber}", addTrailingBlankLine: false);

        await bot.SendMessage(
            chatId: chatId,
            text: sb.ToString(),
            cancellationToken: ct
        ).ConfigureAwait(false);
    }

    private async Task SendTodayAsync(
        ITelegramBotClient bot,
        long chatId,
        CancellationToken ct)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var ayah = await _islamicProvider.GetAyahOfDayAsync(nowUtc, ct).ConfigureAwait(false);
        var dua = await _islamicProvider.GetDuaOfDayAsync(nowUtc, ct).ConfigureAwait(false);
        var azkar = await _islamicProvider.GetAzkarOfDayAsync(nowUtc, ct).ConfigureAwait(false);

        if (ayah is null && dua is null && azkar is null)
        {
            await bot.SendMessage(
                chatId: chatId,
                text: "Пока не удалось подобрать пользу на сегодня.",
                cancellationToken: ct
            ).ConfigureAwait(false);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Польза на сегодня");
        sb.AppendLine();

        if (ayah is not null)
        {
            sb.AppendLine("Аят");
            sb.Append(ayah.SurahNameRu);
            sb.Append(" (");
            sb.Append(ayah.SurahNumber);
            sb.Append(':');
            sb.Append(ayah.AyahNumber);
            sb.AppendLine(")");
            sb.AppendLine(ayah.Arabic.Trim());
            sb.AppendLine(ayah.TranslationRu.Trim());
            sb.AppendLine();
        }

        if (dua is not null)
        {
            AppendCompactDuaCard(sb, "Дуа", dua);
        }

        if (azkar is not null)
        {
            AppendCompactAzkarCard(sb, "Азкар", azkar);
        }

        sb.AppendLine("Если хотите подробнее:");

        if (ayah is not null)
            sb.AppendLine("/ayat_day");

        if (dua is not null)
            sb.AppendLine("/dua");

        if (azkar is not null)
            sb.AppendLine("/azkar");

        await bot.SendMessage(
            chatId: chatId,
            text: sb.ToString().TrimEnd(),
            cancellationToken: ct
        ).ConfigureAwait(false);
    }

    private async Task SendHelpStatePickerAsync(
        ITelegramBotClient bot,
        long chatId,
        CancellationToken ct)
    {
        const string text =
            "Подбор по состоянию\n\n" +
            "Выберите, что вам сейчас ближе. Я подберу короткую дуа и, если уместно, азкар по теме.";

        await bot.SendMessage(
            chatId: chatId,
            text: text,
            replyMarkup: BuildHelpStateKeyboard(),
            cancellationToken: ct
        ).ConfigureAwait(false);
    }

    private async Task SendHelpStateAsync(
        ITelegramBotClient bot,
        long chatId,
        HelpStateOption option,
        CancellationToken ct)
    {
        var dua = string.IsNullOrWhiteSpace(option.DuaTopic)
            ? null
            : await _islamicProvider.GetSuggestedDuaAsync(option.DuaTopic, ct).ConfigureAwait(false);

        var azkar = string.IsNullOrWhiteSpace(option.AzkarTopic)
            ? null
            : await _islamicProvider.GetSuggestedAzkarAsync(option.AzkarTopic, ct).ConfigureAwait(false);

        if (dua is null && azkar is null)
        {
            await bot.SendMessage(
                chatId: chatId,
                text: "Пока не удалось подобрать напоминание по этой теме.",
                cancellationToken: ct
            ).ConfigureAwait(false);
            return;
        }

        var sb = new StringBuilder();
        sb.Append("Подборка: ");
        sb.AppendLine(option.Title);
        sb.AppendLine();
        sb.AppendLine(option.Intro);
        sb.AppendLine();

        if (dua is not null)
        {
            AppendCompactDuaCard(sb, "Короткая дуа", dua);
        }

        if (azkar is not null)
        {
            AppendCompactAzkarCard(sb, "Азкар", azkar);
        }

        sb.AppendLine("Если хотите продолжить по этой теме:");

        if (!string.IsNullOrWhiteSpace(option.DuaTopic))
            sb.Append("/dua ").AppendLine(option.DuaTopic);

        if (!string.IsNullOrWhiteSpace(option.AzkarTopic))
            sb.Append("/azkar ").AppendLine(option.AzkarTopic);

        await bot.SendMessage(
            chatId: chatId,
            text: sb.ToString().TrimEnd(),
            cancellationToken: ct
        ).ConfigureAwait(false);
    }

    private static HelpStateOption? FindHelpStateOption(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var normalized = query.Trim().ToLowerInvariant();
        return HelpStateOptions.FirstOrDefault(item =>
            string.Equals(item.Key, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.ButtonText, normalized, StringComparison.OrdinalIgnoreCase) ||
            item.Aliases.Any(alias =>
                normalized.Contains(alias, StringComparison.OrdinalIgnoreCase) ||
                alias.Contains(normalized, StringComparison.OrdinalIgnoreCase)));
    }

    private static InlineKeyboardMarkup BuildHelpStateKeyboard()
    {
        var rows = new List<List<InlineKeyboardButton>>();

        for (var index = 0; index < HelpStateOptions.Count; index += 2)
        {
            var row = new List<InlineKeyboardButton>();
            row.Add(InlineKeyboardButton.WithCallbackData(
                text: HelpStateOptions[index].ButtonText,
                callbackData: HelpStatePrefix + HelpStateOptions[index].Key));

            if (index + 1 < HelpStateOptions.Count)
            {
                row.Add(InlineKeyboardButton.WithCallbackData(
                    text: HelpStateOptions[index + 1].ButtonText,
                    callbackData: HelpStatePrefix + HelpStateOptions[index + 1].Key));
            }

            rows.Add(row);
        }

        return new InlineKeyboardMarkup(rows);
    }

    private static void AppendCompactDuaCard(StringBuilder sb, string sectionTitle, DuaEntry dua)
    {
        sb.AppendLine(sectionTitle);
        sb.AppendLine(dua.Title);
        sb.AppendLine(dua.Arabic.Trim());
        sb.AppendLine(dua.Transliteration.Trim());
        sb.AppendLine(dua.TranslationRu.Trim());
        sb.Append("Источник: ");
        sb.AppendLine(dua.Source.Trim());
        sb.AppendLine();
    }

    private static void AppendCompactAzkarCard(StringBuilder sb, string sectionTitle, AzkarEntry azkar)
    {
        sb.AppendLine(sectionTitle);
        sb.AppendLine(azkar.Title);
        sb.AppendLine(azkar.Arabic.Trim());
        sb.AppendLine(azkar.Transliteration.Trim());
        sb.AppendLine(azkar.TranslationRu.Trim());

        if (!string.IsNullOrWhiteSpace(azkar.RecommendedCount))
        {
            sb.Append("Сколько читать: ");
            sb.AppendLine(azkar.RecommendedCount.Trim());
        }

        sb.Append("Источник: ");
        sb.AppendLine(azkar.Source.Trim());
        sb.AppendLine();
    }

    private async Task SendTopicsPageAsync(
        ITelegramBotClient bot,
        long chatId,
        int pageIndex,
        int? messageId,
        CancellationToken ct)
    {
        var topics = await _provider.GetAllTopicsAsync(ct).ConfigureAwait(false);
        if (topics.Length == 0)
        {
            var text = "Пока нет доступных тем хадисов.";
            if (messageId.HasValue)
            {
                await bot.EditMessageText(
                    chatId: chatId,
                    messageId: messageId.Value,
                    text: text,
                    cancellationToken: ct
                ).ConfigureAwait(false);
            }
            else
            {
                await bot.SendMessage(
                    chatId: chatId,
                    text: text,
                    cancellationToken: ct
                ).ConfigureAwait(false);
            }

            return;
        }

        var pageCount = (topics.Length + TopicsPerPage - 1) / TopicsPerPage;
        if (pageCount <= 0) pageCount = 1;
        pageIndex = Math.Clamp(pageIndex, 0, pageCount - 1);

        var textToSend = BuildTopicsPageText(topics, pageIndex, pageCount);
        var keyboard = BuildTopicsKeyboard(topics, pageIndex, pageCount);

        if (messageId.HasValue)
        {
            // обновляем существующее сообщение (меняем текст + клавиатуру)
            await bot.EditMessageText(
                chatId: chatId,
                messageId: messageId.Value,
                text: textToSend,
                replyMarkup: keyboard,
                cancellationToken: ct
            ).ConfigureAwait(false);
        }
        else
        {
            await bot.SendMessage(
                chatId: chatId,
                text: textToSend,
                replyMarkup: keyboard,
                cancellationToken: ct
            ).ConfigureAwait(false);
        }
    }

    private async Task SendDuaTopicsPageAsync(
        ITelegramBotClient bot,
        long chatId,
        int pageIndex,
        int? messageId,
        CancellationToken ct)
    {
        var topics = await _islamicProvider.GetDuaTopicsAsync(ct).ConfigureAwait(false);
        if (topics.Length == 0)
        {
            var text = "Пока нет доступных тем дуа.";
            if (messageId.HasValue)
            {
                await bot.EditMessageText(
                    chatId: chatId,
                    messageId: messageId.Value,
                    text: text,
                    cancellationToken: ct
                ).ConfigureAwait(false);
            }
            else
            {
                await bot.SendMessage(
                    chatId: chatId,
                    text: text,
                    cancellationToken: ct
                ).ConfigureAwait(false);
            }

            return;
        }

        var pageCount = (topics.Length + TopicsPerPage - 1) / TopicsPerPage;
        if (pageCount <= 0) pageCount = 1;
        pageIndex = Math.Clamp(pageIndex, 0, pageCount - 1);

        var textToSend = BuildTopicsPageText(
            topics,
            pageIndex,
            pageCount,
            title: "Доступные темы дуа",
            commandByTopicHint: "/dua <часть названия темы>",
            randomHint: "Чтобы получить случайную дуа, нажмите кнопку \"Случайная дуа\".",
            randomByCommandHint: "Команда /dua без темы выдает случайную дуа.");
        var keyboard = BuildTopicsKeyboard(
            topics,
            pageIndex,
            pageCount,
            randomButtonText: "Случайная дуа",
            randomPrefix: DuaTopicsRandomPrefix,
            selectPrefix: DuaTopicsSelectPrefix,
            pagePrefix: DuaTopicsPagePrefix,
            noopCallback: DuaTopicsNoopCallback);

        if (messageId.HasValue)
        {
            await bot.EditMessageText(
                chatId: chatId,
                messageId: messageId.Value,
                text: textToSend,
                replyMarkup: keyboard,
                cancellationToken: ct
            ).ConfigureAwait(false);
        }
        else
        {
            await bot.SendMessage(
                chatId: chatId,
                text: textToSend,
                replyMarkup: keyboard,
                cancellationToken: ct
            ).ConfigureAwait(false);
        }
    }

    private async Task SendAzkarTopicsPageAsync(
        ITelegramBotClient bot,
        long chatId,
        int pageIndex,
        int? messageId,
        CancellationToken ct)
    {
        var topics = await _islamicProvider.GetAzkarTopicsAsync(ct).ConfigureAwait(false);
        if (topics.Length == 0)
        {
            var text = "Пока нет доступных тем азкаров.";
            if (messageId.HasValue)
            {
                await bot.EditMessageText(
                    chatId: chatId,
                    messageId: messageId.Value,
                    text: text,
                    cancellationToken: ct
                ).ConfigureAwait(false);
            }
            else
            {
                await bot.SendMessage(
                    chatId: chatId,
                    text: text,
                    cancellationToken: ct
                ).ConfigureAwait(false);
            }

            return;
        }

        var pageCount = (topics.Length + TopicsPerPage - 1) / TopicsPerPage;
        if (pageCount <= 0) pageCount = 1;
        pageIndex = Math.Clamp(pageIndex, 0, pageCount - 1);

        var textToSend = BuildTopicsPageText(
            topics,
            pageIndex,
            pageCount,
            title: "Доступные темы азкаров",
            commandByTopicHint: "/azkar <часть названия темы>",
            randomHint: "Чтобы получить случайный азкар, нажмите кнопку \"Случайный азкар\".",
            randomByCommandHint: "Команда /azkar без темы выдает случайный азкар.");
        var keyboard = BuildTopicsKeyboard(
            topics,
            pageIndex,
            pageCount,
            randomButtonText: "Случайный азкар",
            randomPrefix: AzkarTopicsRandomPrefix,
            selectPrefix: AzkarTopicsSelectPrefix,
            pagePrefix: AzkarTopicsPagePrefix,
            noopCallback: AzkarTopicsNoopCallback);

        if (messageId.HasValue)
        {
            await bot.EditMessageText(
                chatId: chatId,
                messageId: messageId.Value,
                text: textToSend,
                replyMarkup: keyboard,
                cancellationToken: ct
            ).ConfigureAwait(false);
        }
        else
        {
            await bot.SendMessage(
                chatId: chatId,
                text: textToSend,
                replyMarkup: keyboard,
                cancellationToken: ct
            ).ConfigureAwait(false);
        }
    }

    private static string BuildTopicsPageText(string[] topics, int pageIndex, int pageCount)
    {
        return BuildTopicsPageText(
            topics,
            pageIndex,
            pageCount,
            title: "Доступные темы хадисов",
            commandByTopicHint: "/hadith <часть названия темы>",
            randomHint: "Чтобы получить случайный хадис, нажмите кнопку \"Случайный хадис\".",
            randomByCommandHint: "Команда /hadith без темы выдает случайный хадис.");
    }

    private static string BuildTopicsPageText(
        string[] topics,
        int pageIndex,
        int pageCount,
        string title,
        string commandByTopicHint,
        string randomHint,
        string randomByCommandHint)
    {
        var total = topics.Length;
        var start = pageIndex * TopicsPerPage;
        if (start < 0) start = 0;
        if (start >= total)
            return "Пока нет доступных тем хадисов.";

        var count = Math.Min(TopicsPerPage, total - start);
        var pageTopics = topics.Skip(start).Take(count);

        var sb = new StringBuilder();
        sb.Append(title);
        sb.Append(" (страница ");
        sb.Append(pageIndex + 1);
        sb.Append(" из ");
        sb.Append(pageCount);
        sb.AppendLine("):");
        sb.AppendLine();

        foreach (var t in pageTopics)
        {
            sb.Append("- ");
            sb.AppendLine(t);
        }

        sb.AppendLine();
        sb.Append("Нажмите кнопку темы ниже или отправьте ");
        sb.Append(commandByTopicHint);
        sb.AppendLine(".");
        sb.AppendLine(randomHint);
        sb.AppendLine(randomByCommandHint);

        return sb.ToString();
    }

    private static InlineKeyboardMarkup BuildTopicsKeyboard(string[] topics, int pageIndex, int pageCount)
    {
        return BuildTopicsKeyboard(
            topics,
            pageIndex,
            pageCount,
            randomButtonText: "Случайный хадис",
            randomPrefix: TopicsRandomPrefix,
            selectPrefix: TopicsSelectPrefix,
            pagePrefix: TopicsPagePrefix,
            noopCallback: TopicsNoopCallback);
    }

    private static InlineKeyboardMarkup BuildTopicsKeyboard(
        string[] topics,
        int pageIndex,
        int pageCount,
        string randomButtonText,
        string randomPrefix,
        string selectPrefix,
        string pagePrefix,
        string noopCallback)
    {
        var rows = new List<InlineKeyboardButton[]>();

        rows.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData(randomButtonText, $"{randomPrefix}1")
        });

        var total = topics.Length;
        var start = pageIndex * TopicsPerPage;
        if (start < 0) start = 0;
        if (start < total)
        {
            var count = Math.Min(TopicsPerPage, total - start);
            for (var i = 0; i < count; i++)
            {
                var topicIndex = start + i;
                var topic = topics[topicIndex];
                var buttonText = TruncateButtonText(topic);
                rows.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData(buttonText, $"{selectPrefix}{topicIndex}")
                });
            }
        }

        var prevPage = pageIndex > 0 ? pageIndex - 1 : 0;
        var nextPage = pageIndex < pageCount - 1 ? pageIndex + 1 : pageCount - 1;

        var prevButton = InlineKeyboardButton.WithCallbackData("<", $"{pagePrefix}{prevPage}");
        var centerButton = InlineKeyboardButton.WithCallbackData($"{pageIndex + 1} / {pageCount}", noopCallback);
        var nextButton = InlineKeyboardButton.WithCallbackData(">", $"{pagePrefix}{nextPage}");

        rows.Add(new[]
        {
            prevButton,
            centerButton,
            nextButton
        });

        return new InlineKeyboardMarkup(rows);
    }

    private static string TruncateButtonText(string text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length <= MaxButtonTextLength)
            return trimmed;

        return trimmed[..(MaxButtonTextLength - 3)].TrimEnd() + "...";
    }
}


