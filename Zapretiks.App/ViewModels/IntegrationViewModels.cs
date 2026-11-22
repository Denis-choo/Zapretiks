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
    public bool CanSelectStrategy => HasConnectedInstallation && Session.CanSave && !savingStrategy && !Session.IsRuntimeBusy;
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
    public HomeViewModel(Session session, ICommand openCatalog, ICommand openLibrary, ICommand openSettings,
        ICommand createProfile, ICommand openPreview)
        : base(session)
    {
        OpenCatalogCommand = openCatalog;
        OpenLibraryCommand = openLibrary;
        OpenSettingsCommand = openSettings;
        CreateProfileCommand = createProfile;
        OpenPreviewCommand = openPreview;
        session.Changed += () =>
        {
            Raise(nameof(StatusText));
            Raise(nameof(StatusDetail));
            Raise(nameof(StatusCheckedAt));
            Raise(nameof(IsCheckingStatus));
            Raise(nameof(IsRunning));
            Raise(nameof(IsStatusWarning));
            Raise(nameof(HasManagedRun));
            Raise(nameof(ShowSwitch));
            Raise(nameof(FolderDisplay));
            Raise(nameof(InstalledCount));
            Raise(nameof(HasProfiles));
            Raise(nameof(HasConfiguredFolder));
            Raise(nameof(EnabledCount));
            Raise(nameof(RecentProfileTitles));
        };
    }

    public bool IsCheckingStatus => Session.IsCheckingStatus;
    public bool HasManagedRun => Session.ManagedRun is not null;
    public bool ShowSwitch => Session.ManagedRun is { } run && SelectedStrategy is { } selected &&
        !string.Equals(selected, run.StrategyFile, StringComparison.OrdinalIgnoreCase);
    public bool HasConfiguredFolder => Session.State.Settings.ZapretDirectory is not null;
    public bool IsRunning => !IsCheckingStatus && Session.RuntimeStatus.State == ZapretRuntimeState.Running;
    public bool IsStatusWarning => !IsCheckingStatus && (Session.RuntimeStatus.State is
        ZapretRuntimeState.Unknown or ZapretRuntimeState.Unavailable ||
        Session.RuntimeStatus.MatchingProcessCount > 1 || Session.RuntimeStatus.OtherProcessCount > 0 ||
        Session.RuntimeStatus.UnresolvedProcessCount > 0);
    public string StatusText => Session.RuntimeOperation ?? (IsCheckingStatus ? "Проверяем состояние…" : Session.RuntimeStatus.State switch
    {
        ZapretRuntimeState.Disconnected => "Подключите папку zapret",
        ZapretRuntimeState.Unavailable => "Проверьте папку zapret",
        ZapretRuntimeState.Running => HasManagedRun ? "Запущен" : "Запущен вне Zapretiks",
        ZapretRuntimeState.NotRunning => "Не запущен",
        _ => "Статус неизвестен"
    });
    public string StatusDetail
    {
        get
        {
            if (Session.RuntimeOperation is not null)
                return Session.RuntimeOperationStrategy ?? "";
            if (IsCheckingStatus)
                return "Проверяем файлы сборки и процессы winws.exe.";

            var status = Session.RuntimeStatus;
            var detail = status.State switch
            {
                ZapretRuntimeState.Disconnected => "Выберите папку сборки Flowseal для проверки её состояния.",
                ZapretRuntimeState.Unavailable => "Папка недоступна или в ней отсутствуют необходимые компоненты.",
                ZapretRuntimeState.Running when Session.ManagedRun is { } run =>
                    $"Работает {run.StrategyFile}. При закрытии Zapretiks эта стратегия остановится.",
                ZapretRuntimeState.Running => "Завершите внешний запуск zapret, чтобы управлять им здесь.",
                ZapretRuntimeState.NotRunning when status.OtherProcessCount > 0 =>
                    "Сначала остановите zapret в другой папке и обновите статус.",
                ZapretRuntimeState.NotRunning => "Запустите выбранную стратегию из вашей сборки Flowseal.",
                _ when status.AccessDeniedProcessCount > 0 || status.EnumerationAccessDenied =>
                    "Windows ограничила доступ к данным процессов. Принадлежность подключённой папке не подтверждена.",
                _ => "Не удалось подтвердить состояние процесса. Повторите проверку."
            };

            if (status.MatchingProcessCount > 1 && HasManagedRun)
                detail += " Из этой же папки работает ещё один процесс.";
            if (status.OtherProcessCount > 0 && status.State != ZapretRuntimeState.NotRunning)
                detail += $" Процессы winws.exe из других папок: {status.OtherProcessCount}.";
            if (status.State == ZapretRuntimeState.Running && status.UnresolvedProcessCount > 0)
                detail += $" Процессы с недоступными данными: {status.UnresolvedProcessCount}.";

            return detail;
        }
    }
    public string StatusCheckedAt => !IsCheckingStatus && Session.RuntimeStatus.CheckedAt is { } timestamp
        ? $"Проверено в {timestamp.ToLocalTime():HH:mm:ss}" : "";
    public string FolderDisplay => Session.Installation?.Directory ?? "";
    public ICommand RefreshStatusCommand => Session.RefreshStatusCommand;
    public ICommand StartCommand => Session.StartCommand;
    public ICommand StopCommand => Session.StopCommand;
    public ICommand SwitchCommand => Session.SwitchCommand;
    public ICommand RestartCommand => Session.RestartCommand;
    public int InstalledCount => Session.State.Profiles.Length;
    public bool HasProfiles => InstalledCount > 0;
    public int EnabledCount => Session.State.Profiles.Count(item => item.IsEnabled);
    public IReadOnlyList<string> RecentProfileTitles => Session.State.Profiles.Reverse().Take(2)
        .Select(item => item.Profile.Name).ToArray();
    public ICommand OpenCatalogCommand { get; }
    public ICommand OpenLibraryCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand CreateProfileCommand { get; }
    public ICommand OpenPreviewCommand { get; }
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
    public bool HasDiagnosis => Session.Installation is not null || Session.RejectedFolderDiagnostics.Count > 0;
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
            Raise(nameof(HasDiagnosis));
            Raise(nameof(AutoScanSteam));
        }
        finally
        {
            refreshing = false;
        }
    }
}
