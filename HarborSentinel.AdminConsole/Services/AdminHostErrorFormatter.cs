using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace HarborSentinel.AdminConsole.Services;

public static class AdminHostErrorFormatter
{
    public static string Describe(Exception exception)
    {
        if (exception is HttpRequestException httpException)
            return DescribeHttpException(httpException);

        if (exception is OperationCanceledException)
            return "Время ожидания ответа AdminHost истекло. Проверьте SSH-туннель и повторите запрос.";

        if (exception is InvalidOperationException invalidOperation &&
            !string.IsNullOrWhiteSpace(invalidOperation.Message))
            return invalidOperation.Message;

        return string.IsNullOrWhiteSpace(exception.Message)
            ? "Неизвестная ошибка при обращении к AdminHost."
            : exception.Message;
    }

    private static string DescribeHttpException(HttpRequestException exception)
    {
        if (exception.StatusCode is HttpStatusCode.Unauthorized)
            return "AdminHost отклонил запрос. Проверьте API-токен.";

        if (exception.StatusCode is HttpStatusCode.Forbidden)
            return "AdminHost отказал в доступе к этому запросу.";

        if (exception.StatusCode is HttpStatusCode.NotFound)
            return !string.IsNullOrWhiteSpace(exception.Message)
                ? exception.Message
                : "Запрошенный ресурс не найден в текущей области AdminHost.";

        if (exception.StatusCode is HttpStatusCode.BadRequest)
            return !string.IsNullOrWhiteSpace(exception.Message)
                ? exception.Message
                : "AdminHost отклонил запрос из-за некорректных параметров.";

        if (exception.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
            return "AdminHost временно недоступен. Повторите запрос позже.";

        if (exception.InnerException is SocketException)
            return "Не удалось подключиться к AdminHost. Проверьте адрес и SSH-туннель.";

        return string.IsNullOrWhiteSpace(exception.Message)
            ? "Ошибка HTTP при обращении к AdminHost."
            : exception.Message;
    }
}
