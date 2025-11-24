using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

    // Запоминаем последний хадис по chatId, чтобы не повторять его подряд
    private static readonly ConcurrentDictionary<long, string?> _lastHadithByChat = new();

    // Сколько тем показываем на одной "странице" списка
    private const int TopicsPerPage = 20;

    public HadithCommandHandler(IHadithProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
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
            if (trimmed.StartsWith("/hadith", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("/hadith_topics", StringComparison.OrdinalIgnoreCase))
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
               "Ас-саляму алейкум!\n\n" +
               "Я бот «Крепость имана». Помогаю напоминаниями из Корана и Сунны и делюсь хадисами по темам.\n\n" +
               "Доступные команды:\n" +
               "/hadith — получить хадис. Можно указать тему: например, /hadith милость, /hadith родители.\n" +
               "/hadith_topics — показать список тем хадисов и полистать их с помощью кнопок.\n" +
               "/about — кратко рассказывает о боте и его командах.\n\n" +
               "Добавляйте меня в учебные, семейные и общие чаты, чтобы участники могли получать благие напоминания прямо в беседе.";

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
            var topics = await _provider.GetAllTopicsAsync(ct).ConfigureAwait(false);

            if (topics.Length == 0)
            {
                await bot.SendMessage(
                    chatId: msg.Chat.Id,
                    text: "Пока нет доступных тем хадисов.",
                    cancellationToken: ct
                ).ConfigureAwait(false);

                return true;
            }

            var pageCount = (topics.Length + TopicsPerPage - 1) / TopicsPerPage;
            var pageIndex = 0;

            var textToSend = BuildTopicsPageText(topics, pageIndex, pageCount);
            var keyboard = BuildTopicsKeyboard(pageIndex, pageCount);

            await bot.SendMessage(
                chatId: msg.Chat.Id,
                text: textToSend,
                replyMarkup: keyboard,
                cancellationToken: ct
            ).ConfigureAwait(false);

            return true;
        }

        // Обработка /hadith (случайный или по теме)
        if (!string.Equals(command, "/hadith", StringComparison.OrdinalIgnoreCase))
            return false;

        var topicQuery = args?.Trim() ?? string.Empty;

        HadithEntry? hadith;
        if (string.IsNullOrWhiteSpace(topicQuery))
        {
            hadith = await _provider.GetRandomAsync(ct).ConfigureAwait(false);
        }
        else
        {
            hadith = await _provider.GetRandomByTopicAsync(topicQuery, ct).ConfigureAwait(false);
        }

        // Простая защита от повтора того же самого хадиса подряд в одном чате
        if (hadith is not null)
        {
            var chatId = msg.Chat.Id;

            if (_lastHadithByChat.TryGetValue(chatId, out var lastId) &&
                !string.IsNullOrEmpty(lastId) &&
                string.Equals(lastId, hadith.Id, StringComparison.Ordinal))
            {
                // Делаем одну дополнительную попытку взять другой хадис
                HadithEntry? secondTry;
                if (string.IsNullOrWhiteSpace(topicQuery))
                    secondTry = await _provider.GetRandomAsync(ct).ConfigureAwait(false);
                else
                    secondTry = await _provider.GetRandomByTopicAsync(topicQuery, ct).ConfigureAwait(false);

                if (secondTry is not null)
                    hadith = secondTry;
            }

            // Запоминаем тот хадис, который в итоге отдаем пользователю
            if (!string.IsNullOrEmpty(hadith.Id))
                _lastHadithByChat[chatId] = hadith.Id;
        }

        if (hadith is null)
        {
            await bot.SendMessage(
                chatId: msg.Chat.Id,
                text: "Пока не нашёл хадис по такой теме. Попробуйте выбрать тему из списка /hadith_topics и использовать часть её названия.",
                cancellationToken: ct
            ).ConfigureAwait(false);

            return true;
        }

        var response =
            "📖 Хадис\n\n" +
            hadith.TextRu + "\n\n" +
            "Источник: " + hadith.Source;

        if (!string.IsNullOrWhiteSpace(hadith.ExplanationRu))
        {
            response += "\n\nПояснение: " + hadith.ExplanationRu;
        }

        await bot.SendMessage(
            chatId: msg.Chat.Id,
            text: response,
            cancellationToken: ct
        ).ConfigureAwait(false);

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

        if (!data.StartsWith("ht:", StringComparison.Ordinal))
            return false;

        if (data == "ht:noop")
            return true;

        var parts = data.Split(':', 2);
        if (parts.Length != 2)
            return false;

        if (!int.TryParse(parts[1], out var pageIndex))
            return false;

        var topics = await _provider.GetAllTopicsAsync(ct).ConfigureAwait(false);
        if (topics.Length == 0)
            return true;

        var pageCount = (topics.Length + TopicsPerPage - 1) / TopicsPerPage;
        if (pageCount <= 0) pageCount = 1;
        if (pageIndex < 0) pageIndex = 0;
        if (pageIndex > pageCount - 1) pageIndex = pageCount - 1;

        var textToSend = BuildTopicsPageText(topics, pageIndex, pageCount);
        var keyboard = BuildTopicsKeyboard(pageIndex, pageCount);

        if (cq.Message is { } msg)
        {
            // обновляем существующее сообщение (меняем текст + клавиатуру)
            await bot.EditMessageText(
                chatId: msg.Chat.Id,
                messageId: msg.Id,
                text: textToSend,
                replyMarkup: keyboard,
                cancellationToken: ct
            ).ConfigureAwait(false);
        }

        // аккуратно отвечаем на callback, чтобы пропал "часик" у пользователя
        await bot.AnswerCallbackQuery(
            callbackQueryId: cq.Id,
            cancellationToken: ct
        ).ConfigureAwait(false);

        return true;
    }

    private static string BuildTopicsPageText(string[] topics, int pageIndex, int pageCount)
    {
        var total = topics.Length;
        var start = pageIndex * TopicsPerPage;
        if (start < 0) start = 0;
        if (start >= total)
            return "Пока нет доступных тем хадисов.";

        var count = Math.Min(TopicsPerPage, total - start);
        var pageTopics = topics.Skip(start).Take(count);

        var sb = new StringBuilder();
        sb.Append("📚 Доступные темы хадисов (страница ");
        sb.Append(pageIndex + 1);
        sb.Append(" из ");
        sb.Append(pageCount);
        sb.AppendLine("):");
        sb.AppendLine();

        foreach (var t in pageTopics)
        {
            sb.Append("• ");
            sb.AppendLine(t);
        }

        sb.AppendLine();
        sb.AppendLine("Чтобы получить хадис по теме, напишите, например:");
        sb.AppendLine("/hadith <часть названия темы>");

        return sb.ToString();
    }

    private static InlineKeyboardMarkup BuildTopicsKeyboard(int pageIndex, int pageCount)
    {
        var prevPage = pageIndex > 0 ? pageIndex - 1 : 0;
        var nextPage = pageIndex < pageCount - 1 ? pageIndex + 1 : pageCount - 1;

        var prevButton = InlineKeyboardButton.WithCallbackData("<", $"ht:{prevPage}");
        var centerButton = InlineKeyboardButton.WithCallbackData($"{pageIndex + 1} / {pageCount}", "ht:noop");
        var nextButton = InlineKeyboardButton.WithCallbackData(">", $"ht:{nextPage}");

        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                prevButton,
                centerButton,
                nextButton
            }
        });
    }
}