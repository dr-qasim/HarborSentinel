using HarborSentinel.AdminConsole.Infrastructure;
using HarborSentinel.AdminConsole.Services;
using HarborSentinel.AdminContracts.Chats;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;

namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class ChatsViewModel : PageViewModel, IAsyncPageInitializable
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
    private ChatFilterOptionViewModel<AdminChatActivityFilterDto>? _selectedActivityFilter;
    private ChatFilterOptionViewModel<AdminChatTrackingFilterDto>? _selectedTrackingFilter;
    private ChatListItemViewModel? _selectedRow;
    private ChatDetailViewModel? _selectedChatDetail;
    private string _searchText = string.Empty;
    private string _connectionStatus = "Список чатов ещё не загружался.";
    private string _scopeSummary = "Область текущего бота будет применяться, если она поддерживается схемой runtime.";
    private string _countsSummary = "Сводка пока недоступна.";
    private string _paginationSummary = "Страница 1 из 1";
    private string _lastUpdated = "Никогда";
    private string _detailStatus = "Выберите чат в списке, чтобы загрузить карточку.";
    private string _detailLastUpdated = "Никогда";

    public ChatsViewModel(IAdminHostClient adminHostClient, AdminConnectionSettings connectionSettings)
        : base("Чаты", "Реестр чатов, область текущего бота и состояние отслеживания")
    {
        _adminHostClient = adminHostClient;
        _connectionSettings = connectionSettings;

        ActivityFilters =
        [
            new ChatFilterOptionViewModel<AdminChatActivityFilterDto> { Label = "Все чаты", Value = AdminChatActivityFilterDto.All },
            new ChatFilterOptionViewModel<AdminChatActivityFilterDto> { Label = "Только активные", Value = AdminChatActivityFilterDto.Active },
            new ChatFilterOptionViewModel<AdminChatActivityFilterDto> { Label = "Только удалённые", Value = AdminChatActivityFilterDto.Removed }
        ];

        TrackingFilters =
        [
            new ChatFilterOptionViewModel<AdminChatTrackingFilterDto> { Label = "Все состояния", Value = AdminChatTrackingFilterDto.All },
            new ChatFilterOptionViewModel<AdminChatTrackingFilterDto> { Label = "Только отслеживаемые", Value = AdminChatTrackingFilterDto.Tracked },
            new ChatFilterOptionViewModel<AdminChatTrackingFilterDto> { Label = "Только неотслеживаемые", Value = AdminChatTrackingFilterDto.Untracked }
        ];

        _selectedActivityFilter = ActivityFilters[0];
        _selectedTrackingFilter = TrackingFilters[0];

        Rows = new ObservableCollection<ChatListItemViewModel>();
        Warnings = new ObservableCollection<string>();
        DetailWarnings = new ObservableCollection<string>();
        _refreshCommand = new AsyncRelayCommand(RefreshAsync);
        _refreshDetailCommand = new AsyncRelayCommand(RefreshSelectedChatDetailAsync, () => SelectedRow is not null);
        _nextPageCommand = new AsyncRelayCommand(NextPageAsync, () => _currentPage < _totalPages);
        _prevPageCommand = new AsyncRelayCommand(PrevPageAsync, () => _currentPage > 1);
    }

    public IReadOnlyList<ChatFilterOptionViewModel<AdminChatActivityFilterDto>> ActivityFilters { get; }
    public IReadOnlyList<ChatFilterOptionViewModel<AdminChatTrackingFilterDto>> TrackingFilters { get; }
    public ObservableCollection<ChatListItemViewModel> Rows { get; }
    public ObservableCollection<string> Warnings { get; }
    public ObservableCollection<string> DetailWarnings { get; }

    public ChatFilterOptionViewModel<AdminChatActivityFilterDto>? SelectedActivityFilter
    {
        get => _selectedActivityFilter;
        set
        {
            if (!SetProperty(ref _selectedActivityFilter, value))
                return;
            if (_hasLoadedOnce)
                _ = ResetAndRefreshAsync();
        }
    }

    public ChatFilterOptionViewModel<AdminChatTrackingFilterDto>? SelectedTrackingFilter
    {
        get => _selectedTrackingFilter;
        set
        {
            if (!SetProperty(ref _selectedTrackingFilter, value))
                return;
            if (_hasLoadedOnce)
                _ = ResetAndRefreshAsync();
        }
    }

    public ChatListItemViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetProperty(ref _selectedRow, value))
                return;

            _refreshDetailCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(HasSelectedRow));
            StartSelectedChatDetailLoad();
        }
    }

    public bool HasSelectedRow => _selectedRow is not null;

    public ChatDetailViewModel? SelectedChatDetail
    {
        get => _selectedChatDetail;
        private set => SetProperty(ref _selectedChatDetail, value);
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

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public int CurrentPage => _currentPage;
    public int TotalPages => _totalPages;

    public string PaginationSummary
    {
        get => _paginationSummary;
        private set => SetProperty(ref _paginationSummary, value);
    }

    public bool HasNextPage => _currentPage < _totalPages;
    public bool HasPrevPage => _currentPage > 1;

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

    public async Task InitializeAsync()
    {
        if (!_hasLoadedOnce)
            await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RefreshAsync()
    {
        var requestCts = CancellationHelper.Replace(ref _listLoadCts);
        ConnectionStatus = "Выполняется запрос к реестру чатов...";

        try
        {
            var selectedChatId = SelectedRow?.ChatId;

            var response = await _adminHostClient
                .GetChatsAsync(
                    _connectionSettings.BaseUrl,
                    _connectionSettings.ApiToken,
                    _connectionSettings.RequestTimeout,
                    page: _currentPage,
                    pageSize: PageSize,
                    SelectedActivityFilter?.Value ?? AdminChatActivityFilterDto.All,
                    SelectedTrackingFilter?.Value ?? AdminChatTrackingFilterDto.All,
                    requestCts.Token)
                .ConfigureAwait(true);

            Rows.Clear();
            foreach (var item in response.Items)
            {
                var vm = MapItem(item);
                if (!string.IsNullOrWhiteSpace(_searchText) &&
                    !MatchesSearch(vm, _searchText))
                    continue;
                Rows.Add(vm);
            }

            _totalPages = Math.Max(1, (int)Math.Ceiling((double)response.TotalCount / PageSize));
            UpdatePaginationState();

            Warnings.Clear();
            foreach (var warning in response.Warnings)
                Warnings.Add(warning);

            ScopeSummary = response.Summary.CurrentBotScopeApplied
                ? "Показываются только чаты, видимые текущему экземпляру бота."
                : "Работа идёт в режиме legacy schema: изоляция по текущему боту недоступна.";

            CountsSummary = BuildCountsSummary(response);
            LastUpdated = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
            ConnectionStatus = response.TotalCount == 0
                ? "Подключение установлено. По текущим фильтрам чаты не найдены."
                : "Подключение установлено. Реестр чатов обновлён.";
            _hasLoadedOnce = true;

            ChatListItemViewModel? nextSelection = null;

            if (selectedChatId.HasValue)
                nextSelection = Rows.FirstOrDefault(item => item.ChatId == selectedChatId.Value);

            if (nextSelection is null && Rows.Count > 0)
                nextSelection = Rows[0];

            SelectedRow = nextSelection;

            if (Rows.Count == 0)
                ClearDetailState("По текущим фильтрам карточка чата недоступна.");

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
            Warnings.Add("Не удалось получить список чатов. Убедитесь, что AdminHost запущен и доступен по указанному адресу.");
            ClearDetailState("Карточка чата недоступна, пока список чатов не загружен.");
            OnPropertyChanged(nameof(WarningsHeadline));
        }
        finally
        {
            CancellationHelper.Complete(ref _listLoadCts, requestCts);
        }
    }

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

    private static bool MatchesSearch(ChatListItemViewModel item, string search)
    {
        return item.DisplayTitle.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               item.Username.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               item.ChatId.ToString().Contains(search, StringComparison.Ordinal);
    }

    private async Task RefreshSelectedChatDetailAsync()
    {
        if (SelectedRow is null)
        {
            ClearDetailState("Выберите чат в списке, чтобы загрузить карточку.");
            return;
        }

        await LoadChatDetailAsync(SelectedRow.ChatId).ConfigureAwait(true);
    }

    private void StartSelectedChatDetailLoad()
    {
        if (SelectedRow is null)
        {
            ClearDetailState("Выберите чат в списке, чтобы загрузить карточку.");
            return;
        }

        _ = LoadChatDetailAsync(SelectedRow.ChatId);
    }

    private async Task LoadChatDetailAsync(long chatId)
    {
        var requestCts = CancellationHelper.Replace(ref _detailLoadCts);
        DetailStatus = $"Загружается карточка чата {chatId}...";

        try
        {
            var detail = await _adminHostClient
                .GetChatDetailAsync(
                    _connectionSettings.BaseUrl,
                    _connectionSettings.ApiToken,
                    _connectionSettings.RequestTimeout,
                    chatId,
                    requestCts.Token)
                .ConfigureAwait(true);

            SelectedChatDetail = ChatDetailViewModel.FromDto(detail);
            DetailWarnings.Clear();
            foreach (var warning in detail.Warnings)
                DetailWarnings.Add(warning);

            DetailLastUpdated = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
            DetailStatus = "Карточка чата обновлена.";
            OnPropertyChanged(nameof(DetailWarningsHeadline));
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
        {
            // Quiet cancellation when selection changes quickly.
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            SelectedChatDetail = null;
            DetailWarnings.Clear();
            DetailWarnings.Add("Чат больше не виден в текущей области AdminHost. Обновите список чатов.");
            DetailStatus = "Карточка не найдена в текущей области видимости.";
            DetailLastUpdated = "Никогда";
            OnPropertyChanged(nameof(DetailWarningsHeadline));
        }
        catch (Exception ex)
        {
            SelectedChatDetail = null;
            DetailWarnings.Clear();
            DetailWarnings.Add("Не удалось загрузить карточку чата. Проверьте доступность AdminHost и повторите запрос.");
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
        SelectedChatDetail = null;
        DetailStatus = status;
        DetailLastUpdated = "Никогда";
        DetailWarnings.Clear();
        OnPropertyChanged(nameof(DetailWarningsHeadline));
    }

    private static string BuildCountsSummary(AdminChatListResponseDto response)
    {
        var parts = new List<string>
        {
            $"Видимых: {response.Summary.VisibleChats:N0}"
        };

        if (response.Summary.ActiveChats is int active)
            parts.Add($"Активных: {active:N0}");

        if (response.Summary.RemovedChats is int removed)
            parts.Add($"Удалённых: {removed:N0}");

        if (response.Summary.TrackedChats is int tracked)
            parts.Add($"Отслеживаемых: {tracked:N0}");

        return string.Join(" | ", parts);
    }

    private static ChatListItemViewModel MapItem(AdminChatListItemDto item)
    {
        return new ChatListItemViewModel
        {
            ChatId = item.ChatId,
            DisplayTitle = string.IsNullOrWhiteSpace(item.ChatTitle) ? "(без названия)" : item.ChatTitle,
            Username = string.IsNullOrWhiteSpace(item.ChatUsername) ? "нет" : $"@{item.ChatUsername}",
            UsernameCopyValue = item.ChatUsername,
            ChatType = ChatPresentationFormatter.TranslateChatType(item.ChatType),
            Status = ChatPresentationFormatter.TranslateMemberStatus(item.Status),
            Active = ChatPresentationFormatter.FormatNullableBool(item.IsActive),
            Tracked = ChatPresentationFormatter.FormatNullableBool(item.IsTracked),
            FirstSeenUtc = ChatPresentationFormatter.FormatTimestamp(item.FirstSeenUtc),
            LastSeenUtc = ChatPresentationFormatter.FormatTimestamp(item.LastSeenUtc),
            RemovedUtc = ChatPresentationFormatter.FormatTimestamp(item.RemovedUtc)
        };
    }

}
