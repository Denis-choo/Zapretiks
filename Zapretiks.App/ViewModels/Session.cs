using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Zapretiks.Core;
using Zapretiks.Infrastructure;

namespace Zapretiks.App.ViewModels;

internal enum NotificationKind { Information, Success, Warning, Error }

internal sealed class Session
{
    private readonly IUserStore store;
    private readonly IZapretInspector inspector;
    private readonly IZapretRuntimeInspector runtimeInspector;
    private readonly IZapretRuntimeController runtimeController;
    private readonly LocalLog log;
    private readonly SemaphoreSlim stateGate = new(1);
    private bool canSave;

    public Session(IUserStore store, IZapretInspector inspector, DesktopDialogs dialogs, LocalLog log)
        : this(store, inspector, dialogs, log, new FlowsealRuntimeInspector()) { }

    public Session(IUserStore store, IZapretInspector inspector, DesktopDialogs dialogs, LocalLog log,
        IZapretRuntimeInspector runtimeInspector)
        : this(store, inspector, dialogs, log, runtimeInspector, new FlowsealRuntimeController()) { }

    public Session(IUserStore store, IZapretInspector inspector, DesktopDialogs dialogs, LocalLog log,
        IZapretRuntimeInspector runtimeInspector, IZapretRuntimeController runtimeController)
    {
        this.store = store;
        this.inspector = inspector;
        this.runtimeInspector = runtimeInspector;
        this.runtimeController = runtimeController;
        this.log = log;
        Dialogs = dialogs;
        ChooseFolderCommand = Command(ChooseFolderAsync, () => CanChooseFolder);
        RefreshStatusCommand = Command(RefreshIntegrationAsync,
            () => State.Settings.ZapretDirectory is not null && !IsRuntimeBusy);
        RefreshIntegrationCommand = RefreshStatusCommand;
        StartCommand = Command(() => ControlRuntimeAsync(RuntimeAction.Start), () => CanStart);
        SwitchCommand = Command(() => ControlRuntimeAsync(RuntimeAction.Switch), () => CanSwitch);
        RestartCommand = Command(() => ControlRuntimeAsync(RuntimeAction.Restart), () => CanRestart);
        StopCommand = Command(() => ControlRuntimeAsync(RuntimeAction.Stop), () => CanStop);
        OpenDataFolderCommand = Action(() =>
        {
            Directory.CreateDirectory(DataDirectory);
            OpenWithShell(DataDirectory);
        });
        OpenUpstreamCommand = Action(() => OpenWithShell("https://github.com/Flowseal/zapret-discord-youtube"));
    }

    public DesktopDialogs Dialogs { get; }
    public bool CanSave => canSave;
    public UserState State { get; private set; } = UserState.Empty;
    public ZapretInstallation? Installation { get; private set; }
    public ZapretRuntimeStatus RuntimeStatus { get; private set; } = new(ZapretRuntimeState.Disconnected);
    public bool IsCheckingStatus { get; private set; }
    public ManagedZapretRun? ManagedRun { get; private set; }
    public string? RuntimeOperation { get; private set; }
    public string? RuntimeOperationStrategy { get; private set; }
    public bool IsRuntimeBusy => IsCheckingStatus || RuntimeOperation is not null;
    public bool CanChooseFolder => canSave && !IsRuntimeBusy && ManagedRun is null;
    private bool HasStrategy(string? file) => Installation is { IsValid: true } install &&
        install.Strategies.Any(strategy => StringComparer.OrdinalIgnoreCase.Equals(strategy.FileName, file));
    private bool CanStart => !IsRuntimeBusy && ManagedRun is null && HasStrategy(State.Settings.StrategyFile) &&
        RuntimeStatus is { State: ZapretRuntimeState.NotRunning, OtherProcessCount: 0, UnresolvedProcessCount: 0 };
    private bool CanChangeRun => !IsRuntimeBusy &&
        RuntimeStatus is { State: ZapretRuntimeState.Running, MatchingProcessCount: 1, OtherProcessCount: 0, UnresolvedProcessCount: 0 };
    private bool CanSwitch => CanChangeRun && ManagedRun is { } run && HasStrategy(State.Settings.StrategyFile) &&
        !StringComparer.OrdinalIgnoreCase.Equals(run.StrategyFile, State.Settings.StrategyFile);
    private bool CanRestart => CanChangeRun && ManagedRun is { } run && HasStrategy(run.StrategyFile);
    private bool CanStop => !IsRuntimeBusy && ManagedRun is not null;
    public IReadOnlyList<string> RejectedFolderDiagnostics { get; private set; } = [];
    public string DataDirectory => store.DataDirectory;
    public ICommand ChooseFolderCommand { get; }
    public ICommand RefreshIntegrationCommand { get; }
    public ICommand RefreshStatusCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand SwitchCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenUpstreamCommand { get; }
    public event Action? Changed;
    public event Action<string, NotificationKind>? Notification;

