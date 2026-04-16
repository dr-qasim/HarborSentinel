using HarborSentinel.AdminConsole.Infrastructure;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace HarborSentinel.AdminConsole.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly DashboardViewModel _dashboard;
    private NavigationItemViewModel? _selectedNavigationItem;
    private PageViewModel? _currentPage;
    private readonly IncidentsViewModel _incidents;
    private readonly NavigationItemViewModel _incidentsNavigationItem;

    public MainWindowViewModel(
        DashboardViewModel dashboard,
        ChatsViewModel chats,
        UsersViewModel users,
        IncidentsViewModel incidents,
        ExportsViewModel exports,
        NodeStatusViewModel nodeStatus)
    {
        _dashboard = dashboard;
        _incidents = incidents;
        _incidents.PropertyChanged += OnIncidentsPropertyChanged;

        _incidentsNavigationItem = new NavigationItemViewModel
        {
            Label = incidents.NavigationLabel,
            Caption = "Durable inbox инцидентов, риск и таймлайн",
            Page = incidents
        };

        NavigationItems =
        [
            new NavigationItemViewModel
            {
                Label = "Обзор",
                Caption = "Подключение и состояние системы",
                Page = dashboard
            },
            new NavigationItemViewModel
            {
                Label = "Чаты",
                Caption = "Реестр чатов и область текущего бота",
                Page = chats
            },
            new NavigationItemViewModel
            {
                Label = "Пользователи",
                Caption = "Пользователи и private-start",
                Page = users
            },
            _incidentsNavigationItem,
            new NavigationItemViewModel
            {
                Label = "Выгрузки",
                Caption = "Серверные задачи и файлы",
                Page = exports
            },
            new NavigationItemViewModel
            {
                Label = "Состояние узла",
                Caption = "Диагностика узла и runtime",
                Page = nodeStatus
            }
        ];

        SelectedNavigationItem = NavigationItems[0];
    }

    public string ShellTitle => "Операторская консоль";
    public string ShellSubtitle => "Внутренний read-only клиент для обзора чатов, пользователей, инцидентов и выгрузок через AdminHost";
    public string ReadOnlyModeText => "Режим: только чтение";
    public string IncidentStatusText => _incidents.IncidentStatusBarText;
    public string StatusText => SelectedNavigationItem is null
        ? "Готово."
        : $"{SelectedNavigationItem.Label}: {SelectedNavigationItem.Caption}";

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }

    public NavigationItemViewModel? SelectedNavigationItem
    {
        get => _selectedNavigationItem;
        set
        {
            if (!SetProperty(ref _selectedNavigationItem, value))
                return;

            CurrentPage = value?.Page;
            OnPropertyChanged(nameof(StatusText));
            _ = InitializePageAsync(CurrentPage);
        }
    }

    public PageViewModel? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public async Task InitializeAsync()
    {
        await _dashboard.InitializeAsync().ConfigureAwait(true);
        await _incidents.StartMonitoringAsync().ConfigureAwait(true);

        if (!ReferenceEquals(CurrentPage, _dashboard))
            await InitializePageAsync(CurrentPage).ConfigureAwait(true);
    }

    private void OnIncidentsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IncidentsViewModel.NavigationLabel))
        {
            _incidentsNavigationItem.Label = _incidents.NavigationLabel;
            OnPropertyChanged(nameof(StatusText));
        }

        if (e.PropertyName == nameof(IncidentsViewModel.IncidentStatusBarText))
            OnPropertyChanged(nameof(IncidentStatusText));
    }

    private static async Task InitializePageAsync(PageViewModel? page)
    {
        if (page is IAsyncPageInitializable initializable)
            await initializable.InitializeAsync().ConfigureAwait(true);
    }
}
