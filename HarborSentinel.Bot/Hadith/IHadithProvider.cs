
namespace HarborSentinel.Bot.Hadith;


public interface IHadithProvider
{
    /// <summary>
    /// Получить случайные хадисы по всем темам
    /// </summary>
    Task<HadithEntry?> GetRandomAsync(CancellationToken ct = default);

    /// <summary>
    /// Получить случайный хадис по теме / ключевому слову
    /// Если ничего не найдет, вернет null
    /// </summary>
    Task<HadithEntry?> GetRandomByTopicAsync(string topicQuery, CancellationToken ct = default);

    /// <summary>
    /// Получить список доступных тематических тегов/тем для команды /hadtih_topics
    /// </summary>
    /// <param name="ct"></param>
    /// <returns></returns>
    Task<string[]> GetAllTopicsAsync(CancellationToken ct = default);
}