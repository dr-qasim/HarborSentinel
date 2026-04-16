using Renci.SshNet.Common;
using System.IO;
using System.Net.Sockets;

namespace HarborSentinel.AdminConsole.Services;

public static class SshTunnelErrorFormatter
{
    public static string Describe(Exception exception)
    {
        return exception switch
        {
            InvalidOperationException invalidOperation when !string.IsNullOrWhiteSpace(invalidOperation.Message)
                => invalidOperation.Message,
            FileNotFoundException fileNotFound when !string.IsNullOrWhiteSpace(fileNotFound.FileName)
                => $"Файл ключа не найден: {fileNotFound.FileName}.",
            SshAuthenticationException
                => "SSH-аутентификация не прошла. Проверьте логин, пароль или приватный ключ.",
            SshConnectionException sshConnection
                => string.IsNullOrWhiteSpace(sshConnection.Message)
                    ? "Не удалось установить SSH-соединение."
                    : sshConnection.Message,
            SocketException socketException
                => socketException.SocketErrorCode == SocketError.ConnectionRefused
                    ? "SSH-сервер отклонил подключение."
                    : "Не удалось подключиться к SSH-серверу.",
            OperationCanceledException
                => "Операция SSH была отменена.",
            _ when string.IsNullOrWhiteSpace(exception.Message)
                => "Неизвестная ошибка SSH-туннеля.",
            _
                => exception.Message
        };
    }
}
