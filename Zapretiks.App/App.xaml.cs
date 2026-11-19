using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using Zapretiks.App.ViewModels;
using Zapretiks.Infrastructure;

namespace Zapretiks.App;

public partial class App : Application
{
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
            new FlowsealRuntimeInspector());
        var shell = new ShellViewModel(services, new EmbeddedCatalogService(), new JsonProfileTransfer(),
            new FlowsealConfigurationPreview(), new SteamScanner());

        MainWindow = new MainWindow { DataContext = shell };
        MainWindow.Show();
        await shell.InitializeAsync();
    }
}
