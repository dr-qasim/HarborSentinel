using HarborSentinel.AdminConsole.Infrastructure;
using HarborSentinel.AdminConsole.Services;
using HarborSentinel.AdminContracts.Incidents;
using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class IncidentsViewModel : PageViewModel, IAsyncPageInitializable
{
    private static readonly TimeSpan InboxPollingInterval = TimeSpan.FromSeconds(30);

    private readonly IAdminHostClient _adminHostClient;
    private readonly AdminConnectionSettings _connectionSettings;
    private readonly DispatcherTimer _pollTimer;
    private readonly AsyncRelayCommand _refreshCommand;
    private readonly AsyncRelayCommand _markUnreadAsSeenCommand;
    private readonly AsyncRelayCommand _acknowledgeSelectedCommand;

    private readonly List<IncidentListItemViewModel> _allRows = [];
    private CancellationTokenSource? _loadCts;
    private bool _hasLoadedOnce;
    private bool _monitoringStarted;
    private bool _syncInProgress;
    private IncidentWindowOptionViewModel? _selectedWindow;
    private IncidentSeverityFilterViewModel? _selectedSeverityFilter;
    private string _searchText = string.Empty;
    private IncidentListItemViewModel? _selectedIncident;
    private IncidentDetailViewModel? _selectedIncidentDetail;
    private string _chatIdFilterText = string.Empty;
    private string _connectionStatus = "Лента инцидентов ещё не загружалась.";
    private string _scopeSummary = "Показываются текущие и исторические инциденты; если runtime это поддерживает, текущий контур будет отмечен отдельно.";
    private string _countsSummary = "Сводка пока недоступна.";
    private string _querySummary = "Фильтры пока не применялись.";
    private string _lastUpdated = "Никогда";
    private string _detailStatus = "Выберите инцидент в списке, чтобы открыть подробности.";
    private string _startupSyncStatus = "Синхронизация durable inbox ещё не выполнялась.";
    private string _reviewStateSummary = "Unread/read состояние пока не загружено.";
    private int _unreadCount;
    private int _requiresAttentionCount;
    private string _navigationLabel = "Инциденты";

    public IncidentsViewModel(IAdminHostClient adminHostClient, AdminConnectionSettings connectionSettings)
        : base("Инциденты", "Durable inbox инцидентов, риск-контекст и приоритеты review")
    {
        _adminHostClient = adminHostClient;
        _connectionSettings = connectionSettings;

        RecentWindows =
        [
            new IncidentWindowOptionViewModel { Label = "За 1 день", Days = 1 },
            new IncidentWindowOptionViewModel { Label = "За 3 дня", Days = 3 },
            new IncidentWindowOptionViewModel { Label = "За 7 дней", Days = 7 },
            new IncidentWindowOptionViewModel { Label = "За 14 дней", Days = 14 },
            new IncidentWindowOptionViewModel { Label = "За 30 дней", Days = 30 },
            new IncidentWindowOptionViewModel { Label = "За 90 дней", Days = 90 }
        ];

        SeverityFilters =
        [
            new IncidentSeverityFilterViewModel { Label = "Все уровни", Key = "all" },
            new IncidentSeverityFilterViewModel { Label = "Высокий (≥ 7.0)", Key = "high" },
            new IncidentSeverityFilterViewModel { Label = "Средний (4 – 7)", Key = "medium" },
            new IncidentSeverityFilterViewModel { Label = "Низкий (< 4)", Key = "low" },
        ];

        _selectedWindow = RecentWindows[4];
        _selectedSeverityFilter = SeverityFilters[0];
        Rows = new ObservableCollection<IncidentListItemViewModel>();
        Warnings = new ObservableCollection<string>();
        _refreshCommand = new AsyncRelayCommand(RefreshAsync);
        _markUnreadAsSeenCommand = new AsyncRelayCommand(MarkUnreadAsSeenAsync, () => UnreadCount > 0);
        _acknowledgeSelectedCommand = new AsyncRelayCommand(AcknowledgeSelectedIncidentAsync, CanAcknowledgeSelectedIncident);
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = InboxPollingInterval
        };
        _pollTimer.Tick += OnPollTimerTick;
    }

    public IReadOnlyList<IncidentWindowOptionViewModel> RecentWindows { get; }
    public IReadOnlyList<IncidentSeverityFilterViewModel> SeverityFilters { get; }
    public ObservableCollection<IncidentListItemViewModel> Rows { get; }
    public ObservableCollection<string> Warnings { get; }

    public IncidentWindowOptionViewModel? SelectedWindow
    {
        get => _selectedWindow;
        set => SetProperty(ref _selectedWindow, value);
    }

    public IncidentSeverityFilterViewModel? SelectedSeverityFilter
    {
        get => _selectedSeverityFilter;
        set
        {
            if (!SetProperty(ref _selectedSeverityFilter, value))
                return;
            ApplyClientFilters();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value))
                return;
            ApplyClientFilters();
        }
    }

    public string ChatIdFilterText
    {
        get => _chatIdFilterText;
        set => SetProperty(ref _chatIdFilterText, value);
    }

    public IncidentListItemViewModel? SelectedIncident
    {
        get => _selectedIncident;
        set
        {
            if (!SetProperty(ref _selectedIncident, value))
                return;

            SelectedIncidentDetail = value is null
                ? null
                : IncidentDetailViewModel.FromListItem(value);

            DetailStatus = value is null
                ? "Выберите инцидент в списке, чтобы открыть подробности."
                : "Показывается durable-карточка выбранного инцидента.";

            OnPropertyChanged(nameof(HasSelectedIncident));
            _acknowledgeSelectedCommand.RaiseCanExecuteChanged();
        }
    }

    public IncidentDetailViewModel? SelectedIncidentDetail
    {
        get => _selectedIncidentDetail;
        private set => SetProperty(ref _selectedIncidentDetail, value);
    }

    public bool HasSelectedIncident => _selectedIncident is not null;

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

    public string QuerySummary
    {
        get => _querySummary;
        private set => SetProperty(ref _querySummary, value);
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

    public string StartupSyncStatus
    {
        get => _startupSyncStatus;
        private set => SetProperty(ref _startupSyncStatus, value);
    }

    public string ReviewStateSummary
    {
        get => _reviewStateSummary;
        private set => SetProperty(ref _reviewStateSummary, value);
    }

    public int UnreadCount
    {
        get => _unreadCount;
        private set
        {
            if (!SetProperty(ref _unreadCount, value))
                return;

            OnPropertyChanged(nameof(UnreadIndicatorText));
            OnPropertyChanged(nameof(IncidentStatusBarText));
            UpdateNavigationLabel();
            _markUnreadAsSeenCommand.RaiseCanExecuteChanged();
        }
    }

    public int RequiresAttentionCount
    {
        get => _requiresAttentionCount;
        private set
        {
            if (!SetProperty(ref _requiresAttentionCount, value))
                return;

            OnPropertyChanged(nameof(IncidentStatusBarText));
        }
    }

    public string NavigationLabel
    {
        get => _navigationLabel;
        private set => SetProperty(ref _navigationLabel, value);
    }

    public string UnreadIndicatorText
        => UnreadCount == 0
            ? "Новых инцидентов нет."
            : $"Новых инцидентов: {UnreadCount:N0}";

    public string IncidentStatusBarText
        => UnreadCount == 0 && RequiresAttentionCount == 0
            ? "Инциденты: новых записей нет."
            : $"Инциденты: новых {UnreadCount:N0}, требуют внимания {RequiresAttentionCount:N0}.";

    public string WarningsHeadline
        => Warnings.Count == 0
            ? "Предупреждения: нет"
            : $"Предупреждения: {Warnings.Count}";

    public AsyncRelayCommand RefreshCommand => _refreshCommand;
    public AsyncRelayCommand MarkUnreadAsSeenCommand => _markUnreadAsSeenCommand;
    public AsyncRelayCommand AcknowledgeSelectedCommand => _acknowledgeSelectedCommand;

    public async Task InitializeAsync()
    {
        await StartMonitoringAsync().ConfigureAwait(true);
        if (!_hasLoadedOnce)
            await RefreshAsync().ConfigureAwait(true);
    }

    public async Task StartMonitoringAsync()
    {
        if (_monitoringStarted)
            return;

        _monitoringStarted = true;
        await RefreshStartupSyncAsync().ConfigureAwait(true);
        _pollTimer.Start();
    }

    private async void OnPollTimerTick(object? sender, EventArgs e)
    {
        await RefreshStartupSyncAsync().ConfigureAwait(true);
    }

    private async Task RefreshStartupSyncAsync()
    {
        if (_syncInProgress)
            return;

        _syncInProgress = true;
        try
        {
            var response = await _adminHostClient
                .GetIncidentStartupSyncAsync(
                    _connectionSettings.BaseUrl,
                    _connectionSettings.ApiToken,
                    _connectionSettings.OperatorId,
                    _connectionSettings.RequestTimeout,
                    recentWindowDays: 90,
                    maxItems: 10,
                    CancellationToken.None)
                .ConfigureAwait(true);

            UnreadCount = response.Summary.UnreadCount;
            RequiresAttentionCount = response.Summary.RequiresAttentionCount;
            StartupSyncStatus = response.Summary.UnreadCount == 0
                ? "Startup sync выполнен. Непросмотренных инцидентов нет."
                : $"Startup sync выполнен. Накоплено непросмотренных инцидентов: {response.Summary.UnreadCount:N0}.";
            ReviewStateSummary = BuildReviewStateSummary(response.Summary);
        }
        catch (Exception ex)
        {
            StartupSyncStatus = $"Startup sync недоступен: {AdminHostErrorFormatter.Describe(ex)}";
        }
        finally
        {
            _syncInProgress = false;
        }
    }

    private async Task RefreshAsync()
    {
        var requestCts = CancellationHelper.Replace(ref _loadCts);
        ConnectionStatus = "Выполняется запрос к durable inbox инцидентов...";

        try
        {
            var chatIdFilter = ParseChatIdFilter(out var invalidChatIdFilter);
            var response = await _adminHostClient
                .GetIncidentsAsync(
                    _connectionSettings.BaseUrl,
                    _connectionSettings.ApiToken,
                    _connectionSettings.OperatorId,
                    _connectionSettings.RequestTimeout,
                    page: 1,
                    pageSize: 100,
                    chatIdFilter,
                    SelectedWindow?.Days ?? 30,
                    requestCts.Token)
                .ConfigureAwait(true);

            _allRows.Clear();
            foreach (var item in response.Items)
                _allRows.Add(MapItem(item));

            Warnings.Clear();
            foreach (var warning in response.Warnings)
                Warnings.Add(warning);

            if (response.ReturnedCount < response.TotalCount)
                Warnings.Add($"Показано {response.ReturnedCount} из {response.TotalCount} инцидентов. Сузьте окно или фильтр по чату, чтобы увидеть все записи.");

            if (invalidChatIdFilter)
                Warnings.Add("Фильтр по ID чата не применён: значение не удалось распознать.");

            ApplyClientFilters();

            ScopeSummary = response.Summary.CurrentBotScopeApplied
                ? "Показываются текущие и исторические инциденты. Исторический review-контекст не скрывается, даже если чат больше не активен."
                : "Работа идёт в режиме legacy schema: область текущего бота определить нельзя, поэтому доступен весь исторический контекст.";

            CountsSummary = BuildCountsSummary(response);
            QuerySummary = BuildQuerySummary(response, invalidChatIdFilter, ChatIdFilterText);
            LastUpdated = ChatPresentationFormatter.FormatTimestamp(DateTimeOffset.UtcNow);
            ConnectionStatus = response.TotalCount == 0
                ? BuildEmptyIncidentsStatus(invalidChatIdFilter)
                : "Подключение установлено. Durable inbox инцидентов обновлён.";
            ReviewStateSummary = BuildReviewStateSummary(response.Summary);
            UnreadCount = response.Summary.UnreadCount;
            RequiresAttentionCount = response.Summary.RequiresAttentionCount;
            StartupSyncStatus = response.Summary.UnreadCount == 0
                ? "Startup sync выполнен. Непросмотренных инцидентов нет."
                : $"Startup sync выполнен. В durable inbox есть новые инциденты: {response.Summary.UnreadCount:N0}.";

            _hasLoadedOnce = true;

            // Auto-select most relevant item after a full server refresh.
            SelectedIncident = Rows.FirstOrDefault(static item => item.IsUnreadValue)
                ?? Rows.FirstOrDefault(static item => !item.IsAcknowledgedValue)
                ?? Rows.FirstOrDefault();

            if (Rows.Count == 0)
                DetailStatus = "По текущим фильтрам инциденты отсутствуют.";

            OnPropertyChanged(nameof(WarningsHeadline));
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
        {
            // Quiet cancellation on refresh supersession.
        }
        catch (Exception ex)
        {
            _allRows.Clear();
            Rows.Clear();
            Warnings.Clear();
            Warnings.Add("Не удалось получить durable inbox инцидентов. Убедитесь, что AdminHost запущен и доступен по указанному адресу.");
            ConnectionStatus = $"Ошибка запроса: {AdminHostErrorFormatter.Describe(ex)}";
            ScopeSummary = "Проверьте доступность AdminHost, SSH-туннель и состояние incident inbox storage.";
            CountsSummary = "Лента недоступна.";
            QuerySummary = "Сводка фильтров недоступна.";
            SelectedIncident = null;
            OnPropertyChanged(nameof(WarningsHeadline));
        }
        finally
        {
            CancellationHelper.Complete(ref _loadCts, requestCts);
        }
    }

    private async Task MarkUnreadAsSeenAsync()
    {
        var invalidChatIdFilter = false;
        var chatIdFilter = ParseChatIdFilter(out invalidChatIdFilter);
        if (invalidChatIdFilter)
        {
            Warnings.Add("Фильтр по ID чата не применён: значение не удалось распознать.");
            OnPropertyChanged(nameof(WarningsHeadline));
        }

        try
        {
            var response = await _adminHostClient
                .MarkIncidentsSeenAsync(
                    _connectionSettings.BaseUrl,
                    _connectionSettings.ApiToken,
                    _connectionSettings.OperatorId,
                    _connectionSettings.RequestTimeout,
                    new AdminIncidentMarkSeenRequestDto
                    {
                        OperatorId = _connectionSettings.OperatorId,
                        MarkAllMatching = true,
                        RecentWindowDays = SelectedWindow?.Days ?? 30,
                        ChatIdFilter = chatIdFilter
                    },
                    CancellationToken.None)
                .ConfigureAwait(true);

            UnreadCount = response.Summary.UnreadCount;
            RequiresAttentionCount = response.Summary.RequiresAttentionCount;
            StartupSyncStatus = "Новые инциденты отмечены как просмотренные.";
            ReviewStateSummary = BuildReviewStateSummary(response.Summary);

            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Не удалось отметить инциденты просмотренными: {AdminHostErrorFormatter.Describe(ex)}";
        }
    }

    private async Task AcknowledgeSelectedIncidentAsync()
    {
        if (SelectedIncident is null)
            return;

        try
        {
            var response = await _adminHostClient
                .AcknowledgeIncidentAsync(
                    _connectionSettings.BaseUrl,
                    _connectionSettings.ApiToken,
                    _connectionSettings.OperatorId,
                    _connectionSettings.RequestTimeout,
                    SelectedIncident.IncidentId,
                    new AdminIncidentAcknowledgeRequestDto
                    {
                        OperatorId = _connectionSettings.OperatorId
                    },
                    CancellationToken.None)
                .ConfigureAwait(true);

            UnreadCount = response.Summary.UnreadCount;
            RequiresAttentionCount = response.Summary.RequiresAttentionCount;
            StartupSyncStatus = "Инцидент подтверждён оператором.";
            ReviewStateSummary = BuildReviewStateSummary(response.Summary);

            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Не удалось подтвердить инцидент: {AdminHostErrorFormatter.Describe(ex)}";
        }
    }

    private bool CanAcknowledgeSelectedIncident()
        => SelectedIncident is not null && !SelectedIncident.IsAcknowledgedValue;

    private void ApplyClientFilters()
    {
        var currentlySelected = SelectedIncident;
        var severityKey = _selectedSeverityFilter?.Key ?? "all";
        var search = _searchText.Trim();

        Rows.Clear();
        foreach (var row in _allRows)
        {
            if (severityKey == "high" && !row.IsHighRisk) continue;
            if (severityKey == "medium" && !row.IsMediumRisk) continue;
            if (severityKey == "low" && (row.IsHighRisk || row.IsMediumRisk)) continue;

            if (!string.IsNullOrEmpty(search) &&
                !row.ChatTitle.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !row.Username.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !row.Label.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;

            Rows.Add(row);
        }

        // Keep current selection if still visible; otherwise clear it.
        if (currentlySelected is not null && !Rows.Contains(currentlySelected))
            SelectedIncident = null;

        OnPropertyChanged(nameof(WarningsHeadline));
    }

    private void UpdateNavigationLabel()
    {
        NavigationLabel = UnreadCount == 0
            ? "Инциденты"
            : $"Инциденты ({UnreadCount})";
    }

    private long? ParseChatIdFilter(out bool invalidChatIdFilter)
    {
        invalidChatIdFilter = false;

        if (string.IsNullOrWhiteSpace(ChatIdFilterText))
            return null;

        if (long.TryParse(ChatIdFilterText.Trim(), out var chatId))
            return chatId;

        invalidChatIdFilter = true;
        return null;
    }

    private static string BuildCountsSummary(AdminIncidentFeedResponseDto response)
    {
        var parts = new List<string>
        {
            $"Инцидентов: {response.TotalCount:N0}",
            $"Новых: {response.Summary.UnreadCount:N0}",
            $"Требуют внимания: {response.Summary.RequiresAttentionCount:N0}",
            $"Подтверждено: {response.Summary.AcknowledgedCount:N0}",
            $"Чатов: {response.Summary.DistinctChats:N0}",
            $"Пользователей: {response.Summary.DistinctUsers:N0}"
        };

        if (response.Summary.HistoricalVisibilityEnabled)
        {
            parts.Add($"Текущий контур: {response.Summary.CurrentScopeIncidents:N0}");
            parts.Add($"Исторический контекст: {response.Summary.HistoricalOnlyIncidents:N0}");
        }

        if (response.Summary.HighestRiskScore.HasValue)
            parts.Add($"Макс. score: {response.Summary.HighestRiskScore.Value:0.00}");

        return string.Join(" | ", parts);
    }

    private string BuildQuerySummary(AdminIncidentFeedResponseDto response, bool invalidChatIdFilter, string? rawChatIdFilter)
    {
        var chatPart = invalidChatIdFilter
            ? $"некорректный фильтр \"{rawChatIdFilter}\" проигнорирован"
            : response.ChatIdFilter.HasValue
                ? $"ID чата {response.ChatIdFilter.Value}"
                : "все чаты";

        return $"Окно: {response.RecentWindowDays} дн. | Область: {chatPart} | Показано {response.ReturnedCount} из {response.TotalCount}";
    }

    private static string BuildReviewStateSummary(AdminIncidentUnreadSummaryDto summary)
    {
        return $"Новых: {summary.UnreadCount:N0} | Требуют внимания: {summary.RequiresAttentionCount:N0} | Подтверждено: {summary.AcknowledgedCount:N0}";
    }

    private static string BuildReviewStateSummary(AdminIncidentFeedSummaryDto summary)
    {
        return $"Новых: {summary.UnreadCount:N0} | Требуют внимания: {summary.RequiresAttentionCount:N0} | Подтверждено: {summary.AcknowledgedCount:N0}";
    }

    private static string BuildEmptyIncidentsStatus(bool invalidChatIdFilter)
    {
        return invalidChatIdFilter
            ? "Подключение установлено. Инциденты не найдены, а фильтр по ID чата был проигнорирован из-за некорректного значения."
            : "Подключение установлено. По текущим фильтрам инциденты не найдены.";
    }

    private static IncidentListItemViewModel MapItem(AdminIncidentFeedItemDto item)
    {
        var personDisplay = string.Join(" ", new[] { item.FirstName, item.LastName }.Where(static part => !string.IsNullOrWhiteSpace(part)));

        return new IncidentListItemViewModel
        {
            IncidentId = item.IncidentId,
            EventAtUtcValue = item.EventAtUtc,
            ChatId = item.ChatId,
            UserId = item.UserId,
            MessageId = item.MessageId,
            RiskScoreValue = item.RiskScore,
            ConfidenceValue = item.Confidence,
            NeedsContextReviewValue = item.NeedsContextReview,
            GroupedCountValue = item.GroupedCount,
            IsUnreadValue = item.IsUnread,
            IsAcknowledgedValue = item.IsAcknowledged,
            SeenAtUtcValue = item.SeenAtUtc,
            AcknowledgedAtUtcValue = item.AcknowledgedAtUtc,
            DisplayTimeUtc = ChatPresentationFormatter.FormatTimestamp(item.EventAtUtc),
            ChatTitle = string.IsNullOrWhiteSpace(item.ChatTitle) ? "(без названия)" : item.ChatTitle,
            Username = string.IsNullOrWhiteSpace(item.Username) ? "нет" : $"@{item.Username}",
            UsernameCopyValue = item.Username,
            ScopeStatus = ChatPresentationFormatter.FormatScopeStatus(item.InCurrentBotScope),
            ChatActivity = ChatPresentationFormatter.FormatActivityState(item.ChatIsActive),
            Label = string.IsNullOrWhiteSpace(item.Label) ? "нет" : item.Label,
            Topic = string.IsNullOrWhiteSpace(item.Topic) ? "нет" : item.Topic,
            Intent = string.IsNullOrWhiteSpace(item.Intent) ? "нет" : item.Intent,
            RiskScore = item.RiskScore.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            Confidence = item.Confidence?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "нет данных",
            NeedsContextReview = item.NeedsContextReview ? "да" : "нет",
            GroupedCount = item.GroupedCount.ToString(),
            MessagePreview = string.IsNullOrWhiteSpace(item.MessageText) ? "нет данных" : TrimText(item.MessageText, 160),
            ReasonPreview = string.IsNullOrWhiteSpace(item.Reason) ? "нет данных" : TrimText(item.Reason, 180),
            PersonDisplay = string.IsNullOrWhiteSpace(personDisplay) ? "нет" : personDisplay,
            MessageText = string.IsNullOrWhiteSpace(item.MessageText) ? "нет данных" : item.MessageText,
            ReasonText = string.IsNullOrWhiteSpace(item.Reason) ? "нет данных" : item.Reason,
            InboxStatus = item.IsUnread ? "новый" : "просмотрен",
            AcknowledgmentStatus = item.IsAcknowledged ? "подтверждён" : "ожидает подтверждения",
            SeenAtText = item.SeenAtUtc.HasValue ? ChatPresentationFormatter.FormatTimestamp(item.SeenAtUtc.Value) : "ещё не просмотрен",
            AcknowledgedAtText = item.AcknowledgedAtUtc.HasValue ? ChatPresentationFormatter.FormatTimestamp(item.AcknowledgedAtUtc.Value) : "не подтверждён"
        };
    }

    private static string TrimText(string value, int maxLength)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (singleLine.Contains("  ", StringComparison.Ordinal))
            singleLine = singleLine.Replace("  ", " ", StringComparison.Ordinal);

        if (singleLine.Length <= maxLength)
            return singleLine;

        return singleLine[..(maxLength - 1)] + "…";
    }

}
