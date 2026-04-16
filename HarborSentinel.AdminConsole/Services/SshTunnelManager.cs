using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Threading;

namespace HarborSentinel.AdminConsole.Services;

public sealed class SshTunnelManager : ISshTunnelManager
{
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(10);

    private readonly ILogger<SshTunnelManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _healthTimer;

    private SshClient? _client;
    private ForwardedPortLocal? _forwardedPort;
    private SshTunnelConnectRequest? _lastRequest;
    private bool _desiredConnected;
    private bool _disposed;
    private int _reconnectAttempts;
    private SshTunnelConnectionState _state = SshTunnelConnectionState.Disconnected;
    private string _statusText = "SSH-туннель не подключён.";
    private string? _activeProfileId;
    private string? _lastFailureReason;

    public SshTunnelManager(ILogger<SshTunnelManager> logger)
    {
        _logger = logger;
        _healthTimer = new Timer(OnHealthTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event EventHandler? StateChanged;

    public SshTunnelConnectionState State => _state;

    public string StatusText => _statusText;

    public string? ActiveProfileId => _activeProfileId;

    public string? LastFailureReason => _lastFailureReason;

    public async Task ConnectAsync(SshTunnelConnectRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            _desiredConnected = true;
            _reconnectAttempts = 0;
            SetState(SshTunnelConnectionState.Connecting, $"Устанавливается SSH-туннель к {request.Profile.Host}:{request.Profile.Port}...");

            await DisconnectCoreAsync().ConfigureAwait(false);
            await ConnectCoreAsync(request, ct).ConfigureAwait(false);

            _lastRequest = request;
            _activeProfileId = request.Profile.ProfileId;
            _lastFailureReason = null;
            _desiredConnected = true;
            _healthTimer.Change(HealthCheckInterval, HealthCheckInterval);
            SetState(SshTunnelConnectionState.Connected, BuildConnectedMessage(request.Profile));
        }
        catch
        {
            _desiredConnected = false;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _desiredConnected = false;
            _lastRequest = null;
            _activeProfileId = null;
            _reconnectAttempts = 0;
            _healthTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            await DisconnectCoreAsync().ConfigureAwait(false);
            _lastFailureReason = null;
            SetState(SshTunnelConnectionState.Disconnected, "SSH-туннель отключён.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReconnectAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_lastRequest is null)
                throw new InvalidOperationException("Нет последнего SSH-профиля для переподключения.");

            _desiredConnected = true;
            _reconnectAttempts = 0;
            SetState(SshTunnelConnectionState.Reconnecting, $"Выполняется переподключение к {_lastRequest.Profile.Host}:{_lastRequest.Profile.Port}...");
            await DisconnectCoreAsync().ConfigureAwait(false);
            await ConnectCoreAsync(_lastRequest, ct).ConfigureAwait(false);
            _lastFailureReason = null;
            _healthTimer.Change(HealthCheckInterval, HealthCheckInterval);
            SetState(SshTunnelConnectionState.Connected, BuildConnectedMessage(_lastRequest.Profile));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TestConnectionAsync(SshTunnelConnectRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var previousState = _state;
            SetState(previousState, $"Проверяется SSH-доступ к {request.Profile.Host}:{request.Profile.Port}...");

            using var client = CreateClient(request);
            client.Connect();
            client.Disconnect();

            _lastFailureReason = null;
            SetState(previousState, $"SSH-доступ к {request.Profile.Host}:{request.Profile.Port} подтверждён.");
        }
        catch (Exception ex)
        {
            var nextState = _state == SshTunnelConnectionState.Connected
                ? SshTunnelConnectionState.Connected
                : SshTunnelConnectionState.Failed;

            _lastFailureReason = ex.Message;
            SetState(
                nextState,
                nextState == SshTunnelConnectionState.Connected
                    ? $"Активный туннель сохранён, но тест завершился ошибкой: {SshTunnelErrorFormatter.Describe(ex)}"
                    : SshTunnelErrorFormatter.Describe(ex));
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _healthTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _healthTimer.Dispose();

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async void OnHealthTimerTick(object? state)
    {
        try
        {
            if (!_desiredConnected || _lastRequest is null)
                return;

            if (_client?.IsConnected == true && _forwardedPort?.IsStarted == true)
                return;

            await TryReconnectFromTimerAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SSH tunnel reconnect attempt failed.");
        }
    }

    private async Task TryReconnectFromTimerAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_desiredConnected || _lastRequest is null)
                return;

            _reconnectAttempts++;
            SetState(SshTunnelConnectionState.Reconnecting, $"SSH-туннель переподключается. Попытка {_reconnectAttempts}...");

            await DisconnectCoreAsync().ConfigureAwait(false);
            await ConnectCoreAsync(_lastRequest, CancellationToken.None).ConfigureAwait(false);

            _lastFailureReason = null;
            _healthTimer.Change(HealthCheckInterval, HealthCheckInterval);
            SetState(SshTunnelConnectionState.Connected, BuildConnectedMessage(_lastRequest.Profile));
        }
        catch (Exception ex)
        {
            _lastFailureReason = ex.Message;
            SetState(SshTunnelConnectionState.Failed, $"SSH-туннель разорван: {SshTunnelErrorFormatter.Describe(ex)}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task ConnectCoreAsync(SshTunnelConnectRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateProfile(request.Profile);
        EnsureLocalPortAvailable(request.Profile.LocalPort);

        var client = CreateClient(request);
        try
        {
            client.Connect();

            var forwardedPort = new ForwardedPortLocal(
                "127.0.0.1",
                (uint)request.Profile.LocalPort,
                request.Profile.RemoteHost,
                (uint)request.Profile.RemotePort);

            forwardedPort.Exception += (_, args) =>
            {
                _logger.LogWarning(args.Exception, "SSH forwarded port reported an exception.");
                _lastFailureReason = args.Exception.Message;
                SetState(SshTunnelConnectionState.Failed, $"SSH-туннель прерван: {SshTunnelErrorFormatter.Describe(args.Exception)}");
            };

            client.ErrorOccurred += (_, args) =>
            {
                _logger.LogWarning(args.Exception, "SSH client reported an exception.");
                _lastFailureReason = args.Exception.Message;
                SetState(SshTunnelConnectionState.Failed, $"SSH-сессия завершилась ошибкой: {SshTunnelErrorFormatter.Describe(args.Exception)}");
            };

            client.AddForwardedPort(forwardedPort);
            forwardedPort.Start();

            _client = client;
            _forwardedPort = forwardedPort;
        }
        catch (Exception ex)
        {
            client.Dispose();
            _lastFailureReason = ex.Message;
            SetState(SshTunnelConnectionState.Failed, SshTunnelErrorFormatter.Describe(ex));
            throw;
        }

        return Task.CompletedTask;
    }

    private Task DisconnectCoreAsync()
    {
        try
        {
            _forwardedPort?.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SSH forwarded port stop failed.");
        }

        try
        {
            _client?.Disconnect();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SSH client disconnect failed.");
        }

        _forwardedPort?.Dispose();
        _client?.Dispose();
        _forwardedPort = null;
        _client = null;

        return Task.CompletedTask;
    }

    private static SshClient CreateClient(SshTunnelConnectRequest request)
    {
        var profile = request.Profile;
        var connectionInfo = profile.AuthMode switch
        {
            SshTunnelAuthMode.Password => new ConnectionInfo(
                profile.Host,
                profile.Port,
                profile.Username,
                new PasswordAuthenticationMethod(profile.Username, RequirePassword(request.Password))),
            SshTunnelAuthMode.PrivateKey => new ConnectionInfo(
                profile.Host,
                profile.Port,
                profile.Username,
                new PrivateKeyAuthenticationMethod(profile.Username, CreatePrivateKeyFile(profile.PrivateKeyPath, request.PrivateKeyPassphrase))),
            _ => throw new InvalidOperationException("Неизвестный режим аутентификации SSH.")
        };

        connectionInfo.Timeout = TimeSpan.FromSeconds(10);
        return new SshClient(connectionInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15)
        };
    }

    private static PrivateKeyFile CreatePrivateKeyFile(string privateKeyPath, string? passphrase)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPath) || !File.Exists(privateKeyPath))
            throw new FileNotFoundException("Файл приватного ключа не найден.", privateKeyPath);

        return string.IsNullOrWhiteSpace(passphrase)
            ? new PrivateKeyFile(privateKeyPath)
            : new PrivateKeyFile(privateKeyPath, passphrase);
    }

