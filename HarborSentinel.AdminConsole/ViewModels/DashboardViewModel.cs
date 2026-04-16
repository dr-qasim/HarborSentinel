using HarborSentinel.AdminConsole.Infrastructure;
using HarborSentinel.AdminConsole.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;

namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class DashboardViewModel : PageViewModel, IAsyncPageInitializable
{
    private readonly IAdminHostClient _adminHostClient;
    private readonly AdminConnectionSettings _connectionSettings;
    private readonly ISshTunnelProfileStore _tunnelProfileStore;
    private readonly ITunnelSecretStore _tunnelSecretStore;
    private readonly ISshTunnelManager _sshTunnelManager;
    private readonly IConnectionSettingsStore _connectionSettingsStore;
    private readonly AsyncRelayCommand _refreshCommand;
    private readonly AsyncRelayCommand _saveConnectionSettingsCommand;
    private readonly AsyncRelayCommand _saveTunnelProfileCommand;
    private readonly AsyncRelayCommand _deleteTunnelProfileCommand;
    private readonly AsyncRelayCommand _connectTunnelCommand;
    private readonly AsyncRelayCommand _disconnectTunnelCommand;
    private readonly AsyncRelayCommand _reconnectTunnelCommand;
    private readonly AsyncRelayCommand _testTunnelCommand;
    private readonly RelayCommand _createTunnelProfileCommand;
    private readonly RelayCommand _browsePrivateKeyCommand;

    private bool _hasLoadedOnce;
    private bool _profilesLoaded;
    private bool _profileSelectionSyncInProgress;
    private string _connectionStatus = "Подключение к AdminHost ещё не выполнялось.";
    private string _hostSummary = "Ожидается локальный AdminHost или доступ через SSH-туннель.";
    private string _runtimeSummary = "Сводка runtime появится после первого успешного запроса.";
    private string _securitySummary = "Локальная привязка, токен API и при необходимости SSH-туннель.";
    private string _lastUpdated = "Никогда";
    private SshTunnelProfileViewModel? _selectedTunnelProfile;
    private string _tunnelStatus = "SSH-туннель не настроен.";
    private string _tunnelStatusHint = "Можно использовать ручной внешний SSH-туннель или встроенный tunnel manager.";
    private string _passwordInput = string.Empty;
    private string _privateKeyPassphraseInput = string.Empty;
    private string _passwordStorageStatus = "Сохранённый пароль: нет";
    private string _privateKeyPassphraseStorageStatus = "Сохранённая passphrase: нет";

    public DashboardViewModel(
        IAdminHostClient adminHostClient,
        AdminConnectionSettings connectionSettings,
        ISshTunnelProfileStore tunnelProfileStore,
        ITunnelSecretStore tunnelSecretStore,
        ISshTunnelManager sshTunnelManager,
        IConnectionSettingsStore connectionSettingsStore)
        : base("Обзор", "Подключение, SSH-туннель, состояние runtime и базовые показатели")
    {
        _adminHostClient = adminHostClient;
        _connectionSettings = connectionSettings;
        _tunnelProfileStore = tunnelProfileStore;
        _tunnelSecretStore = tunnelSecretStore;
        _sshTunnelManager = sshTunnelManager;
        _connectionSettingsStore = connectionSettingsStore;

        _connectionSettings.PropertyChanged += HandleConnectionSettingsChanged;
        _sshTunnelManager.StateChanged += HandleTunnelManagerStateChanged;

        Metrics = new ObservableCollection<DashboardMetricCardViewModel>();
        Warnings = new ObservableCollection<string>();
        TunnelProfiles = new ObservableCollection<SshTunnelProfileViewModel>();
        AuthModes =
        [
            new SshTunnelAuthModeOptionViewModel { Value = SshTunnelAuthMode.Password, Label = "Пароль" },
            new SshTunnelAuthModeOptionViewModel { Value = SshTunnelAuthMode.PrivateKey, Label = "Приватный ключ" }
        ];

        _refreshCommand = new AsyncRelayCommand(RefreshAsync);
        _saveConnectionSettingsCommand = new AsyncRelayCommand(SaveConnectionSettingsAsync);
        _saveTunnelProfileCommand = new AsyncRelayCommand(SaveSelectedTunnelProfileAsync, CanEditSelectedTunnelProfile);
        _deleteTunnelProfileCommand = new AsyncRelayCommand(DeleteSelectedTunnelProfileAsync, CanEditSelectedTunnelProfile);
        _connectTunnelCommand = new AsyncRelayCommand(ConnectTunnelAsync, CanEditSelectedTunnelProfile);
        _disconnectTunnelCommand = new AsyncRelayCommand(DisconnectTunnelAsync, () => _sshTunnelManager.State is not SshTunnelConnectionState.Disconnected);
        _reconnectTunnelCommand = new AsyncRelayCommand(ReconnectTunnelAsync, () => _sshTunnelManager.ActiveProfileId is not null);
        _testTunnelCommand = new AsyncRelayCommand(TestTunnelAsync, CanEditSelectedTunnelProfile);
        _createTunnelProfileCommand = new RelayCommand(_ => CreateTunnelProfile());
        _browsePrivateKeyCommand = new RelayCommand(_ => BrowsePrivateKey(), _ => SelectedTunnelProfile?.AuthMode == SshTunnelAuthMode.PrivateKey);
    }

    public ObservableCollection<DashboardMetricCardViewModel> Metrics { get; }
    public ObservableCollection<string> Warnings { get; }
    public ObservableCollection<SshTunnelProfileViewModel> TunnelProfiles { get; }
    public IReadOnlyList<SshTunnelAuthModeOptionViewModel> AuthModes { get; }

    public string HostUrl
    {
        get => _connectionSettings.BaseUrl;
        set
        {
            _connectionSettings.BaseUrl = value;
            OnPropertyChanged();
        }
    }

    public string ApiToken
    {
        get => _connectionSettings.ApiToken;
        set
        {
            _connectionSettings.ApiToken = value;
            OnPropertyChanged();
        }
    }

    public int RequestTimeoutSeconds
    {
        get => _connectionSettings.RequestTimeoutSeconds;
        set
        {
            _connectionSettings.RequestTimeoutSeconds = value;
            OnPropertyChanged();
        }
    }

    public SshTunnelProfileViewModel? SelectedTunnelProfile
    {
        get => _selectedTunnelProfile;
        set
        {
            if (_selectedTunnelProfile is not null)
                _selectedTunnelProfile.PropertyChanged -= HandleSelectedTunnelProfilePropertyChanged;

            if (!SetProperty(ref _selectedTunnelProfile, value))
                return;

            if (_selectedTunnelProfile is not null)
                _selectedTunnelProfile.PropertyChanged += HandleSelectedTunnelProfilePropertyChanged;

            _passwordInput = string.Empty;
            _privateKeyPassphraseInput = string.Empty;
            OnPropertyChanged(nameof(PasswordInput));
            OnPropertyChanged(nameof(PrivateKeyPassphraseInput));
            OnPropertyChanged(nameof(HasTunnelProfiles));
            OnPropertyChanged(nameof(IsPasswordAuthSelected));
            OnPropertyChanged(nameof(IsPrivateKeyAuthSelected));
            UpdateForwardedBaseUrl();
            UpdateTunnelCommandStates();
            _ = RefreshSelectedProfileSecretStateAsync();
        }
    }

    public string PasswordInput
    {
        get => _passwordInput;
        set => SetProperty(ref _passwordInput, value ?? string.Empty);
    }

    public string PrivateKeyPassphraseInput
    {
        get => _privateKeyPassphraseInput;
        set => SetProperty(ref _privateKeyPassphraseInput, value ?? string.Empty);
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    public string HostSummary
    {
        get => _hostSummary;
        private set => SetProperty(ref _hostSummary, value);
    }

    public string RuntimeSummary
    {
        get => _runtimeSummary;
        private set => SetProperty(ref _runtimeSummary, value);
    }

    public string SecuritySummary
    {
        get => _securitySummary;
        private set => SetProperty(ref _securitySummary, value);
    }

    public string LastUpdated
    {
        get => _lastUpdated;
        private set => SetProperty(ref _lastUpdated, value);
    }

    public string TunnelStatus
    {
        get => _tunnelStatus;
        private set => SetProperty(ref _tunnelStatus, value);
    }

    public string TunnelStatusHint
    {
        get => _tunnelStatusHint;
        private set => SetProperty(ref _tunnelStatusHint, value);
    }

    public string PasswordStorageStatus
    {
        get => _passwordStorageStatus;
        private set => SetProperty(ref _passwordStorageStatus, value);
    }

    public string PrivateKeyPassphraseStorageStatus
    {
        get => _privateKeyPassphraseStorageStatus;
        private set => SetProperty(ref _privateKeyPassphraseStorageStatus, value);
    }

    public bool HasTunnelProfiles => TunnelProfiles.Count > 0;

    public bool IsPasswordAuthSelected => SelectedTunnelProfile?.AuthMode == SshTunnelAuthMode.Password;

    public bool IsPrivateKeyAuthSelected => SelectedTunnelProfile?.AuthMode == SshTunnelAuthMode.PrivateKey;

    public string WarningsHeadline
        => Warnings.Count == 0
            ? "Предупреждения: нет"
            : $"Предупреждения: {Warnings.Count}";

    public AsyncRelayCommand RefreshCommand => _refreshCommand;
    public AsyncRelayCommand SaveConnectionSettingsCommand => _saveConnectionSettingsCommand;
    public AsyncRelayCommand SaveTunnelProfileCommand => _saveTunnelProfileCommand;
    public AsyncRelayCommand DeleteTunnelProfileCommand => _deleteTunnelProfileCommand;
    public AsyncRelayCommand ConnectTunnelCommand => _connectTunnelCommand;
    public AsyncRelayCommand DisconnectTunnelCommand => _disconnectTunnelCommand;
    public AsyncRelayCommand ReconnectTunnelCommand => _reconnectTunnelCommand;
    public AsyncRelayCommand TestTunnelCommand => _testTunnelCommand;
    public RelayCommand CreateTunnelProfileCommand => _createTunnelProfileCommand;
    public RelayCommand BrowsePrivateKeyCommand => _browsePrivateKeyCommand;

    public async Task InitializeAsync()
    {
        await LoadConnectionSettingsAsync().ConfigureAwait(true);
        await EnsureTunnelProfilesLoadedAsync().ConfigureAwait(true);

        if (!_hasLoadedOnce)
        {
            await TryAutoConnectAsync().ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    private async Task LoadConnectionSettingsAsync()
    {
        try
        {
            var doc = await _connectionSettingsStore.LoadAsync(CancellationToken.None).ConfigureAwait(true);

            if (!string.IsNullOrWhiteSpace(doc.BaseUrl))
                _connectionSettings.BaseUrl = doc.BaseUrl;
            if (!string.IsNullOrWhiteSpace(doc.ApiToken))
                _connectionSettings.ApiToken = doc.ApiToken;
            if (doc.RequestTimeoutSeconds.HasValue)
                _connectionSettings.RequestTimeoutSeconds = doc.RequestTimeoutSeconds.Value;
        }
        catch
        {
            // Не удалось загрузить настройки — продолжаем со значениями из appsettings
        }
    }

    private async Task SaveConnectionSettingsAsync()
    {
        try
        {
            var doc = new ConnectionSettingsDocument
            {
                BaseUrl = HostUrl,
                ApiToken = ApiToken,
                RequestTimeoutSeconds = RequestTimeoutSeconds
            };
            await _connectionSettingsStore.SaveAsync(doc, CancellationToken.None).ConfigureAwait(true);
            ConnectionStatus = "Настройки подключения сохранены.";
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Не удалось сохранить настройки: {AdminHostErrorFormatter.Describe(ex)}";
        }
    }

    private async Task RefreshAsync()
    {
        ConnectionStatus = "Выполняется запрос к AdminHost...";

        try
        {
            var snapshot = await _adminHostClient
                .GetDashboardSnapshotAsync(
                    HostUrl,
                    ApiToken,
                    _connectionSettings.RequestTimeout,
                    CancellationToken.None)
                .ConfigureAwait(true);

            Metrics.Clear();
            foreach (var metric in snapshot.Metrics)
            {
                Metrics.Add(new DashboardMetricCardViewModel
                {
                    Label = metric.Label,
                    Value = metric.Value,
                    Hint = metric.Hint
                });
            }

            Warnings.Clear();
            foreach (var warning in snapshot.Warnings)
                Warnings.Add(warning);

            HostSummary = $"{snapshot.Product} | версия {snapshot.Version} | среда {TranslateEnvironment(snapshot.Environment)} | {snapshot.Runtime}";
            RuntimeSummary = $"База: {snapshot.DatabasePath} | Хранилище: {snapshot.StorageBasePath}";
            SecuritySummary = snapshot.ApiTokenRequired
                ? $"Адрес: {snapshot.ListenAddress} | требуется заголовок X-Admin-Token"
                : $"Адрес: {snapshot.ListenAddress} | токен не настроен";
            LastUpdated = snapshot.GeneratedAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
            ConnectionStatus = snapshot.DatabaseExists
                ? "Подключение установлено. Сводка runtime обновлена."
                : "Подключение установлено, но runtime-база данных отсутствует.";
            _hasLoadedOnce = true;

            OnPropertyChanged(nameof(WarningsHeadline));
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Ошибка подключения: {AdminHostErrorFormatter.Describe(ex)}";
            HostSummary = "AdminHost недоступен по указанному адресу.";
            RuntimeSummary = "Проверьте локальную привязку, SSH-туннель и настройки путей runtime.";
            SecuritySummary = "Настольный клиент выполняет только операции чтения.";
            Metrics.Clear();
            Warnings.Clear();
            Warnings.Add("Не удалось получить данные обзора. Убедитесь, что AdminHost запущен и доступен по указанному адресу.");
            OnPropertyChanged(nameof(WarningsHeadline));
        }
    }

    private async Task SaveSelectedTunnelProfileAsync()
    {
        await RunTunnelActionAsync(
            async () =>
            {
                if (SelectedTunnelProfile is null)
                    return;

                ValidateSelectedProfile(SelectedTunnelProfile);

                if (SelectedTunnelProfile.AutoConnectOnStartup)
                {
                    foreach (var profile in TunnelProfiles.Where(profile => !ReferenceEquals(profile, SelectedTunnelProfile)))
                        profile.AutoConnectOnStartup = false;
                }

                await PersistProfilesAsync().ConfigureAwait(true);

                if (!string.IsNullOrWhiteSpace(PasswordInput))
                {
                    await _tunnelSecretStore
                        .SaveSecretAsync(SelectedTunnelProfile.ProfileId, SshTunnelSecretKind.Password, PasswordInput, CancellationToken.None)
                        .ConfigureAwait(true);
                    PasswordInput = string.Empty;
                }

                if (!string.IsNullOrWhiteSpace(PrivateKeyPassphraseInput))
                {
                    await _tunnelSecretStore
                        .SaveSecretAsync(SelectedTunnelProfile.ProfileId, SshTunnelSecretKind.PrivateKeyPassphrase, PrivateKeyPassphraseInput, CancellationToken.None)
                        .ConfigureAwait(true);
                    PrivateKeyPassphraseInput = string.Empty;
                }

                await RefreshSelectedProfileSecretStateAsync().ConfigureAwait(true);
                TunnelStatus = $"Профиль \"{SelectedTunnelProfile.ProfileName}\" сохранён.";
                TunnelStatusHint = "Профиль доступен для автоподключения и ручного запуска туннеля.";
            },
            "Не удалось сохранить SSH-профиль.").ConfigureAwait(true);
    }

    private async Task DeleteSelectedTunnelProfileAsync()
    {
        await RunTunnelActionAsync(
            async () =>
            {
                if (SelectedTunnelProfile is null)
                    return;

                var profileToDelete = SelectedTunnelProfile;
                if (_sshTunnelManager.ActiveProfileId == profileToDelete.ProfileId)
                    await _sshTunnelManager.DisconnectAsync(CancellationToken.None).ConfigureAwait(true);

                TunnelProfiles.Remove(profileToDelete);
                await _tunnelSecretStore.DeleteSecretAsync(profileToDelete.ProfileId, SshTunnelSecretKind.Password, CancellationToken.None).ConfigureAwait(true);
                await _tunnelSecretStore.DeleteSecretAsync(profileToDelete.ProfileId, SshTunnelSecretKind.PrivateKeyPassphrase, CancellationToken.None).ConfigureAwait(true);

                if (TunnelProfiles.Count == 0)
                    TunnelProfiles.Add(CreateDefaultProfile());

                SelectedTunnelProfile = TunnelProfiles.FirstOrDefault();
                await PersistProfilesAsync().ConfigureAwait(true);
                TunnelStatus = $"Профиль \"{profileToDelete.ProfileName}\" удалён.";
                TunnelStatusHint = "При необходимости создайте новый профиль или используйте внешний SSH-туннель.";
            },
            "Не удалось удалить SSH-профиль.").ConfigureAwait(true);
    }

    private async Task ConnectTunnelAsync()
    {
        await RunTunnelActionAsync(
            async () =>
            {
                if (SelectedTunnelProfile is null)
                    return;

                ValidateSelectedProfile(SelectedTunnelProfile);

                var connectRequest = await BuildConnectRequestAsync(SelectedTunnelProfile).ConfigureAwait(true);
                await _sshTunnelManager.ConnectAsync(connectRequest, CancellationToken.None).ConfigureAwait(true);

                UpdateForwardedBaseUrl();
                TunnelStatusHint = "Туннель поднят приложением. После обрыва будет предпринята попытка переподключения.";
                await RefreshAsync().ConfigureAwait(true);
            },
            "Не удалось поднять SSH-туннель.").ConfigureAwait(true);
    }

    private async Task DisconnectTunnelAsync()
    {
        await RunTunnelActionAsync(
            async () =>
            {
                await _sshTunnelManager.DisconnectAsync(CancellationToken.None).ConfigureAwait(true);
                TunnelStatusHint = "При необходимости можно снова поднять встроенный туннель или использовать внешний ssh -L.";
            },
            "Не удалось отключить SSH-туннель.").ConfigureAwait(true);
    }

    private async Task ReconnectTunnelAsync()
    {
        await RunTunnelActionAsync(
            async () =>
            {
                await _sshTunnelManager.ReconnectAsync(CancellationToken.None).ConfigureAwait(true);
                await RefreshAsync().ConfigureAwait(true);
            },
            "Не удалось переподключить SSH-туннель.").ConfigureAwait(true);
    }

    private async Task TestTunnelAsync()
    {
        await RunTunnelActionAsync(
            async () =>
            {
                if (SelectedTunnelProfile is null)
                    return;

                ValidateSelectedProfile(SelectedTunnelProfile);
                var connectRequest = await BuildConnectRequestAsync(SelectedTunnelProfile).ConfigureAwait(true);
                await _sshTunnelManager.TestConnectionAsync(connectRequest, CancellationToken.None).ConfigureAwait(true);
                TunnelStatusHint = "SSH-доступ подтверждён. Для начала работы с AdminHost нужно нажать «Подключить» или использовать внешний туннель.";
            },
            "Не удалось проверить SSH-подключение.").ConfigureAwait(true);
    }

    private void CreateTunnelProfile()
    {
        var profile = CreateDefaultProfile();
        TunnelProfiles.Add(profile);
        SelectedTunnelProfile = profile;
        TunnelStatus = "Создан новый профиль SSH-туннеля. Заполните поля и сохраните профиль.";
        TunnelStatusHint = "Профиль хранит только host/port/user/режим и forwarding. Пароль сохраняется отдельно через DPAPI.";
    }

    private void BrowsePrivateKey()
    {
        if (SelectedTunnelProfile is null)
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Выберите приватный SSH-ключ",
            Filter = "OpenSSH private key|id_*;*.pem;*.key|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
            return;

        SelectedTunnelProfile.PrivateKeyPath = dialog.FileName;
    }

    private async Task EnsureTunnelProfilesLoadedAsync()
    {
        if (_profilesLoaded)
            return;

        _profilesLoaded = true;
        var document = await _tunnelProfileStore.LoadAsync(CancellationToken.None).ConfigureAwait(true);

        TunnelProfiles.Clear();
        foreach (var profile in document.Profiles.OrderBy(static item => item.ProfileName, StringComparer.CurrentCultureIgnoreCase))
            TunnelProfiles.Add(SshTunnelProfileViewModel.FromRecord(profile));

        if (TunnelProfiles.Count == 0)
            TunnelProfiles.Add(CreateDefaultProfile());

        SelectedTunnelProfile = TunnelProfiles.FirstOrDefault(profile => profile.ProfileId == document.LastSelectedProfileId)
            ?? TunnelProfiles.FirstOrDefault();

        UpdateTunnelCommandStates();
    }

    private async Task TryAutoConnectAsync()
    {
        var autoProfile = TunnelProfiles.FirstOrDefault(static profile => profile.AutoConnectOnStartup);
        if (autoProfile is null)
            return;

        SelectedTunnelProfile = autoProfile;
        try
        {
            TunnelStatus = $"Автоподключение к {autoProfile.ProfileName}...";
            var request = await BuildConnectRequestAsync(autoProfile).ConfigureAwait(true);
            await _sshTunnelManager.ConnectAsync(request, CancellationToken.None).ConfigureAwait(true);
            UpdateForwardedBaseUrl();
            TunnelStatusHint = "Автоподключение выполнено. Приложение использует встроенный SSH-туннель.";
        }
        catch (Exception ex)
        {
            TunnelStatus = $"Автоподключение не удалось: {SshTunnelErrorFormatter.Describe(ex)}";
            TunnelStatusHint = "Проверьте профиль SSH, пароль или ключ. Ручной внешний SSH-туннель по-прежнему поддерживается.";
        }
    }

    private async Task RunTunnelActionAsync(Func<Task> action, string operatorMessagePrefix)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            TunnelStatus = $"{operatorMessagePrefix} {SshTunnelErrorFormatter.Describe(ex)}";
            TunnelStatusHint = "Профиль сохранён локально только если операция завершилась успешно. Внешний ssh -L по-прежнему можно использовать как fallback.";
        }
    }

    private async Task<SshTunnelConnectRequest> BuildConnectRequestAsync(SshTunnelProfileViewModel profile)
    {
        var password = profile.AuthMode == SshTunnelAuthMode.Password
            ? await ResolveSecretAsync(profile.ProfileId, SshTunnelSecretKind.Password, PasswordInput).ConfigureAwait(true)
            : null;
        var passphrase = profile.AuthMode == SshTunnelAuthMode.PrivateKey
            ? await ResolveSecretAsync(profile.ProfileId, SshTunnelSecretKind.PrivateKeyPassphrase, PrivateKeyPassphraseInput, allowBlank: true).ConfigureAwait(true)
            : null;

        return new SshTunnelConnectRequest
        {
            Profile = profile.ToRecord(),
            Password = password,
            PrivateKeyPassphrase = passphrase
        };
    }

    private async Task<string?> ResolveSecretAsync(string profileId, SshTunnelSecretKind kind, string currentInput, bool allowBlank = false)
    {
        if (!string.IsNullOrWhiteSpace(currentInput))
            return currentInput;

        var storedSecret = await _tunnelSecretStore.GetSecretAsync(profileId, kind, CancellationToken.None).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(storedSecret))
            return storedSecret;

        if (allowBlank)
            return null;

        throw new InvalidOperationException(kind == SshTunnelSecretKind.Password
            ? "Введите SSH-пароль или сохраните его в профиле."
            : "Введите passphrase для ключа или сохраните её в профиле.");
    }

    private async Task PersistProfilesAsync()
    {
        var document = new SshTunnelProfilesDocument
        {
            LastSelectedProfileId = SelectedTunnelProfile?.ProfileId,
            Profiles = TunnelProfiles.Select(static profile => profile.ToRecord()).ToList()
        };

        await _tunnelProfileStore.SaveAsync(document, CancellationToken.None).ConfigureAwait(true);
    }

    private async Task RefreshSelectedProfileSecretStateAsync()
    {
        if (SelectedTunnelProfile is null)
        {
            PasswordStorageStatus = "Сохранённый пароль: нет";
            PrivateKeyPassphraseStorageStatus = "Сохранённая passphrase: нет";
            return;
        }

        var hasPassword = await _tunnelSecretStore
            .HasSecretAsync(SelectedTunnelProfile.ProfileId, SshTunnelSecretKind.Password, CancellationToken.None)
            .ConfigureAwait(true);
        var hasPassphrase = await _tunnelSecretStore
            .HasSecretAsync(SelectedTunnelProfile.ProfileId, SshTunnelSecretKind.PrivateKeyPassphrase, CancellationToken.None)
            .ConfigureAwait(true);

        PasswordStorageStatus = hasPassword
            ? "Сохранённый пароль: есть"
            : "Сохранённый пароль: нет";
        PrivateKeyPassphraseStorageStatus = hasPassphrase
            ? "Сохранённая passphrase: есть"
            : "Сохранённая passphrase: нет";
    }

    private void HandleConnectionSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AdminConnectionSettings.BaseUrl))
            OnPropertyChanged(nameof(HostUrl));
        else if (e.PropertyName is nameof(AdminConnectionSettings.ApiToken))
            OnPropertyChanged(nameof(ApiToken));
        else if (e.PropertyName is nameof(AdminConnectionSettings.RequestTimeoutSeconds))
            OnPropertyChanged(nameof(RequestTimeoutSeconds));
    }

    private void HandleSelectedTunnelProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_profileSelectionSyncInProgress)
            return;

        if (e.PropertyName is nameof(SshTunnelProfileViewModel.AuthMode))
        {
            OnPropertyChanged(nameof(IsPasswordAuthSelected));
            OnPropertyChanged(nameof(IsPrivateKeyAuthSelected));
        }

        if (e.PropertyName is nameof(SshTunnelProfileViewModel.LocalPort))
            UpdateForwardedBaseUrl();

        UpdateTunnelCommandStates();
    }

    private void HandleTunnelManagerStateChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(() => HandleTunnelManagerStateChanged(sender, e));
            return;
        }

        TunnelStatus = _sshTunnelManager.StatusText;
        if (!string.IsNullOrWhiteSpace(_sshTunnelManager.LastFailureReason))
            TunnelStatusHint = _sshTunnelManager.LastFailureReason!;

        UpdateTunnelCommandStates();
    }

    private void UpdateForwardedBaseUrl()
    {
        if (SelectedTunnelProfile is null)
            return;

        _profileSelectionSyncInProgress = true;
        try
        {
            _connectionSettings.BaseUrl = $"http://127.0.0.1:{SelectedTunnelProfile.LocalPort}";
        }
        finally
        {
            _profileSelectionSyncInProgress = false;
        }

        OnPropertyChanged(nameof(HostUrl));
    }

    private void UpdateTunnelCommandStates()
    {
        _saveTunnelProfileCommand.RaiseCanExecuteChanged();
        _deleteTunnelProfileCommand.RaiseCanExecuteChanged();
        _connectTunnelCommand.RaiseCanExecuteChanged();
        _disconnectTunnelCommand.RaiseCanExecuteChanged();
        _reconnectTunnelCommand.RaiseCanExecuteChanged();
        _testTunnelCommand.RaiseCanExecuteChanged();
        _browsePrivateKeyCommand.RaiseCanExecuteChanged();
    }

    private bool CanEditSelectedTunnelProfile()
        => SelectedTunnelProfile is not null;

    private static void ValidateSelectedProfile(SshTunnelProfileViewModel profile)
    {
        if (string.IsNullOrWhiteSpace(profile.ProfileName))
            throw new InvalidOperationException("Укажите имя SSH-профиля.");
        if (string.IsNullOrWhiteSpace(profile.Host))
            throw new InvalidOperationException("Укажите SSH host.");
        if (profile.Port is < 1 or > 65535)
            throw new InvalidOperationException("SSH port должен быть в диапазоне 1-65535.");
        if (string.IsNullOrWhiteSpace(profile.Username))
            throw new InvalidOperationException("Укажите SSH username.");
        if (profile.LocalPort is < 1 or > 65535)
            throw new InvalidOperationException("Локальный порт должен быть в диапазоне 1-65535.");
        if (string.IsNullOrWhiteSpace(profile.RemoteHost))
            throw new InvalidOperationException("Укажите удалённый host для forwarding.");
        if (profile.RemotePort is < 1 or > 65535)
            throw new InvalidOperationException("Удалённый порт должен быть в диапазоне 1-65535.");
        if (profile.AuthMode == SshTunnelAuthMode.PrivateKey && string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
            throw new InvalidOperationException("Для режима PrivateKey укажите путь к приватному ключу.");
    }

    private SshTunnelProfileViewModel CreateDefaultProfile()
    {
        return new SshTunnelProfileViewModel
        {
            ProfileName = "Основной VPS",
            LocalPort = TryExtractPort(_connectionSettings.BaseUrl) ?? 58421,
            RemoteHost = "127.0.0.1",
            RemotePort = 58421,
            Port = 22,
            AuthMode = SshTunnelAuthMode.Password
        };
    }

    private static int? TryExtractPort(string? baseUrl)
    {
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Port : null;
    }

    private static string TranslateEnvironment(string environment)
    {
        return environment switch
        {
            "Development" => "Разработка",
            "Production" => "Продакшен",
            "Staging" => "Тестовый стенд",
            _ => environment
        };
    }
}
