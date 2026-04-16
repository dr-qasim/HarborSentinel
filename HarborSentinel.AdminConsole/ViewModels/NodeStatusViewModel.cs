using HarborSentinel.AdminConsole.Infrastructure;
using HarborSentinel.AdminConsole.Services;
using HarborSentinel.AdminContracts.Diagnostics;
using System.Collections.ObjectModel;
using System.Globalization;

namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class NodeStatusViewModel : PageViewModel, IAsyncPageInitializable
{
    private readonly IAdminHostClient _adminHostClient;
    private readonly AdminConnectionSettings _connectionSettings;
    private readonly AsyncRelayCommand _refreshCommand;

    private bool _hasLoadedOnce;
    private string _connectionStatus = "Диагностика узла ещё не запрашивалась.";
    private string _nodeSummary = "AdminHost, runtime DB и schema mode будут показаны после первого запроса.";
    private string _securitySummary = "Ожидается localhost-only AdminHost c token-based auth.";
    private string _schemaSummary = "Schema mode ещё не определён.";
    private string _issueSummary = "Последние проблемы ещё не загружены.";
    private string _lastUpdated = "Никогда";

    public NodeStatusViewModel(IAdminHostClient adminHostClient, AdminConnectionSettings connectionSettings)
        : base("Состояние узла", "Диагностика узла, runtime-хранилищ, схемы и degraded-state")
    {
        _adminHostClient = adminHostClient;
        _connectionSettings = connectionSettings;
        _refreshCommand = new AsyncRelayCommand(RefreshAsync);

        Components = [];
        Paths = [];
        RuntimeTables = [];
        Capabilities = [];
        RecentIssues = [];
    }

    public ObservableCollection<NodeComponentItemViewModel> Components { get; }
    public ObservableCollection<NodePathItemViewModel> Paths { get; }
    public ObservableCollection<NodeTableItemViewModel> RuntimeTables { get; }
    public ObservableCollection<NodeCapabilityItemViewModel> Capabilities { get; }
    public ObservableCollection<NodeIssueItemViewModel> RecentIssues { get; }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    public string NodeSummary
    {
        get => _nodeSummary;
        private set => SetProperty(ref _nodeSummary, value);
    }

    public string SecuritySummary
    {
        get => _securitySummary;
        private set => SetProperty(ref _securitySummary, value);
    }

    public string SchemaSummary
    {
        get => _schemaSummary;
        private set => SetProperty(ref _schemaSummary, value);
    }

    public string IssueSummary
    {
        get => _issueSummary;
        private set => SetProperty(ref _issueSummary, value);
    }

    public string LastUpdated
    {
        get => _lastUpdated;
        private set => SetProperty(ref _lastUpdated, value);
    }

    public AsyncRelayCommand RefreshCommand => _refreshCommand;

    public async Task InitializeAsync()
    {
        if (!_hasLoadedOnce)
            await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RefreshAsync()
    {
        ConnectionStatus = "Запрашивается diagnostics snapshot узла...";

        try
        {
            var response = await _adminHostClient
                .GetNodeStatusAsync(
                    _connectionSettings.BaseUrl,
                    _connectionSettings.ApiToken,
                    _connectionSettings.RequestTimeout,
                    CancellationToken.None)
                .ConfigureAwait(true);

            ReplaceCollection(Components, response.Components.Select(static item => new NodeComponentItemViewModel(item)));
            ReplaceCollection(Paths, response.Paths.Select(static item => new NodePathItemViewModel(item)));
            ReplaceCollection(RuntimeTables, response.RuntimeTables.Select(static item => new NodeTableItemViewModel(item)));
            ReplaceCollection(Capabilities, response.Capabilities.Select(static item => new NodeCapabilityItemViewModel(item)));
            ReplaceCollection(RecentIssues, response.RecentIssues.Select(static item => new NodeIssueItemViewModel(item)));

            NodeSummary = $"{response.Product} | версия {response.Version} | {TranslateEnvironment(response.Environment)} | {response.Runtime}";
            SecuritySummary = $"{response.ListenAddress} | localhost-only: {ToYesNo(response.LocalhostOnly)} | token required: {ToYesNo(response.ApiTokenRequired)}";
            SchemaSummary = $"Schema: {TranslateSchemaMode(response.SchemaMode)} | runtime DB: {TranslateReadable(response.RuntimeDatabaseReadable)} | warning: {response.WarningCount} | critical: {response.CriticalCount}";
            IssueSummary = RecentIssues.Count == 0
                ? "Последние проблемы: нет серверных warning/error событий."
                : $"Последние проблемы: {RecentIssues.Count}. Смотрите curated issue feed ниже.";
            LastUpdated = response.GeneratedAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
            ConnectionStatus = response.CriticalCount == 0
                ? "Состояние узла обновлено."
                : "Состояние узла обновлено, но есть критические проблемы.";
            _hasLoadedOnce = true;
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Не удалось получить состояние узла: {AdminHostErrorFormatter.Describe(ex)}";
            NodeSummary = "Diagnostics snapshot недоступен.";
            SecuritySummary = "Проверьте SSH tunnel, BaseUrl и токен AdminHost.";
            SchemaSummary = "Schema mode не определён из-за ошибки запроса.";
            IssueSummary = "Server-side diagnostics issue feed не был загружен.";
            LastUpdated = "Никогда";
            Components.Clear();
            Paths.Clear();
            RuntimeTables.Clear();
            Capabilities.Clear();
            RecentIssues.Clear();
        }
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    private static string TranslateEnvironment(string environmentName)
        => string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase) ? "Production" : environmentName;

    private static string TranslateSchemaMode(AdminRuntimeSchemaModeDto value)
        => value switch
        {
            AdminRuntimeSchemaModeDto.Full => "полная",
            AdminRuntimeSchemaModeDto.Partial => "частичная",
            AdminRuntimeSchemaModeDto.Legacy => "legacy",
            _ => "не определена"
        };

    private static string TranslateReadable(bool value) => value ? "читается" : "не читается";

    private static string ToYesNo(bool value) => value ? "да" : "нет";

    private static string TranslateSeverity(AdminDiagnosticSeverityDto value)
        => value switch
        {
            AdminDiagnosticSeverityDto.Healthy => "OK",
            AdminDiagnosticSeverityDto.Info => "Инфо",
            AdminDiagnosticSeverityDto.Warning => "Warning",
            AdminDiagnosticSeverityDto.Critical => "Critical",
            _ => "Unavailable"
        };

    public sealed class NodeComponentItemViewModel
    {
        public NodeComponentItemViewModel(AdminNodeComponentStatusDto dto)
        {
            Label = dto.Label;
            Severity = TranslateSeverity(dto.Severity);
            Summary = dto.Summary;
            Details = dto.Details ?? string.Empty;
        }

        public string Label { get; }
        public string Severity { get; }
        public string Summary { get; }
        public string Details { get; }
    }

    public sealed class NodePathItemViewModel
    {
        public NodePathItemViewModel(AdminNodePathStatusDto dto)
        {
            Label = dto.Label;
            Severity = TranslateSeverity(dto.Severity);
            Kind = dto.Kind;
            FullPath = dto.FullPath;
            Access = BuildAccess(dto);
            Summary = dto.Summary;
        }

        public string Label { get; }
        public string Severity { get; }
        public string Kind { get; }
        public string FullPath { get; }
        public string Access { get; }
        public string Summary { get; }

        private static string BuildAccess(AdminNodePathStatusDto dto)
        {
            var writePart = dto.Writable.HasValue ? $", write: {ToYesNo(dto.Writable.Value)}" : string.Empty;
            return $"exists: {ToYesNo(dto.Exists)}, read: {ToYesNo(dto.Readable)}{writePart}";
        }
    }

    public sealed class NodeTableItemViewModel
    {
        public NodeTableItemViewModel(AdminRuntimeTableStatusDto dto)
        {
            TableName = dto.TableName;
            Severity = TranslateSeverity(dto.Severity);
            Exists = ToYesNo(dto.Exists);
            MissingColumns = dto.MissingColumns.Count == 0 ? "нет" : string.Join(", ", dto.MissingColumns);
            Summary = dto.Summary;
        }

        public string TableName { get; }
        public string Severity { get; }
        public string Exists { get; }
        public string MissingColumns { get; }
        public string Summary { get; }
    }

    public sealed class NodeCapabilityItemViewModel
    {
        public NodeCapabilityItemViewModel(AdminNodeCapabilityStatusDto dto)
        {
            Label = dto.Label;
            Severity = TranslateSeverity(dto.Severity);
            Summary = dto.Summary;
            Impact = dto.Impact;
        }

        public string Label { get; }
        public string Severity { get; }
        public string Summary { get; }
        public string Impact { get; }
    }

    public sealed class NodeIssueItemViewModel
    {
        public NodeIssueItemViewModel(AdminDiagnosticEventDto dto)
        {
            Timestamp = dto.TimestampUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
            Severity = TranslateSeverity(dto.Severity);
            Source = dto.Source;
            Message = dto.Message;
        }

        public string Timestamp { get; }
        public string Severity { get; }
        public string Source { get; }
        public string Message { get; }
    }
}
