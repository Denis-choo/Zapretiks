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
    private readonly LocalLog log;
    private readonly SemaphoreSlim stateGate = new(1);
    private bool canSave;

    public Session(IUserStore store, IZapretInspector inspector, DesktopDialogs dialogs, LocalLog log)
        : this(store, inspector, dialogs, log, new FlowsealRuntimeInspector()) { }

    public Session(IUserStore store, IZapretInspector inspector, DesktopDialogs dialogs, LocalLog log,
        IZapretRuntimeInspector runtimeInspector)
    {
        this.store = store;
        this.inspector = inspector;
        this.runtimeInspector = runtimeInspector;
        this.log = log;
        Dialogs = dialogs;
        ChooseFolderCommand = Command(ChooseFolderAsync, () => canSave);
        RefreshStatusCommand = Command(RefreshIntegrationAsync,
            () => State.Settings.ZapretDirectory is not null && !IsCheckingStatus);
        RefreshIntegrationCommand = RefreshStatusCommand;
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
    public IReadOnlyList<string> RejectedFolderDiagnostics { get; private set; } = [];
    public string DataDirectory => store.DataDirectory;
    public ICommand ChooseFolderCommand { get; }
    public ICommand RefreshIntegrationCommand { get; }
    public ICommand RefreshStatusCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenUpstreamCommand { get; }
    public event Action? Changed;
    public event Action<string, NotificationKind>? Notification;

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
        var folder = Dialogs.ChooseFolder();
        if (folder is null)
            return false;

        await stateGate.WaitAsync();
        try
        {
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
