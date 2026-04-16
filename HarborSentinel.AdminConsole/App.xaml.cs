using HarborSentinel.AdminConsole.Options;
using HarborSentinel.AdminConsole.Services;
using HarborSentinel.AdminConsole.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Windows;

namespace HarborSentinel.AdminConsole;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((context, config) =>
            {
                config.SetBasePath(AppContext.BaseDirectory);
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            })
            .ConfigureServices((context, services) =>
            {
                services.Configure<AdminConsoleOptions>(context.Configuration.GetSection("AdminConsole"));
                services.AddSingleton<AdminConnectionSettings>();
                services.AddSingleton<IAdminConsoleStoragePaths, AdminConsoleStoragePaths>();
                services.AddSingleton<ISshTunnelProfileStore, SshTunnelProfileStore>();
                services.AddSingleton<IConnectionSettingsStore, ConnectionSettingsStore>();
                services.AddSingleton<ITunnelSecretStore, DpapiTunnelSecretStore>();
                services.AddSingleton<ISshTunnelManager, SshTunnelManager>();
                services.AddHttpClient<IAdminHostClient, AdminHostClient>();
                services.AddSingleton<IExportFileSaveService, ExportFileSaveService>();
                services.AddSingleton<DashboardViewModel>();
                services.AddSingleton<ChatsViewModel>();
                services.AddSingleton<IncidentsViewModel>();
                services.AddSingleton<UsersViewModel>();
                services.AddSingleton<ExportsViewModel>();
                services.AddSingleton<NodeStatusViewModel>();
                services.AddSingleton<MainWindowViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync().ConfigureAwait(true);

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
            _host.Dispose();
        }

        base.OnExit(e);
    }
}
