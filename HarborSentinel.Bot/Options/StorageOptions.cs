
namespace HarborSentinel.Bot.Options
{
    public sealed class StorageOptions
    {
        /// <summary>Каталог для всех файлов данных (JSONL и flagged выгрузок).</summary>
        public string BasePath { get; set; } = "data";

        /// <summary>Префикс файла дневного лога: messages-YYYY-MM-DD.jsonl</summary>
        public string FilePrefix { get; set; } = "messages";

        /// <summary>Подкаталог в BasePath для выгрузок подозрительных диалогов.</summary>
        public string FlaggedDir { get; set; } = "flagged_lists";

        /// <summary>Сколько записей копить в памяти перед записью на диск.</summary>
        public int BatchSize { get; set; } = 64;

        /// <summary>Ёмкость очереди сообщений на запись (backpressure при всплесках).</summary>
        public int ChannelCapacity { get; set; } = 1000;

        /// <summary>Периодический flush (даже если батч не набрался).</summary>
        public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(1);
    }
}
