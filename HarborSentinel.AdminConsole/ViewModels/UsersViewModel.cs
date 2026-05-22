using HarborSentinel.AdminConsole.Infrastructure;
using HarborSentinel.AdminConsole.Services;
using HarborSentinel.AdminContracts.Users;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;

namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class UsersViewModel : PageViewModel, IAsyncPageInitializable
{
    private const int PageSize = 100;

    private readonly IAdminHostClient _adminHostClient;
    private readonly AdminConnectionSettings _connectionSettings;
    private readonly AsyncRelayCommand _refreshCommand;
    private readonly AsyncRelayCommand _refreshDetailCommand;
    private readonly AsyncRelayCommand _nextPageCommand;
    private readonly AsyncRelayCommand _prevPageCommand;

    private CancellationTokenSource? _listLoadCts;
    private CancellationTokenSource? _detailLoadCts;
    private bool _hasLoadedOnce;
    private int _currentPage = 1;
    private int _totalPages = 1;
    private ChatFilterOptionViewModel<AdminUserListSourceFilterDto>? _selectedSourceFilter;
    private UserListItemViewModel? _selectedRow;
    private UserDetailViewModel? _selectedUserDetail;
    private string _searchText = string.Empty;
    private string _connectionStatus = "Список пользователей ещё не загружался.";
    private string _scopeSummary = "Показываются текущие и исторические пользователи. Контур текущего бота будет отмечен, если это поддерживается схемой runtime.";
    private string _countsSummary = "Сводка пока недоступна.";
    private string _paginationSummary = "Страница 1 из 1";
    private string _lastUpdated = "Никогда";
    private string _detailStatus = "Выберите пользователя в списке, чтобы открыть карточку.";
    private string _detailLastUpdated = "Никогда";

    public UsersViewModel(IAdminHostClient adminHostClient, AdminConnectionSettings connectionSettings)
        : base("Пользователи", "Межчатовый обзор пользователей, private-start и связанный контекст")
    {
        _adminHostClient = adminHostClient;
        _connectionSettings = connectionSettings;

        SourceFilters =
        [
            new ChatFilterOptionViewModel<AdminUserListSourceFilterDto> { Label = "Все пользователи", Value = AdminUserListSourceFilterDto.All },
            new ChatFilterOptionViewModel<AdminUserListSourceFilterDto> { Label = "Только private-start", Value = AdminUserListSourceFilterDto.PrivateStart },
            new ChatFilterOptionViewModel<AdminUserListSourceFilterDto> { Label = "Только flagged", Value = AdminUserListSourceFilterDto.Flagged }
        ];

        _selectedSourceFilter = SourceFilters[0];
        Rows = new ObservableCollection<UserListItemViewModel>();
        Warnings = new ObservableCollection<string>();
        DetailWarnings = new ObservableCollection<string>();
        _refreshCommand = new AsyncRelayCommand(RefreshAsync);
        _refreshDetailCommand = new AsyncRelayCommand(RefreshSelectedUserDetailAsync, () => SelectedRow is not null);
        _nextPageCommand = new AsyncRelayCommand(NextPageAsync, () => _currentPage < _totalPages);
        _prevPageCommand = new AsyncRelayCommand(PrevPageAsync, () => _currentPage > 1);
    }

    public IReadOnlyList<ChatFilterOptionViewModel<AdminUserListSourceFilterDto>> SourceFilters { get; }
    public ObservableCollection<UserListItemViewModel> Rows { get; }
    public ObservableCollection<string> Warnings { get; }
    public ObservableCollection<string> DetailWarnings { get; }

    public ChatFilterOptionViewModel<AdminUserListSourceFilterDto>? SelectedSourceFilter
    {
        get => _selectedSourceFilter;
        set
        {
            if (!SetProperty(ref _selectedSourceFilter, value))
                return;
            if (_hasLoadedOnce)
                _ = ResetAndRefreshAsync();
        }
    }

    public bool HasSelectedRow => _selectedRow is not null;

    public UserListItemViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetProperty(ref _selectedRow, value))
                return;

            _refreshDetailCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(HasSelectedRow));
            StartSelectedUserDetailLoad();
        }
    }

    public UserDetailViewModel? SelectedUserDetail
    {
        get => _selectedUserDetail;
        private set => SetProperty(ref _selectedUserDetail, value);
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    public string ScopeSummary
    {
        get => _scopeSummary;
        private set => SetProperty(ref _scopeSummary, value);
    }

    public string CountsSummary
    {
        get => _countsSummary;
        private set => SetProperty(ref _countsSummary, value);
    }

    public string LastUpdated
    {
        get => _lastUpdated;
        private set => SetProperty(ref _lastUpdated, value);
    }

    public string DetailStatus
    {
        get => _detailStatus;
        private set => SetProperty(ref _detailStatus, value);
    }

    public string DetailLastUpdated
    {
        get => _detailLastUpdated;
        private set => SetProperty(ref _detailLastUpdated, value);
    }

    public string WarningsHeadline
        => Warnings.Count == 0
            ? "Предупреждения: нет"
            : $"Предупреждения: {Warnings.Count}";

    public string DetailWarningsHeadline
        => DetailWarnings.Count == 0
            ? "Предупреждения карточки: нет"
            : $"Предупреждения карточки: {DetailWarnings.Count}";

    public AsyncRelayCommand RefreshCommand => _refreshCommand;
    public AsyncRelayCommand RefreshDetailCommand => _refreshDetailCommand;
    public AsyncRelayCommand NextPageCommand => _nextPageCommand;
    public AsyncRelayCommand PrevPageCommand => _prevPageCommand;

    public int CurrentPage => _currentPage;
    public int TotalPages => _totalPages;

    public string PaginationSummary
    {
        get => _paginationSummary;
        private set => SetProperty(ref _paginationSummary, value);
    }

    public bool HasNextPage => _currentPage < _totalPages;
    public bool HasPrevPage => _currentPage > 1;

    public async Task InitializeAsync()
    {
        if (!_hasLoadedOnce)
            await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RefreshAsync()
    {
        var requestCts = CancellationHelper.Replace(ref _listLoadCts);
        ConnectionStatus = "Выполняется запрос к реестру пользователей...";

        try
        {
            var selectedUserId = SelectedRow?.UserId;

            var response = await _adminHostClient
                .GetUsersAsync(
                    _connectionSettings.BaseUrl,
                    _connectionSettings.ApiToken,
                    _connectionSettings.RequestTimeout,
                    page: _currentPage,
                    pageSize: PageSize,
                    SelectedSourceFilter?.Value ?? AdminUserListSourceFilterDto.All,
                    SearchText,
                    requestCts.Token)
                .ConfigureAwait(true);

            Rows.Clear();
            foreach (var item in response.Items)
                Rows.Add(MapItem(item));

            _totalPages = Math.Max(1, (int)Math.Ceiling((double)response.TotalCount / PageSize));
            UpdatePaginationState();

            Warnings.Clear();
            foreach (var warning in response.Warnings)
                Warnings.Add(warning);

            ScopeSummary = response.Summary.CurrentBotScopeApplied
                ? "Показываются текущие и исторические пользователи. Исторические записи не скрываются, а текущий контур определяется по chat_registry."
                : "Работа идёт в режиме legacy schema: область текущего бота определить нельзя, поэтому доступен весь исторический контекст.";

            CountsSummary = BuildCountsSummary(response);
            LastUpdated = ChatPresentationFormatter.FormatTimestamp(DateTimeOffset.UtcNow);
            ConnectionStatus = response.TotalCount == 0
                ? BuildEmptyUsersStatus(response.SearchQuery)
                : "Подключение установлено. Реестр пользователей обновлён.";
            _hasLoadedOnce = true;

            UserListItemViewModel? nextSelection = null;

            if (selectedUserId.HasValue)
                nextSelection = Rows.FirstOrDefault(item => item.UserId == selectedUserId.Value);

            if (nextSelection is null && Rows.Count > 0)
                nextSelection = Rows[0];

            SelectedRow = nextSelection;

            if (Rows.Count == 0)
                ClearDetailState("По текущим фильтрам карточка пользователя недоступна.");

            OnPropertyChanged(nameof(WarningsHeadline));
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
        {
            // Quiet cancellation when a newer request supersedes this one.
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Ошибка запроса: {AdminHostErrorFormatter.Describe(ex)}";
            ScopeSummary = "Проверьте доступность AdminHost, SSH-туннель и настройки путей runtime.";
            CountsSummary = "Слой запросов только для чтения доступен архитектурно, но этот запрос завершился ошибкой.";
            Rows.Clear();
            Warnings.Clear();
            Warnings.Add("Не удалось получить список пользователей. Убедитесь, что AdminHost запущен и доступен по указанному адресу.");
            ClearDetailState("Карточка пользователя недоступна, пока список не загружен.");
            OnPropertyChanged(nameof(WarningsHeadline));
        }
        finally
        {
            CancellationHelper.Complete(ref _listLoadCts, requestCts);
        }
    }

    private async Task RefreshSelectedUserDetailAsync()
    {
        if (SelectedRow is null)
        {
            ClearDetailState("Выберите пользователя в списке, чтобы загрузить карточку.");
            return;
        }

        await LoadUserDetailAsync(SelectedRow.UserId).ConfigureAwait(true);
    }

    private void StartSelectedUserDetailLoad()
    {
        if (SelectedRow is null)
        {
            ClearDetailState("Выберите пользователя в списке, чтобы загрузить карточку.");
            return;
        }

        _ = LoadUserDetailAsync(SelectedRow.UserId);
    }

    private async Task LoadUserDetailAsync(long userId)
    {
        var requestCts = CancellationHelper.Replace(ref _detailLoadCts);
        DetailStatus = $"Загружается карточка пользователя {userId}...";

        try
        {
            var detail = await _adminHostClient
                .GetUserDetailAsync(
                    _connectionSettings.BaseUrl,
                    _connectionSettings.ApiToken,
                    _connectionSettings.RequestTimeout,
                    userId,
                    requestCts.Token)
                .ConfigureAwait(true);

            SelectedUserDetail = UserDetailViewModel.FromDto(detail);
            DetailWarnings.Clear();
            foreach (var warning in detail.Warnings)
                DetailWarnings.Add(warning);

            DetailLastUpdated = ChatPresentationFormatter.FormatTimestamp(DateTimeOffset.UtcNow);
            DetailStatus = "Карточка пользователя обновлена.";
            OnPropertyChanged(nameof(DetailWarningsHeadline));
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
        {
            // Quiet cancellation when selection changes quickly.
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            SelectedUserDetail = null;
            DetailWarnings.Clear();
            DetailWarnings.Add("Пользователь больше не виден в текущей области AdminHost. Обновите список пользователей.");
            DetailStatus = "Карточка не найдена в текущей области видимости.";
            DetailLastUpdated = "Никогда";
            OnPropertyChanged(nameof(DetailWarningsHeadline));
        }
        catch (Exception ex)
        {
            SelectedUserDetail = null;
            DetailWarnings.Clear();
            DetailWarnings.Add("Не удалось загрузить карточку пользователя. Проверьте доступность AdminHost и повторите запрос.");
            DetailStatus = $"Ошибка загрузки карточки: {AdminHostErrorFormatter.Describe(ex)}";
            DetailLastUpdated = "Никогда";
            OnPropertyChanged(nameof(DetailWarningsHeadline));
        }
        finally
        {
            CancellationHelper.Complete(ref _detailLoadCts, requestCts);
        }
    }

    private void ClearDetailState(string status)
    {
        SelectedUserDetail = null;
        DetailStatus = status;
        DetailLastUpdated = "Никогда";
        DetailWarnings.Clear();
        OnPropertyChanged(nameof(DetailWarningsHeadline));
    }

    private static string BuildCountsSummary(AdminUserListResponseDto response)
    {
        var parts = new List<string>
        {
            $"Пользователей: {response.Summary.VisibleUsers:N0}",
            $"private-start: {response.Summary.PrivateStartUsers:N0}",
            $"flagged: {response.Summary.FlaggedUsers:N0}",
            $"С телефоном: {response.Summary.UsersWithPhone:N0}"
        };

        if (response.Summary.HistoricalVisibilityEnabled)
        {
            parts.Add($"Текущий контур: {response.Summary.CurrentScopeUsers:N0}");
            parts.Add($"Исторический контекст: {response.Summary.HistoricalOnlyUsers:N0}");
        }

        return string.Join(" | ", parts);
    }

    private static string BuildEmptyUsersStatus(string? searchQuery)
    {
        return string.IsNullOrWhiteSpace(searchQuery)
            ? "Подключение установлено. По текущим фильтрам пользователи не найдены."
            : $"Подключение установлено. По запросу {FormatSearchQuery(searchQuery)} пользователи не найдены.";
    }

    private static string FormatSearchQuery(string? searchQuery)
        => string.IsNullOrWhiteSpace(searchQuery) ? "нет" : $"\"{searchQuery}\"";

    private async Task ResetAndRefreshAsync()
    {
        _currentPage = 1;
        UpdatePaginationState();
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task NextPageAsync()
    {
        if (_currentPage >= _totalPages) return;
        _currentPage++;
        UpdatePaginationState();
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task PrevPageAsync()
    {
        if (_currentPage <= 1) return;
        _currentPage--;
        UpdatePaginationState();
        await RefreshAsync().ConfigureAwait(true);
    }

    private void UpdatePaginationState()
    {
        PaginationSummary = $"Страница {_currentPage} из {_totalPages}";
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(HasNextPage));
        OnPropertyChanged(nameof(HasPrevPage));
        _nextPageCommand.RaiseCanExecuteChanged();
        _prevPageCommand.RaiseCanExecuteChanged();
    }

    private static UserListItemViewModel MapItem(AdminUserListItemDto item)
    {
        return new UserListItemViewModel
        {
            UserId = item.UserId,
            DisplayName = UserPresentationFormatter.BuildDisplayName(item.FirstName, item.LastName, item.Username, item.UserId),
            Username = UserPresentationFormatter.FormatUsername(item.Username),
            UsernameCopyValue = item.Username,
            ScopeStatus = ChatPresentationFormatter.FormatScopeStatus(item.InCurrentBotScope),
            PrivateStartStatus = UserPresentationFormatter.FormatPrivateStart(item.HasPrivateStart, item.PrivateStartCount),
            PhoneStatus = UserPresentationFormatter.FormatPhonePresence(item.HasPhone),
            ChatCount = ChatPresentationFormatter.FormatNumber(item.ChatCount),
            MessageCount = ChatPresentationFormatter.FormatNumber(item.MessageCount),
            FlaggedEventCount = ChatPresentationFormatter.FormatNumber(item.FlaggedEventCount),
            RiskEventCount = ChatPresentationFormatter.FormatNumber(item.RiskEventCount),
            LastActivityUtc = ChatPresentationFormatter.FormatTimestamp(item.LastActivityUtc)
        };
    }

}
