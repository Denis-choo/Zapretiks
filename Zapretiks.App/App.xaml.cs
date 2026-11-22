using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Zapretiks.App.ViewModels;
using Zapretiks.Infrastructure;

namespace Zapretiks.App;

public partial class App : Application
{
    private readonly FlowsealRuntimeController runtimeController = new();
    private DispatcherTimer? runtimeMonitor;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

        string? dataDirectory = null;
#if DEBUG
        dataDirectory = Environment.GetEnvironmentVariable("ZAPRETIKS_TEST_DATA");
#endif
        var store = new JsonUserStore(dataDirectory);
        var services = new Session(store, new FlowsealInspector(), new DesktopDialogs(), new LocalLog(store.DataDirectory),
            new FlowsealRuntimeInspector(), runtimeController);
        var shell = new ShellViewModel(services, new EmbeddedCatalogService(), new JsonProfileTransfer(),
            new FlowsealConfigurationEditor(store.DataDirectory), new SteamScanner());

        MainWindow = new MainWindow { DataContext = shell };
        MainWindow.Show();
        await shell.InitializeAsync();
        runtimeMonitor = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        runtimeMonitor.Tick += (_, _) => services.CheckManagedRun();
        runtimeMonitor.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        runtimeMonitor?.Stop();
        runtimeController.Dispose();
        base.OnExit(e);
    }
}
