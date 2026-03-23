using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Navigation;

namespace HarborSentinel.Viewer
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window window = Window.Current;

        public static IntPtr MainWindowHandle { get; private set; }

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            window ??= new Window();

            if (window.Content is not Frame rootFrame)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                window.Content = rootFrame;
            }

            _ = rootFrame.Navigate(typeof(MainPage), e.Arguments);
            window.Activate();
            MainWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            TryStartMaximized(MainWindowHandle);
        }

        /// <summary>
        /// Invoked when Navigation to a certain page fails
        /// </summary>
        /// <param name="sender">The Frame which failed navigation</param>
        /// <param name="e">Details about the navigation failure</param>
        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private static void TryStartMaximized(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                if (appWindow is null)
                {
                    return;
                }

                if (appWindow.Presenter is not OverlappedPresenter)
                {
                    appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                }

                if (appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.Maximize();
                }
            }
            catch
            {
            }
        }
    }
}