    private static string RequirePassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Пароль SSH не задан.");

        return password;
    }

    private static void ValidateProfile(SshTunnelProfileRecord profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Host))
            throw new InvalidOperationException("Не указан SSH host.");
        if (profile.Port is < 1 or > 65535)
            throw new InvalidOperationException("SSH port должен быть в диапазоне 1-65535.");
        if (string.IsNullOrWhiteSpace(profile.Username))
            throw new InvalidOperationException("Не указан SSH username.");
        if (profile.LocalPort is < 1 or > 65535)
            throw new InvalidOperationException("Локальный порт forwarding должен быть в диапазоне 1-65535.");
        if (string.IsNullOrWhiteSpace(profile.RemoteHost))
            throw new InvalidOperationException("Не указан удалённый host для forwarding.");
        if (profile.RemotePort is < 1 or > 65535)
            throw new InvalidOperationException("Удалённый порт forwarding должен быть в диапазоне 1-65535.");
        if (profile.AuthMode is SshTunnelAuthMode.PrivateKey &&
            string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
        {
            throw new InvalidOperationException("Для режима PrivateKey нужно указать путь к приватному ключу.");
        }
    }

    private void EnsureLocalPortAvailable(int localPort)
    {
        if (_forwardedPort?.IsStarted == true && _lastRequest?.Profile.LocalPort == localPort)
            return;

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, localPort);
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"Локальный порт {localPort} уже занят.", ex);
        }
        finally
        {
            listener?.Stop();
        }
    }

    private void SetState(SshTunnelConnectionState state, string statusText)
    {
        _state = state;
        _statusText = statusText;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string BuildConnectedMessage(SshTunnelProfileRecord profile)
        => $"SSH-туннель активен: 127.0.0.1:{profile.LocalPort} -> {profile.RemoteHost}:{profile.RemotePort} через {profile.Username}@{profile.Host}:{profile.Port}.";

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
