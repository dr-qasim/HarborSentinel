using HarborSentinel.AdminConsole.Infrastructure;
using HarborSentinel.AdminConsole.Options;
using Microsoft.Extensions.Options;

namespace HarborSentinel.AdminConsole.Services;

public sealed class AdminConnectionSettings : ObservableObject
{
    private string _baseUrl;
    private string _apiToken;
    private int _requestTimeoutSeconds;
    private string _operatorId;

    public AdminConnectionSettings(IOptions<AdminConsoleOptions> options)
    {
        var config = options.Value;
        _baseUrl = string.IsNullOrWhiteSpace(config.BaseUrl)
            ? "http://127.0.0.1:58421"
            : config.BaseUrl.Trim();
        _apiToken = config.ApiToken ?? string.Empty;
        _requestTimeoutSeconds = Math.Clamp(config.RequestTimeoutSeconds, 3, 60);
        _operatorId = string.IsNullOrWhiteSpace(config.OperatorId)
            ? $"{Environment.UserName}@{Environment.MachineName}".ToLowerInvariant()
            : config.OperatorId.Trim();
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set => SetProperty(
            ref _baseUrl,
            string.IsNullOrWhiteSpace(value) ? "http://127.0.0.1:58421" : value.Trim());
    }

    public string ApiToken
    {
        get => _apiToken;
        set => SetProperty(ref _apiToken, value?.Trim() ?? string.Empty);
    }

    public int RequestTimeoutSeconds
    {
        get => _requestTimeoutSeconds;
        set => SetProperty(ref _requestTimeoutSeconds, Math.Clamp(value, 3, 60));
    }

    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(_requestTimeoutSeconds);

    public string OperatorId
    {
        get => _operatorId;
        set => SetProperty(
            ref _operatorId,
            string.IsNullOrWhiteSpace(value)
                ? $"{Environment.UserName}@{Environment.MachineName}".ToLowerInvariant()
                : value.Trim());
    }
}
