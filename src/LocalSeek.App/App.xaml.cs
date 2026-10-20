using LocalSeek.App.Services;
using LocalSeek.App.ViewModels;
using LocalSeek.App.Views;
using LocalSeek.Indexing.FileSystem;
using LocalSeek.Indexing.Settings;
using LocalSeek.Indexing.Watching;
using Microsoft.UI.Dispatching;
using Uno.Resizetizer;

namespace LocalSeek.App;

public partial class App : Application
{
    private AppHost? _host;

    /// <summary>
    /// Initializes the singleton application object. This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        this.InitializeComponent();
    }

    protected Window? MainWindow { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new Window { Title = "LocalSeek 本機全文檢索台" };
#if DEBUG
        MainWindow.UseStudio();
#endif

        SettingsView.WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow);
        _host = new AppHost(
            AppPaths.FromEnvironment(),
            new PhysicalSourceFileSystem(),
            new PhysicalFileWatcherFactory(),
            TimeProvider.System,
            new DispatcherQueueDispatcher(DispatcherQueue.GetForCurrentThread()),
            new ShellService(),
            TimeZoneInfo.Local);

        MainWindow.Content = new MainPage(_host.Main);
        MainWindow.Closed += OnMainWindowClosed;
        _host.Start();

        MainWindow.SetWindowIcon();
        MainWindow.Activate();
    }

    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        // Stop indexing and close index files; committed data is already durable.
        _host?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _host = null;
    }

    /// <summary>
    /// Configures global Uno Platform logging
    /// </summary>
    public static void InitializeLogging()
    {
#if DEBUG
        // Logging is disabled by default for release builds, as it incurs a significant
        // initialization cost from Microsoft.Extensions.Logging setup.
        var factory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddFilter("Uno", LogLevel.Warning);
            builder.AddFilter("Windows", LogLevel.Warning);
            builder.AddFilter("Microsoft", LogLevel.Warning);
        });

        global::Uno.Extensions.LogExtensionPoint.AmbientLoggerFactory = factory;

#if HAS_UNO
        global::Uno.UI.Adapter.Microsoft.Extensions.Logging.LoggingAdapter.Initialize();
#endif
#endif
    }
}
