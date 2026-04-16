using HarborSentinel.AdminConsole.Infrastructure;
using HarborSentinel.AdminConsole.Services;
using HarborSentinel.AdminContracts.Exports;
using System.Collections.ObjectModel;
using System.Net.Http;

namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class ExportsViewModel : PageViewModel, IAsyncPageInitializable
{
    private readonly IAdminHostClient _adminHostClient;
    private readonly IExportFileSaveService _fileSaveService;
    private readonly AdminConnectionSettings _connectionSettings;
    private readonly AsyncRelayCommand _refreshCommand;
    private readonly AsyncRelayCommand _createJobCommand;
    private readonly AsyncRelayCommand _refreshDetailCommand;
    private readonly AsyncRelayCommand _downloadCommand;

    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _detailLoadCts;
    private bool _hasLoadedOnce;
    private ExportKindOptionViewModel? _selectedKind;
    private ExportJobListItemViewModel? _selectedJob;
    private ExportJobDetailViewModel? _selectedJobDetail;
    private string _chatIdText = string.Empty;
    private string _connectionStatus = "Контур выгрузок ещё не загружался.";
    private string _catalogSummary = "Каталог выгрузок пока недоступен.";
    private string _jobsSummary = "Список задач пока недоступен.";
    private string _lastUpdated = "Никогда";
    private string _detailStatus = "Выберите задачу в списке, чтобы открыть карточку.";
    private string _detailLastUpdated = "Никогда";
    private string _downloadStatus = "Файл ещё не загружался.";
    private bool _isLoadingViewer;

    public ExportsViewModel(
        IAdminHostClient adminHostClient,
        IExportFileSaveService fileSaveService,
        AdminConnectionSettings connectionSettings)
        : base("Выгрузки", "Server-side задачи и безопасное скачивание результатов вне Telegram")
    {
        _adminHostClient = adminHostClient;
        _fileSaveService = fileSaveService;
        _connectionSettings = connectionSettings;

        CatalogItems = new ObservableCollection<ExportKindOptionViewModel>();
        Jobs = new ObservableCollection<ExportJobListItemViewModel>();
        Warnings = new ObservableCollection<string>();
        DetailWarnings = new ObservableCollection<string>();

        _refreshCommand = new AsyncRelayCommand(RefreshAsync);
        _createJobCommand = new AsyncRelayCommand(CreateJobAsync, CanCreateJob);
        _refreshDetailCommand = new AsyncRelayCommand(RefreshSelectedJobDetailAsync, () => SelectedJob is not null);
        _downloadCommand = new AsyncRelayCommand(DownloadSelectedFileAsync, () => SelectedJobDetail?.CanDownload == true);
    }

    public ObservableCollection<ExportKindOptionViewModel> CatalogItems { get; }
    public ObservableCollection<ExportJobListItemViewModel> Jobs { get; }
    public ObservableCollection<string> Warnings { get; }
    public ObservableCollection<string> DetailWarnings { get; }

    public ExportKindOptionViewModel? SelectedKind
    {
        get => _selectedKind;
        set
        {
            if (!SetProperty(ref _selectedKind, value))
                return;

            _createJobCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsChatIdRequired));
            OnPropertyChanged(nameof(SelectedKindDescription));
            OnPropertyChanged(nameof(SelectedKindAvailability));
        }
    }

    public ExportJobListItemViewModel? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (!SetProperty(ref _selectedJob, value))
                return;

            _refreshDetailCommand.RaiseCanExecuteChanged();
            StartSelectedJobDetailLoad();
        }
    }

    public ExportJobDetailViewModel? SelectedJobDetail
    {
        get => _selectedJobDetail;
        private set
        {
            if (!SetProperty(ref _selectedJobDetail, value))
                return;

            _downloadCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(HasSelectedDownloadableFile));
            OnPropertyChanged(nameof(CanOpenViewer));
        }
    }

    public bool IsLoadingViewer
    {
        get => _isLoadingViewer;
        private set
        {
            SetProperty(ref _isLoadingViewer, value);
            OnPropertyChanged(nameof(CanOpenViewer));
        }
    }

    public string ChatIdText
    {
        get => _chatIdText;
        set
        {
            if (!SetProperty(ref _chatIdText, value))
                return;

            _createJobCommand.RaiseCanExecuteChanged();
        }
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    public string CatalogSummary
    {
        get => _catalogSummary;
        private set => SetProperty(ref _catalogSummary, value);
    }

    public string JobsSummary
    {
        get => _jobsSummary;
        private set => SetProperty(ref _jobsSummary, value);
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

    public string DownloadStatus
    {
        get => _downloadStatus;
        private set => SetProperty(ref _downloadStatus, value);
    }

    public bool IsChatIdRequired => SelectedKind?.RequiresChatId == true;

    public string SelectedKindDescription
        => string.IsNullOrWhiteSpace(SelectedKind?.Description)
            ? "Выберите тип выгрузки из каталога."
            : SelectedKind.Description;

    public string SelectedKindAvailability
        => string.IsNullOrWhiteSpace(SelectedKind?.AvailabilityNote)
            ? "Статус доступности не задан."
            : SelectedKind.AvailabilityNote;

    public string WarningsHeadline
        => Warnings.Count == 0
            ? "Предупреждения: нет"
            : $"Предупреждения: {Warnings.Count}";

    public string DetailWarningsHeadline
        => DetailWarnings.Count == 0
            ? "Предупреждения карточки: нет"
            : $"Предупреждения карточки: {DetailWarnings.Count}";

    public bool HasSelectedDownloadableFile => SelectedJobDetail?.CanDownload == true;

    public bool CanOpenViewer => HasSelectedDownloadableFile && !IsLoadingViewer;

    public AsyncRelayCommand RefreshCommand => _refreshCommand;
    public AsyncRelayCommand CreateJobCommand => _createJobCommand;
    public AsyncRelayCommand RefreshDetailCommand => _refreshDetailCommand;
    public AsyncRelayCommand DownloadCommand => _downloadCommand;

    public async Task InitializeAsync()
    {
        if (!_hasLoadedOnce)
            await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RefreshAsync()
    {
        var requestCts = CancellationHelper.Replace(ref _loadCts);
        ConnectionStatus = "Загружается каталог выгрузок и список задач...";

        try
        {
            var selectedJobId = SelectedJob?.JobId;
            var timeout = _connectionSettings.RequestTimeout;
            var catalog = await _adminHostClient.GetExportCatalogAsync(_connectionSettings.BaseUrl, _connectionSettings.ApiToken, timeout, requestCts.Token).ConfigureAwait(true);
            var jobs = await _adminHostClient.GetExportJobsAsync(_connectionSettings.BaseUrl, _connectionSettings.ApiToken, timeout, requestCts.Token).ConfigureAwait(true);

            CatalogItems.Clear();
            foreach (var item in catalog.Items)
            {
                CatalogItems.Add(new ExportKindOptionViewModel
                {
                    Kind = item.Kind,
                    Label = item.Label,
                    Description = item.Description,
                    RequiresChatId = item.RequiresChatId,
                    IsAvailable = item.IsAvailable,
                    AvailabilityNote = item.AvailabilityNote ?? "нет данных"
                });
            }

            if (SelectedKind is null || CatalogItems.All(x => x.Kind != SelectedKind.Kind))
                SelectedKind = CatalogItems.FirstOrDefault();

            Jobs.Clear();
            foreach (var item in jobs.Items)
                Jobs.Add(ExportPresentationFormatter.ToListItem(item));

            Warnings.Clear();
            foreach (var warning in catalog.Warnings.Concat(jobs.Warnings))
                Warnings.Add(warning);

            CatalogSummary = $"Доступно типов: {CatalogItems.Count(x => x.IsAvailable)} из {CatalogItems.Count}.";
            JobsSummary = $"Задач: {jobs.TotalCount:N0} | Готово к скачиванию: {jobs.Items.Count(x => x.HasDownload):N0}.";
            LastUpdated = ChatPresentationFormatter.FormatTimestamp(DateTimeOffset.UtcNow);
            ConnectionStatus = "Каталог выгрузок и список задач обновлены.";
            _hasLoadedOnce = true;

            ExportJobListItemViewModel? nextSelection = null;
            if (!string.IsNullOrWhiteSpace(selectedJobId))
                nextSelection = Jobs.FirstOrDefault(item => item.JobId == selectedJobId);
            if (nextSelection is null && Jobs.Count > 0)
                nextSelection = Jobs[0];

            SelectedJob = nextSelection;
            if (Jobs.Count == 0)
                ClearDetailState("Задачи выгрузки ещё не создавались.");

            OnPropertyChanged(nameof(WarningsHeadline));
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            CatalogItems.Clear();
            Jobs.Clear();
            Warnings.Clear();
            Warnings.Add("Не удалось загрузить export contour. Убедитесь, что AdminHost запущен и доступен по указанному адресу.");
            ConnectionStatus = $"Ошибка запроса: {AdminHostErrorFormatter.Describe(ex)}";
            CatalogSummary = "Каталог недоступен.";
            JobsSummary = "Список задач недоступен.";
            ClearDetailState("Карточка выгрузки недоступна, пока список задач не загружен.");
            OnPropertyChanged(nameof(WarningsHeadline));
        }
        finally
        {
            CancellationHelper.Complete(ref _loadCts, requestCts);
        }
    }

    private bool CanCreateJob()
    {
        if (SelectedKind is null || !SelectedKind.IsAvailable)
            return false;

        if (!SelectedKind.RequiresChatId)
            return true;

        return long.TryParse(ChatIdText?.Trim(), out _);
    }

    private async Task CreateJobAsync()
    {
        if (SelectedKind is null)
            return;

        var request = new AdminExportJobCreateRequestDto
        {
            Kind = SelectedKind.Kind,
            ChatId = SelectedKind.RequiresChatId && long.TryParse(ChatIdText.Trim(), out var chatId)
                ? chatId
                : null
        };

        try
        {
            ConnectionStatus = "Создаётся новая задача выгрузки...";
            var created = await _adminHostClient
                .CreateExportJobAsync(
                    _connectionSettings.BaseUrl,
                    _connectionSettings.ApiToken,
                    _connectionSettings.RequestTimeout,
                    request,
                    CancellationToken.None)
                .ConfigureAwait(true);

            await RefreshAsync().ConfigureAwait(true);
            await LoadJobDetailAsync(created.JobId).ConfigureAwait(true);
            ConnectionStatus = "Задача выгрузки создана.";
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Не удалось создать задачу: {AdminHostErrorFormatter.Describe(ex)}";
        }
    }

    private async Task RefreshSelectedJobDetailAsync()
    {
        if (SelectedJob is null)
        {
            ClearDetailState("Выберите задачу в списке, чтобы открыть карточку.");
            return;
        }

        await LoadJobDetailAsync(SelectedJob.JobId).ConfigureAwait(true);
    }

    private void StartSelectedJobDetailLoad()
    {
        if (SelectedJob is null)
        {
            ClearDetailState("Выберите задачу в списке, чтобы открыть карточку.");
            return;
        }

        SelectedJobDetail = null;
        DetailStatus = $"Загружается карточка задачи {SelectedJob.JobId}...";
        DetailLastUpdated = "Никогда";
        DetailWarnings.Clear();
        DownloadStatus = "Файл ещё не загружался.";
        OnPropertyChanged(nameof(DetailWarningsHeadline));

        _ = LoadJobDetailAsync(SelectedJob.JobId);
    }

    private async Task LoadJobDetailAsync(string jobId)
    {
        var requestCts = CancellationHelper.Replace(ref _detailLoadCts);
        DetailStatus = $"Загружается карточка задачи {jobId}...";

        try
        {
            var detail = await _adminHostClient
                .GetExportJobAsync(
                    _connectionSettings.BaseUrl,
                    _connectionSettings.ApiToken,
                    _connectionSettings.RequestTimeout,
                    jobId,
                    requestCts.Token)
                .ConfigureAwait(true);

            SelectedJobDetail = ExportJobDetailViewModel.FromDto(detail);
            DetailWarnings.Clear();
            foreach (var warning in detail.Warnings)
                DetailWarnings.Add(warning);

            DetailLastUpdated = ChatPresentationFormatter.FormatTimestamp(DateTimeOffset.UtcNow);
            DetailStatus = "Карточка выгрузки обновлена.";
            OnPropertyChanged(nameof(DetailWarningsHeadline));
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
        {
        }
        catch (HttpRequestException ex)
        {
            SelectedJobDetail = null;
            DetailWarnings.Clear();
            DetailWarnings.Add("Не удалось загрузить карточку задачи. Обновите список выгрузок и повторите запрос.");
            DetailStatus = $"Ошибка загрузки карточки: {AdminHostErrorFormatter.Describe(ex)}";
            DetailLastUpdated = "Никогда";
            OnPropertyChanged(nameof(DetailWarningsHeadline));
        }
        finally
        {
            CancellationHelper.Complete(ref _detailLoadCts, requestCts);
        }
    }

    private async Task DownloadSelectedFileAsync()
    {
        if (SelectedJobDetail is null || !SelectedJobDetail.CanDownload || SelectedJob is null)
            return;

        try
        {
            DownloadStatus = "Подготавливается скачивание файла...";
            var file = await DownloadSelectedExportFileAsync(CancellationToken.None).ConfigureAwait(true);

            var path = _fileSaveService.PickSavePath(file.FileName);
            if (string.IsNullOrWhiteSpace(path))
            {
                DownloadStatus = "Скачивание отменено пользователем.";
                return;
            }

            await _fileSaveService.SaveAsync(path, file.Content, CancellationToken.None).ConfigureAwait(true);
            DownloadStatus = $"Файл сохранён: {path}";
        }
        catch (Exception ex)
        {
            DownloadStatus = $"Не удалось скачать файл: {AdminHostErrorFormatter.Describe(ex)}";
        }
    }

    public async Task<ExportViewerViewModel?> OpenSelectedExportViewerAsync(CancellationToken ct)
    {
        if (SelectedJob is null)
            return null;

        if (SelectedJobDetail is null || SelectedJobDetail.JobId != SelectedJob.JobId)
            await LoadJobDetailAsync(SelectedJob.JobId).ConfigureAwait(true);

        if (SelectedJobDetail is null || !SelectedJobDetail.CanDownload)
            return null;

        IsLoadingViewer = true;
        try
        {
            DownloadStatus = "Загружается содержимое выгрузки для просмотра...";
            var file = await DownloadSelectedExportFileAsync(ct).ConfigureAwait(true);

            DownloadStatus = "Разбирается содержимое файла...";
            var fileName = file.FileName;
            var fileContent = file.Content;
            var (structured, raw) = await Task.Run(async () =>
            {
                var s = await ExportFileReader.ReadAsync(fileName, fileContent, ct).ConfigureAwait(false);
                var r = await ExportFileReader.ReadRawAsync(fileName, fileContent, ct).ConfigureAwait(false);
                return (s, r);
            }, ct).ConfigureAwait(true);

            DownloadStatus = "Выгрузка открыта в просмотрщике.";

            return new ExportViewerViewModel
            {
                JobId = SelectedJob.JobId,
                JobLabel = SelectedJob.Label,
                FileName = file.FileName,
                ContentType = file.ContentType,
                ScopeSummary = SelectedJobDetail.ScopeSummary,
                DownloadedAt = ChatPresentationFormatter.FormatTimestamp(DateTimeOffset.UtcNow),
                FileSize = ExportPresentationFormatter.FormatFileSize(file.Content.LongLength),
                ResultSummary = SelectedJobDetail.ResultSummary,
                StructuredContent = structured,
                RawJson = raw
            };
        }
        catch (Exception ex)
        {
            DownloadStatus = $"Не удалось открыть выгрузку: {AdminHostErrorFormatter.Describe(ex)}";
            return null;
        }
        finally
        {
            IsLoadingViewer = false;
        }
    }

    private void ClearDetailState(string status)
    {
        SelectedJobDetail = null;
        DetailStatus = status;
        DetailLastUpdated = "Никогда";
        DetailWarnings.Clear();
        DownloadStatus = "Файл ещё не загружался.";
        OnPropertyChanged(nameof(DetailWarningsHeadline));
    }

    private Task<DownloadedAdminExportFile> DownloadSelectedExportFileAsync(CancellationToken ct)
    {
        if (SelectedJob is null)
            throw new InvalidOperationException("Выберите задачу выгрузки.");

        return _adminHostClient.DownloadExportJobFileAsync(
            _connectionSettings.BaseUrl,
            _connectionSettings.ApiToken,
            _connectionSettings.RequestTimeout,
            SelectedJob.JobId,
            ct);
    }

}