    public void CheckManagedRun()
    {
        if (ManagedRun is not null && !IsRuntimeBusy && runtimeController.Current is null &&
            RefreshStatusCommand.CanExecute(null))
            RefreshStatusCommand.Execute(null);
    }

    public async Task InitializeAsync()
    {
        try
        {
            State = await store.LoadAsync();
            canSave = true;
        }
        catch (Exception error)
        {
            log.Write(error);
            Notify("Не удалось загрузить локальные данные. Подробности есть в журнале.", NotificationKind.Error);
        }

        if (State.Settings.ZapretDirectory is not null)
        {
            try
            {
                await RefreshIntegrationAsync();
            }
            catch (Exception error)
            {
                Report(error);
            }
        }
        Publish();
    }

    public ICommand Command(Func<Task> run, Func<bool>? canRun = null) =>
        new UiCommand(_ => run(), Report, canRun is null ? null : _ => canRun());

    public ICommand Command(Func<object?, Task> run, Predicate<object?>? canRun = null) =>
        new UiCommand(run, Report, canRun);

    public ICommand Action(Action run) => Command(() =>
    {
        run();
        return Task.CompletedTask;
    });

    public Task UpdateSettingsAsync(Func<UserSettings, UserSettings> update) =>
        UpdateAsync(state => state with { Settings = update(state.Settings) });

    public async Task UpdateAsync(Func<UserState, UserState> update)
    {
        await stateGate.WaitAsync();
        try
        {
            var previousDirectory = State.Settings.ZapretDirectory;
            await CommitAsync(update(State), publish: false);
            if (!StringComparer.OrdinalIgnoreCase.Equals(previousDirectory, State.Settings.ZapretDirectory))
                await CheckIntegrationAsync();
            else
                Publish();
        }
        finally
        {
            stateGate.Release();
        }
    }

    public void Notify(string message, NotificationKind kind = NotificationKind.Information) => Notification?.Invoke(message, kind);

    public void Report(Exception error)
    {
        log.Write(error);
        Notify(error switch
        {
            InvalidDataException or InvalidOperationException or ArgumentException => error.Message,
            UnauthorizedAccessException => "Windows не разрешила доступ к выбранному файлу или папке.",
            IOException => "Не удалось прочитать или сохранить файл. Проверьте, что он доступен.",
            _ => "Операция не завершилась. Подробности есть в журнале приложения."
        }, NotificationKind.Error);
    }

    public async Task<bool> ChooseFolderAsync()
    {
        if (!CanChooseFolder)
            return false;
        var folder = Dialogs.ChooseFolder();
        if (folder is null)
            return false;

        await stateGate.WaitAsync();
        try
        {
            if (runtimeController.Current is not null)
            {
                Notify("Сначала остановите zapret в разделе «Обзор».", NotificationKind.Warning);
                return false;
            }
            var result = await inspector.InspectAsync(folder);
            if (!result.IsValid)
            {
                RejectedFolderDiagnostics = result.Diagnostics;
                Notify($"Папка Flowseal не подключена. {result.Diagnostics.FirstOrDefault()}", NotificationKind.Warning);
                Publish();
                return false;
            }

            var strategy = result.Strategies.FirstOrDefault(item =>
                string.Equals(item.FileName, State.Settings.StrategyFile, StringComparison.OrdinalIgnoreCase))
                ?? result.Strategies.FirstOrDefault(item => item.FileName.Equals("general.bat", StringComparison.OrdinalIgnoreCase))
                ?? result.Strategies.FirstOrDefault();

            var next = State with
            {
                Settings = State.Settings with
                {
                    ZapretDirectory = result.Directory,
                    StrategyFile = strategy?.FileName,
                    OnboardingCompleted = true
                }
            };
            await CommitAsync(next, publish: false);
            RejectedFolderDiagnostics = [];
            await CheckIntegrationAsync(result);
            Notify("Папка Flowseal подключена.", NotificationKind.Success);
            return true;
        }
        finally
        {
            stateGate.Release();
        }
    }

