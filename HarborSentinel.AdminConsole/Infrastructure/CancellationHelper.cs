namespace HarborSentinel.AdminConsole.Infrastructure;

internal static class CancellationHelper
{
    /// <summary>
    /// Отменяет и освобождает текущий CTS, создаёт новый и возвращает его.
    /// Вызывать перед каждым новым запросом, чтобы отменить предыдущий.
    /// </summary>
    public static CancellationTokenSource Replace(ref CancellationTokenSource? field)
    {
        field?.Cancel();
        field?.Dispose();
        field = new CancellationTokenSource();
        return field;
    }

    /// <summary>
    /// Освобождает CTS только если он совпадает с завершённым запросом.
    /// Вызывать в finally-блоке запроса.
    /// </summary>
    public static void Complete(ref CancellationTokenSource? field, CancellationTokenSource completed)
    {
        if (!ReferenceEquals(field, completed))
            return;

        field = null;
        completed.Dispose();
    }
}
