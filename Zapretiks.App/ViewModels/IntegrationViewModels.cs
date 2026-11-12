using System.Windows.Input;
using Zapretiks.Core;

namespace Zapretiks.App.ViewModels;

internal abstract class IntegrationViewModel : ViewModel
{
    private ZapretInstallation? observedInstallation;
    private bool savingStrategy;
    private bool refreshingStrategy;

    protected IntegrationViewModel(Session session)
    {
        Session = session;
        session.Changed += RefreshIntegration;
    }

    protected Session Session { get; }
    public ICommand ChooseFolderCommand => Session.ChooseFolderCommand;
    public bool HasConnectedInstallation => Session.Installation is { IsValid: true };
    public bool CanSelectStrategy => HasConnectedInstallation && Session.CanSave && !savingStrategy;
    public IReadOnlyList<string> Strategies { get; private set; } = [];

    public string? SelectedStrategy
    {
        get => Strategies.FirstOrDefault(strategy =>
            string.Equals(strategy, Session.State.Settings.StrategyFile, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (!refreshingStrategy && CanSelectStrategy && value is not null
                && value != SelectedStrategy && Strategies.Contains(value))
            {
                _ = SaveStrategyAsync(value);
            }
        }
    }

    public string IntegrationTitle => Session.Installation?.IsValid switch
    {
        true => "Папка подключена",
        false => "Нужна проверка папки",
        _ => "Папка не подключена"
    };

    public string IntegrationDetail => Session.Installation switch
    {
        { IsValid: true, Version: { } version } => $"Версия {version} · стратегий: {Session.Installation.Strategies.Count}",
        { IsValid: true } => $"Доступно стратегий: {Session.Installation.Strategies.Count}",
        { IsValid: false } => "Проверьте, что архив полностью распакован, или выберите другую папку.",
        _ => "Выберите папку распакованной сборки Flowseal."
    };

    private void RefreshIntegration()
    {
        Raise(nameof(IntegrationTitle));
        Raise(nameof(IntegrationDetail));
        Raise(nameof(HasConnectedInstallation));
        Raise(nameof(CanSelectStrategy));

        refreshingStrategy = true;
        try
        {
            if (!ReferenceEquals(Session.Installation, observedInstallation))
            {
                observedInstallation = Session.Installation;
                Strategies = observedInstallation is { IsValid: true }
                    ? observedInstallation.Strategies.Select(item => item.FileName).ToArray() : [];
                Raise(nameof(Strategies));
            }
            Raise(nameof(SelectedStrategy));
        }
        finally
        {
            refreshingStrategy = false;
        }
    }

    private async Task SaveStrategyAsync(string strategy)
    {
        var directory = Session.Installation?.Directory;
        savingStrategy = true;
        Raise(nameof(CanSelectStrategy));
        try
        {
            await Session.UpdateSettingsAsync(settings =>
                HasConnectedInstallation && Session.Installation?.Directory == directory && Strategies.Contains(strategy)
                    ? settings with { StrategyFile = strategy } : settings);
        }
        catch (Exception error)
        {
            Session.Report(error);
        }
        finally
        {
            savingStrategy = false;
            RefreshIntegration();
        }
    }
}

internal sealed class HomeViewModel : IntegrationViewModel
{
    public HomeViewModel(Session session, ICommand openCatalog, ICommand openLibrary, ICommand openSettings)
        : base(session)
    {
        OpenCatalogCommand = openCatalog;
        OpenLibraryCommand = openLibrary;
        OpenSettingsCommand = openSettings;
        session.Changed += () =>
        {
            Raise(nameof(InstalledCount));
            Raise(nameof(EnabledCount));
            Raise(nameof(RecentProfileTitles));
        };
    }

    public string StatusText => "Статус неизвестен";
    public string StatusDetail => "Управление запуском и остановкой пока недоступно.";
    public int InstalledCount => Session.State.Profiles.Length;
    public int EnabledCount => Session.State.Profiles.Count(item => item.IsEnabled);
    public IReadOnlyList<string> RecentProfileTitles => Session.State.Profiles.Reverse().Take(2)
        .Select(item => item.Profile.Name).ToArray();
    public ICommand OpenCatalogCommand { get; }
    public ICommand OpenLibraryCommand { get; }
    public ICommand OpenSettingsCommand { get; }
}

internal sealed class SettingsViewModel : IntegrationViewModel
{
    private bool refreshing;

    public SettingsViewModel(Session session, ICommand showOnboarding) : base(session)
    {
        ShowOnboardingCommand = showOnboarding;
        session.Changed += Refresh;
    }

    public string FolderDisplay => Session.State.Settings.ZapretDirectory ?? "Папка ещё не выбрана";
    public IReadOnlyList<string> Diagnostics => Session.RejectedFolderDiagnostics.Count > 0
        ? ["Последняя выбранная папка", .. Session.RejectedFolderDiagnostics]
        : Session.Installation?.Diagnostics ?? [];
    public string DataFolderDisplay => Session.DataDirectory;
    public string VersionText => "0.1.0";
    public ICommand RefreshIntegrationCommand => Session.RefreshIntegrationCommand;
    public ICommand OpenDataFolderCommand => Session.OpenDataFolderCommand;
    public ICommand OpenUpstreamCommand => Session.OpenUpstreamCommand;
    public ICommand ShowOnboardingCommand { get; }

    public bool AutoScanSteam
    {
        get => Session.State.Settings.AutoScanSteam;
        set
        {
            if (!refreshing && value != AutoScanSteam)
                _ = SaveAsync(settings => settings with { AutoScanSteam = value });
        }
    }

    private async Task SaveAsync(Func<UserSettings, UserSettings> update)
    {
        try
        {
            await Session.UpdateSettingsAsync(update);
        }
        catch (Exception error)
        {
            Session.Report(error);
            Refresh();
        }
    }

    private void Refresh()
    {
        refreshing = true;
        try
        {
            Raise(nameof(FolderDisplay));
            Raise(nameof(Diagnostics));
            Raise(nameof(AutoScanSteam));
        }
        finally
        {
            refreshing = false;
        }
    }
}