    public async Task RunExclusiveAsync(Func<Task> operation)
    {
        await stateGate.WaitAsync();
        try { await operation(); }
        finally { stateGate.Release(); }
    }

    private enum RuntimeAction { Start, Switch, Restart, Stop }

    private async Task ControlRuntimeAsync(RuntimeAction action)
    {
        // Capture the user's choice before queued changes to the folder or strategy are saved.
        var directory = Installation?.Directory;
        var strategy = State.Settings.StrategyFile;
        var run = ManagedRun;
        await RunExclusiveAsync(async () =>
        {
            var unchangedSelection = StringComparer.OrdinalIgnoreCase.Equals(directory, Installation?.Directory) &&
                StringComparer.OrdinalIgnoreCase.Equals(strategy, State.Settings.StrategyFile);
            var sameRun = ReferenceEquals(run, ManagedRun);
            var canRun = action switch
            {
                RuntimeAction.Start => CanStart && unchangedSelection,
                RuntimeAction.Switch => CanSwitch && unchangedSelection && sameRun,
                RuntimeAction.Restart => CanRestart && sameRun,
                _ => CanStop && sameRun
            };
            if (!canRun)
            {
                Notify("Стратегия или состояние zapret изменились. Проверьте статус и повторите действие.", NotificationKind.Warning);
                return;
            }
            RuntimeOperationStrategy = action is RuntimeAction.Start or RuntimeAction.Switch ? strategy : run?.StrategyFile;
            RuntimeOperation = action switch
            {
                RuntimeAction.Start => "Запускаем zapret…",
                RuntimeAction.Switch => "Меняем стратегию…",
                RuntimeAction.Restart => "Перезапускаем zapret…",
                _ => "Останавливаем zapret…"
            };
            Publish();
            try
            {
                var result = action switch
                {
                    RuntimeAction.Start => await runtimeController.StartAsync(Installation!, strategy!),
                    RuntimeAction.Switch => await runtimeController.SwitchAsync(Installation!, strategy!),
                    RuntimeAction.Restart => await runtimeController.RestartAsync(Installation!, run!),
                    _ => await runtimeController.StopAsync()
                };
                Notify(result.Message, result.Succeeded ? NotificationKind.Success : NotificationKind.Warning);
            }
            finally
            {
                RuntimeOperation = null;
                RuntimeOperationStrategy = null;
                await CheckIntegrationAsync();
            }
        });
    }

    private async Task RefreshIntegrationAsync()
    {
        await stateGate.WaitAsync();
        try
        {
            RejectedFolderDiagnostics = [];
            await CheckIntegrationAsync();
        }
        finally
        {
            stateGate.Release();
        }
    }

    private async Task CheckIntegrationAsync(ZapretInstallation? inspectedInstallation = null)
    {
        IsCheckingStatus = true;
        RuntimeStatus = new(ZapretRuntimeState.Unknown);
        Publish();
        try
        {
            Installation = inspectedInstallation ?? (State.Settings.ZapretDirectory is { } directory
                ? await inspector.InspectAsync(directory) : null);
            RuntimeStatus = await runtimeInspector.InspectAsync(Installation);
        }
        catch
        {
            RuntimeStatus = new(ZapretRuntimeState.Unknown, CheckedAt: DateTimeOffset.Now);
            Notify("Не удалось проверить состояние zapret. Повторите проверку.", NotificationKind.Warning);
        }
        finally
        {
            ManagedRun = runtimeController.Current;
            IsCheckingStatus = false;
            Publish();
        }
    }

    private async Task CommitAsync(UserState next, bool publish = true)
    {
        if (!canSave)
            throw new InvalidOperationException("Локальные данные недоступны для сохранения. Проверьте папку данных в настройках.");
        if (next == State)
            return;

        await store.SaveAsync(next);
        if (!StringComparer.OrdinalIgnoreCase.Equals(State.Settings.ZapretDirectory, next.Settings.ZapretDirectory))
            Installation = null;
        State = next;
        if (publish)
            Publish();
    }

    private void Publish()
    {
        Changed?.Invoke();
        CommandManager.InvalidateRequerySuggested();
    }

    private static void OpenWithShell(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
